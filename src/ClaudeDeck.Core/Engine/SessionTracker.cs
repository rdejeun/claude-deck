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
    /// <summary>Taille de fenêtre de contexte observée par modèle (status line).</summary>
    private readonly Dictionary<string, long> _windowByModel = new(StringComparer.Ordinal);

    /// <summary>Outils qui, par nature, attendent une réponse de l'utilisateur.</summary>
    private static readonly HashSet<string> UserFacingTools = new(StringComparer.Ordinal)
    {
        "AskUserQuestion",
        "ExitPlanMode",
    };

    public SessionTracker(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    private static bool IsQuestionTool(string? tool) => tool is not null && UserFacingTools.Contains(tool);

    private static bool MentionsQuestion(string? text) =>
        text is not null && (text.Contains("question", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("AskUserQuestion", StringComparison.Ordinal) ||
                             text.Contains("plan", StringComparison.OrdinalIgnoreCase) && text.Contains("approv", StringComparison.OrdinalIgnoreCase));

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
                var transcript = payload.Str("transcript_path");
                if (!string.IsNullOrEmpty(transcript)) s.TranscriptPath = transcript;
                if (client is not null && (s.Client is null || s.Client.Pid != client.Pid)) s.Client = client;

                Apply(s, eventName, payload, now);
                changed = true;
            }
        }

        if (changed) Changed?.Invoke();
    }

    private static void Apply(Session s, string eventName, JsonElement p, DateTimeOffset now)
    {
        // Événement émis depuis un sous-agent (champ agent_id) : il travaille encore, même si l'agent principal a fini.
        var agentId = p.Str("agent_id");
        if (!string.IsNullOrEmpty(agentId) && eventName is not ("SubagentStart" or "SubagentStop"))
        {
            if (eventName is "Stop" or "StopFailure") return; // ne concerne pas l'agent principal
            s.RunningAgentIds.Add(agentId);
            if (s.State is SessionState.Done or SessionState.Idle)
            {
                // Le tour principal était fini : la session repasse « au travail », chrono du tour repris.
                s.AwaitingBackgroundAgents = true;
                s.TurnStartedAt ??= s.LastTurnStartedAt ?? now;
                SetState(s, SessionState.Working, now);
            }
        }

        switch (eventName)
        {
            case "SessionStart":
                // Une reprise ou un /clear repart au repos ; un démarrage après compaction aussi.
                s.Model = ModelName.Format(p.Str("model")) ?? s.Model;
                SetState(s, SessionState.Idle, now);
                s.CurrentTool = null;
                s.RunningAgentIds.Clear();
                s.BackgroundAgents = 0;
                s.AwaitingBackgroundAgents = false;
                break;

            case "UserPromptSubmit":
                SetState(s, SessionState.Working, now);
                s.TurnStartedAt = now;
                s.CurrentTool = null;
                s.AwaitingBackgroundAgents = false;
                break;

            case "PreToolUse":
            {
                var tool = p.Str("tool_name");
                s.CurrentTool = tool;
                if (tool is not null) s.LastTool = tool;
                if (tool is not null && UserFacingTools.Contains(tool))
                {
                    SetState(s, SessionState.Waiting, now, WaitReason.Question);
                    s.QuestionTitle = QuestionTitleOf(tool, p.Obj("tool_input")) ?? s.QuestionTitle;
                }
                else
                {
                    SetState(s, SessionState.Working, now);
                }
                s.TurnStartedAt ??= now;
                break;
            }

            case "PermissionRequest":
            {
                // Dans l'extension VS Code (et Claude Desktop), les questions passent aussi par le circuit des
                // permissions : un PermissionRequest pour AskUserQuestion / ExitPlanMode est une question.
                var tool = p.Str("tool_name");
                if (tool is not null) s.CurrentTool = s.LastTool = tool;
                SetState(s, SessionState.Waiting, now, IsQuestionTool(tool) ? WaitReason.Question : WaitReason.Permission);
                if (IsQuestionTool(tool)) s.QuestionTitle = QuestionTitleOf(tool, p.Obj("tool_input")) ?? s.QuestionTitle;
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
                        // Rappel envoyé ~6 s après une demande en attente : il porte le type « permission » même
                        // quand l'attente est une question (outil AskUserQuestion en cours, ou texte qui le dit).
                        SetState(s, SessionState.Waiting, now, IsQuestionTool(s.CurrentTool) ||
                            (s.State == SessionState.Waiting && s.WaitReason == WaitReason.Question) ||
                            MentionsQuestion(p.Str("message")) || MentionsQuestion(p.Str("title"))
                                ? WaitReason.Question
                                : WaitReason.Permission);
                        break;
                    case "elicitation_dialog":
                    case "elicitation_url_dialog":
                    case "agent_needs_input":
                        SetState(s, SessionState.Waiting, now, WaitReason.Question);
                        // Formulaire MCP : à défaut d'en-tête, le titre (ou le message) de la notification.
                        s.QuestionTitle ??= p.Str("title") ?? p.Str("message");
                        break;
                    case "idle_prompt":
                        // Claude attend une saisie : si l'on croyait qu'il travaillait, le tour a été
                        // interrompu (Échap) sans événement Stop.
                        // Ce rappel n'est envoyé que lorsqu'aucun agent d'arrière-plan ne tourne plus.
                        if (s.AwaitingBackgroundAgents)
                        {
                            s.AwaitingBackgroundAgents = false;
                            s.BackgroundAgents = 0;
                            s.RunningAgentIds.Clear();
                            EndTurn(s, now);
                            SetState(s, SessionState.Done, now);
                        }
                        else if (s.State is SessionState.Working or SessionState.Compacting)
                        {
                            EndTurn(s, now);
                            SetState(s, SessionState.Idle, now);
                        }
                        break;
                }
                break;

            case "Stop":
            {
                // « Stop » = l'agent principal a fini de répondre, pas forcément la fin du travail : des agents lancés
                // en arrière-plan peuvent continuer (liste « background_tasks »). La session reste alors « au travail »
                // et le chrono continue, jusqu'à ce que ces agents soient terminés.
                // Deux signaux, l'un ou l'autre suffit : la liste « background_tasks » (absente de certaines versions),
                // et les sous-agents démarrés (SubagentStart) dont on n'a pas encore reçu le SubagentStop.
                s.CurrentTool = null;
                var background = CountBackgroundAgents(p);
                s.BackgroundAgents = background ?? 0;
                if (background > 0 || s.RunningAgentIds.Count > 0)
                {
                    s.AwaitingBackgroundAgents = true;
                    SetState(s, SessionState.Working, now);
                }
                else
                {
                    s.AwaitingBackgroundAgents = false;
                    EndTurn(s, now);
                    SetState(s, SessionState.Done, now);
                }
                break;
            }

            case "StopFailure":
                EndTurn(s, now);
                SetState(s, SessionState.Error, now);
                break;

            case "PostModelSwitch":
                s.Model = ModelName.Format(p.Str("to_model")) ?? s.Model;
                break;

            case "SubagentStart":
                if (p.Str("agent_id") is { Length: > 0 } startedId) s.RunningAgentIds.Add(startedId);
                break;

            case "SubagentStop":
            {
                // Claude Code envoie aussi SubagentStop pour ses agents internes (suggestions, /btw) sans SubagentStart :
                // on ne retire que des agents connus, au lieu de décrémenter un compteur.
                if (p.Str("agent_id") is { Length: > 0 } stoppedId) s.RunningAgentIds.Remove(stoppedId);
                if (CountBackgroundAgents(p) is { } background) s.BackgroundAgents = background;
                else if (s.RunningAgentIds.Count == 0) s.BackgroundAgents = 0;
                if (s.AwaitingBackgroundAgents && s.BackgroundAgents == 0 && s.RunningAgentIds.Count == 0)
                {
                    s.AwaitingBackgroundAgents = false;
                    EndTurn(s, now);
                    SetState(s, SessionState.Done, now);
                }
                break;
            }

            case "PreCompact":
                SetState(s, SessionState.Compacting, now);
                break;

            case "PostCompact":
                if (s.State == SessionState.Compacting)
                    SetState(s, s.TurnStartedAt is null ? SessionState.Idle : SessionState.Working, now);
                break;
        }
    }

    /// <summary>
    /// Types de tâches d'arrière-plan qui sont du travail d'agent. Les tâches « shell » (serveur lancé en fond…),
    /// « monitor » et « MCP task » peuvent tourner indéfiniment : elles ne gardent pas la session « au travail ».
    /// </summary>
    private static readonly HashSet<string> AgentTaskTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "subagent", "workflow", "teammate", "cloud session",
        // Noms internes, quand Claude Code ne connaît pas de libellé.
        "local_agent", "local_workflow", "in_process_teammate", "remote_agent",
    };

    /// <summary>Nombre d'agents en cours d'après « background_tasks », ou null si le champ est absent.</summary>
    internal static int? CountBackgroundAgents(JsonElement p)
    {
        if (!p.TryGetProperty("background_tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return null;
        var count = 0;
        foreach (var t in tasks.EnumerateArray())
        {
            var type = t.Str("type");
            var status = t.Str("status");
            var finished = status is not null &&
                           (status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("killed", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("cancelled", StringComparison.OrdinalIgnoreCase));
            if (type is not null && AgentTaskTypes.Contains(type) && !finished) count++;
        }
        return count;
    }

    private static void EndTurn(Session s, DateTimeOffset now)
    {
        if (s.TurnStartedAt is { } start)
        {
            s.LastTurnDuration = now - start;
            s.LastTurnStartedAt = start;
        }
        s.TurnStartedAt = null;
        s.CurrentTool = null;
    }

    private static void SetState(Session s, SessionState state, DateTimeOffset now, WaitReason reason = WaitReason.None)
    {
        if (s.State != state || s.WaitReason != reason) s.StateSince = now;
        s.State = state;
        s.WaitReason = state == SessionState.Waiting ? reason : WaitReason.None;
        if (s.WaitReason != WaitReason.Question) s.QuestionTitle = null;
    }

    /// <summary>
    /// Titre court d'une question : « header » de la première question d'AskUserQuestion (≤ 12 caractères, conçu pour
    /// une étiquette), sinon le début de son texte ; « Plan » pour la validation d'un plan.
    /// </summary>
    internal static string? QuestionTitleOf(string? tool, JsonElement? input)
    {
        if (tool == "ExitPlanMode") return "Plan";
        if (input is not { } i || !i.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array) return null;
        foreach (var q in qs.EnumerateArray())
        {
            var header = q.Str("header");
            if (!string.IsNullOrWhiteSpace(header)) return header.Trim();
            var text = q.Str("question");
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return null;
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
            var ctxWindow = payload.Obj("context_window");
            if (ctxWindow?.Num("used_percentage") is { } pct)
            {
                s.ContextUsedPercent = pct;
                s.ContextEstimated = false;
                s.ContextFromStatusLineAt = now;
            }
            // Mémorise la taille de fenêtre par modèle, pour les sessions sans status line (extension VS Code).
            if (ctxWindow?.Num("context_window_size") is { } size && size > 0 &&
                ModelName.Format(payload.Obj("model")?.Str("id")) is { } key)
                _windowByModel[key] = (long)size;
            s.CostUsd = payload.Obj("cost")?.Num("total_cost_usd") ?? s.CostUsd;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Met à jour modèle et remplissage du contexte à partir des transcripts (seule source pour l'extension VS Code) :
    /// après chaque nouvel événement (au plus toutes les 4 s), et toutes les 15 s pendant le travail.
    /// La lecture des fichiers se fait hors verrou.
    /// </summary>
    public void RefreshFromTranscripts(Func<string, TranscriptInfo?> read)
    {
        var now = _time.GetUtcNow();
        var minGap = TimeSpan.FromSeconds(4);
        List<(string Id, string Path)> due;
        lock (_gate)
        {
            due = _sessions.Values
                .Where(s => s.TranscriptPath is not null)
                .Where(s => s.ModelProbedAt is null
                    || (now - s.ModelProbedAt > minGap && (s.Model is null || s.LastEventAt > s.ModelProbedAt))
                    || (s.State is SessionState.Working && now - s.ModelProbedAt > TimeSpan.FromSeconds(15)))
                .Select(s => (s.Id, s.TranscriptPath!))
                .ToList();
            foreach (var (id, _) in due) _sessions[id].ModelProbedAt = now;
        }

        var changed = false;
        foreach (var (id, path) in due)
        {
            TranscriptInfo? info;
            try { info = read(path); }
            catch { continue; }
            if (info is null) continue;
            var model = ModelName.Format(info.ModelId);
            lock (_gate)
            {
                if (!_sessions.TryGetValue(id, out var s)) continue;
                if (model is not null && s.Model != model)
                {
                    s.Model = model;
                    changed = true;
                }

                // La status line (CLI) fait foi tant qu'elle est récente ; sinon, estimation depuis le transcript.
                var statusLineFresh = s.ContextFromStatusLineAt is { } at && now - at < TimeSpan.FromMinutes(2);
                if (!statusLineFresh && info.ContextTokens is { } tokens && tokens > 0)
                {
                    var key = model ?? s.Model;
                    var known = key is not null && _windowByModel.TryGetValue(key, out var w) ? w : (long?)null;
                    var window = known ?? (tokens > 200_000 ? 1_000_000 : 200_000);
                    var pct = Math.Min(100.0, tokens * 100.0 / window);
                    if (s.ContextUsedPercent != pct || s.ContextEstimated != (known is null))
                    {
                        s.ContextUsedPercent = pct;
                        s.ContextEstimated = known is null;
                        changed = true;
                    }
                }
            }
        }
        if (changed) Changed?.Invoke();
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
