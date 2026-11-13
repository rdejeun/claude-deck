namespace ClaudeDeck.Core.Model;

public enum SessionState
{
    /// <summary>Session ouverte, rien en cours.</summary>
    Idle,
    /// <summary>Claude travaille (prompt en cours, outil en cours).</summary>
    Working,
    /// <summary>Claude attend une action de l'utilisateur (permission, question).</summary>
    Waiting,
    /// <summary>Claude a terminé sa réponse (événement Stop), pas encore vu par l'utilisateur.</summary>
    Done,
    /// <summary>Compaction du contexte en cours.</summary>
    Compacting,
    /// <summary>Le tour s'est terminé sur une erreur API (StopFailure).</summary>
    Error,
}

public enum WaitReason
{
    None,
    Permission,
    Question,
}

/// <summary>Processus client (Claude Code) identifié à partir de la connexion TCP du hook.</summary>
public sealed record ClientProcess(int Pid, DateTime? StartTimeUtc, string? Name);

/// <summary>État d'une session Claude Code tel que vu par le daemon. Muté uniquement par <see cref="Engine.SessionTracker"/>.</summary>
public sealed class Session
{
    public Session(string id, DateTimeOffset now)
    {
        Id = id;
        CreatedAt = now;
        LastEventAt = now;
        StateSince = now;
    }

    public string Id { get; }
    public DateTimeOffset CreatedAt { get; }

    public string? Cwd { get; set; }
    public string? ProjectDir { get; set; }
    public string? SessionName { get; set; }
    public string? Model { get; set; }

    public SessionState State { get; set; } = SessionState.Idle;
    public DateTimeOffset StateSince { get; set; }
    public WaitReason WaitReason { get; set; }

    /// <summary>Outil en cours d'exécution (PreToolUse sans PostToolUse).</summary>
    public string? CurrentTool { get; set; }
    public string? LastTool { get; set; }

    /// <summary>Début du tour courant (UserPromptSubmit).</summary>
    public DateTimeOffset? TurnStartedAt { get; set; }
    /// <summary>Durée du dernier tour terminé.</summary>
    public TimeSpan? LastTurnDuration { get; set; }

    /// <summary>Sous-agents lancés (SubagentStart) et pas encore terminés, par agent_id.</summary>
    public HashSet<string> RunningAgentIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Agents encore en cours en arrière-plan d'après le dernier Stop / SubagentStop (« background_tasks »).</summary>
    public int BackgroundAgents { get; set; }

    /// <summary>État avant la compaction en cours, rétabli à sa fin.</summary>
    public SessionState StateBeforeCompact { get; set; } = SessionState.Idle;

    /// <summary>Début du dernier tour terminé (pour reprendre le chrono si un sous-agent se manifeste après la fin du tour).</summary>
    public DateTimeOffset? LastTurnStartedAt { get; set; }

    /// <summary>L'agent principal a fini son tour (Stop) mais des agents travaillent encore en arrière-plan.</summary>
    public bool AwaitingBackgroundAgents { get; set; }

    public int ActiveSubagents => Math.Max(RunningAgentIds.Count, BackgroundAgents);
    public double? ContextUsedPercent { get; set; }
    /// <summary>Le % de contexte est estimé (taille de fenêtre supposée) et non fourni par la status line.</summary>
    public bool ContextEstimated { get; set; }
    /// <summary>Dernière valeur de contexte fournie par la status line (prioritaire sur le transcript).</summary>
    public DateTimeOffset? ContextFromStatusLineAt { get; set; }
    public double? CostUsd { get; set; }

    public DateTimeOffset LastEventAt { get; set; }
    public ClientProcess? Client { get; set; }

    /// <summary>Titre court de la question en attente (champ « header » d'AskUserQuestion), ou null.</summary>
    public string? QuestionTitle { get; set; }

    /// <summary>Transcript JSONL de la session (champ transcript_path des hooks).</summary>
    public string? TranscriptPath { get; set; }

    /// <summary>Dernière lecture du modèle dans le transcript.</summary>
    public DateTimeOffset? ModelProbedAt { get; set; }

    /// <summary>Nom court affiché sur la touche.</summary>
    public string DisplayName
    {
        get
        {
            var dir = ProjectDir ?? Cwd;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var trimmed = dir.TrimEnd('/', '\\');
                var idx = trimmed.LastIndexOfAny(['/', '\\']);
                var name = idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            return Id.Length > 6 ? Id[..6] : Id;
        }
    }

    public SessionSnapshot Snapshot() => new(
        Id, DisplayName, Cwd, SessionName, Model, State, StateSince, WaitReason, CurrentTool, LastTool,
        TurnStartedAt, LastTurnDuration, ActiveSubagents, ContextUsedPercent, CostUsd, LastEventAt, Client, ContextEstimated,
        QuestionTitle);
}

/// <summary>Copie immuable d'une session, partagée avec le rendu et l'API web.</summary>
public sealed record SessionSnapshot(
    string Id,
    string DisplayName,
    string? Cwd,
    string? SessionName,
    string? Model,
    SessionState State,
    DateTimeOffset StateSince,
    WaitReason WaitReason,
    string? CurrentTool,
    string? LastTool,
    DateTimeOffset? TurnStartedAt,
    TimeSpan? LastTurnDuration,
    int ActiveSubagents,
    double? ContextUsedPercent,
    double? CostUsd,
    DateTimeOffset LastEventAt,
    ClientProcess? Client,
    bool ContextEstimated = false,
    string? QuestionTitle = null);
