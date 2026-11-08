using System.Text.Json;
using ClaudeDeck.Core.Model;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Engine;

/// <summary>
/// Machine à états des sessions Claude Code, alimentée par les hooks HTTP et la status line.
/// Thread-safe : toutes les mutations passent par un verrou unique.
/// </summary>
public sealed class SessionTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Outils qui, par nature, attendent une réponse de l'utilisateur.</summary>
    private static readonly HashSet<string> UserFacingTools = new(StringComparer.Ordinal)
    {
        "AskUserQuestion",
        "ExitPlanMode",
    };

    public SessionTracker(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Délai après lequel une session « terminée » non consultée redevient « au repos ». Zéro = jamais.</summary>
    public TimeSpan DoneFadeAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Délai sans aucun événement après lequel une session est oubliée.</summary>
    public TimeSpan ForgetAfter { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Levé (hors verrou) à chaque changement visible.</summary>
    public event Action? Changed;

    public IReadOnlyList<SessionSnapshot> Snapshot()
    {
        lock (_gate) return _sessions.Values.Select(s => s.Snapshot()).ToList();
    }

    public int Count
    {
        get { lock (_gate) return _sessions.Count; }
    }

    /// <summary>Traite le JSON d'entrée d'un hook Claude Code.</summary>
    public void HandleHook(JsonElement payload, ClientProcess? client = null)
    {
        var sessionId = payload.Str("session_id");
        var eventName = payload.Str("hook_event_name");
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(eventName)) return;

        var now = _time.GetUtcNow();
        bool changed;
        lock (_gate)
        {
            if (eventName == "SessionEnd")
            {
                changed = _sessions.Remove(sessionId);
            }
            else
            {
                if (!_sessions.TryGetValue(sessionId, out var s))
                {
                    s = new Session(sessionId, now);
                    _sessions[sessionId] = s;
                }

                s.LastEventAt = now;
                var cwd = payload.Str("cwd");
                if (!string.IsNullOrEmpty(cwd)) s.Cwd = cwd;
                if (client is not null && (s.Client is null || s.Client.Pid != client.Pid)) s.Client = client;

                Apply(s, eventName, payload, now);
                changed = true;
            }
        }

        if (changed) Changed?.Invoke();
    }

    private static void Apply(Session s, string eventName, JsonElement p, DateTimeOffset now)
    {
        switch (eventName)
        {
            case "SessionStart":
                // Une reprise ou un /clear repart au repos ; un démarrage après compaction aussi.
                SetState(s, SessionState.Idle, now);
                s.CurrentTool = null;
                s.ActiveSubagents = 0;
                break;

            case "UserPromptSubmit":
                SetState(s, SessionState.Working, now);
                s.TurnStartedAt = now;
                s.CurrentTool = null;
                break;

            case "PreToolUse":
            {
                var tool = p.Str("tool_name");
                s.CurrentTool = tool;
                if (tool is not null) s.LastTool = tool;
                if (tool is not null && UserFacingTools.Contains(tool))
                    SetState(s, SessionState.Waiting, now, WaitReason.Question);
                else
                    SetState(s, SessionState.Working, now);
                s.TurnStartedAt ??= now;
                break;
            }

            case "PermissionRequest":
            {
                var tool = p.Str("tool_name");
                if (tool is not null) s.CurrentTool = s.LastTool = tool;
                SetState(s, SessionState.Waiting, now, WaitReason.Permission);
                break;
            }

            case "PostToolUse":
            case "PostToolUseFailure":
            case "PermissionDenied":
                s.CurrentTool = null;
                SetState(s, SessionState.Working, now);
                s.TurnStartedAt ??= now;
                break;

            case "Notification":
                switch (p.Str("notification_type"))
                {
                    case "permission_prompt":
                        SetState(s, SessionState.Waiting, now, WaitReason.Permission);
                        break;
                    case "elicitation_dialog":
                        SetState(s, SessionState.Waiting, now, WaitReason.Question);
                        break;
                    case "idle_prompt":
                        // Claude attend une saisie : si l'on croyait qu'il travaillait, le tour a été
                        // interrompu (Échap) sans événement Stop.
                        if (s.State is SessionState.Working or SessionState.Compacting)
                        {
                            EndTurn(s, now);
                            SetState(s, SessionState.Idle, now);
                        }
                        break;
                }
                break;

            case "Stop":
                EndTurn(s, now);
                SetState(s, SessionState.Done, now);
                break;

            case "StopFailure":
                EndTurn(s, now);
                SetState(s, SessionState.Error, now);
                break;

            case "SubagentStart":
                s.ActiveSubagents++;
                break;

            case "SubagentStop":
                s.ActiveSubagents = Math.Max(0, s.ActiveSubagents - 1);
                break;

            case "PreCompact":
                SetState(s, SessionState.Compacting, now);
                break;

            case "PostCompact":
                if (s.State == SessionState.Compacting)
                    SetState(s, s.TurnStartedAt is null ? SessionState.Idle : SessionState.Working, now);
                break;
        }
    }

    private static void EndTurn(Session s, DateTimeOffset now)
    {
        if (s.TurnStartedAt is { } start) s.LastTurnDuration = now - start;
        s.TurnStartedAt = null;
        s.CurrentTool = null;
    }

    private static void SetState(Session s, SessionState state, DateTimeOffset now, WaitReason reason = WaitReason.None)
    {
        if (s.State != state || s.WaitReason != reason) s.StateSince = now;
        s.State = state;
        s.WaitReason = state == SessionState.Waiting ? reason : WaitReason.None;
    }

    /// <summary>Met à jour une session à partir du JSON reçu par la status line (modèle, contexte, coût).</summary>
    public void HandleStatusLine(JsonElement payload)
    {
        var sessionId = payload.Str("session_id");
        if (string.IsNullOrEmpty(sessionId)) return;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
            {
                // La status line peut précéder le premier hook (ex. : daemon démarré en cours de session).
                s = new Session(sessionId, now);
                _sessions[sessionId] = s;
            }

            var ws = payload.Obj("workspace");
            s.Cwd = ws?.Str("current_dir") ?? payload.Str("cwd") ?? s.Cwd;
            s.ProjectDir = ws?.Str("project_dir") ?? s.ProjectDir;
            s.SessionName = payload.Str("session_name") ?? s.SessionName;
            s.Model = payload.Obj("model")?.Str("display_name") ?? s.Model;
            s.ContextUsedPercent = payload.Obj("context_window")?.Num("used_percentage") ?? s.ContextUsedPercent;
            s.CostUsd = payload.Obj("cost")?.Num("total_cost_usd") ?? s.CostUsd;
        }
        Changed?.Invoke();
    }

    /// <summary>L'utilisateur a consulté la session (appui sur la touche) : « terminé » ou « erreur » repasse au repos.</summary>
    public void Acknowledge(string sessionId)
    {
        var now = _time.GetUtcNow();
        bool changed = false;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var s) && s.State is SessionState.Done or SessionState.Error)
            {
                SetState(s, SessionState.Idle, now);
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Maintenance périodique : estompage des sessions terminées, oubli des sessions mortes ou silencieuses.</summary>
    public void Sweep(Func<ClientProcess, bool>? isAlive = null)
    {
        var now = _time.GetUtcNow();
        bool changed = false;
        lock (_gate)
        {
            foreach (var s in _sessions.Values.ToList())
            {
                var dead = s.Client is not null && isAlive is not null && !isAlive(s.Client);
                if (dead || now - s.LastEventAt > ForgetAfter)
                {
                    _sessions.Remove(s.Id);
                    changed = true;
                    continue;
                }

                if (DoneFadeAfter > TimeSpan.Zero &&
                    s.State is SessionState.Done or SessionState.Error &&
                    now - s.StateSince > DoneFadeAfter)
                {
                    SetState(s, SessionState.Idle, now);
                    changed = true;
                }
            }
        }
        if (changed) Changed?.Invoke();
    }
}
