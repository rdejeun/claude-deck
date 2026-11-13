using ClaudeDeck.Core.Engine;
using ClaudeDeck.Core.Model;
using ClaudeDeck.Core.Rendering;

namespace ClaudeDeck.Tests;

public class SessionTrackerTests
{
    private readonly FakeTime _time = new();
    private SessionTracker NewTracker() => new(_time);

    private static SessionSnapshot Only(SessionTracker t) => Assert.Single(t.Snapshot());

    [Fact]
    public void Full_turn_goes_idle_working_waiting_working_done()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "SessionStart", new { source = "startup" }));
        Assert.Equal(SessionState.Idle, Only(t).State);
        Assert.Equal("a", Only(t).DisplayName);

        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        Assert.Equal(SessionState.Working, Only(t).State);

        t.HandleHook(J.Hook("a", "PreToolUse", new { tool_name = "Bash" }));
        Assert.Equal("Bash", Only(t).CurrentTool);

        t.HandleHook(J.Hook("a", "PermissionRequest", new { tool_name = "Bash" }));
        Assert.Equal(SessionState.Waiting, Only(t).State);
        Assert.Equal(WaitReason.Permission, Only(t).WaitReason);

        t.HandleHook(J.Hook("a", "PostToolUse", new { tool_name = "Bash" }));
        Assert.Equal(SessionState.Working, Only(t).State);
        Assert.Null(Only(t).CurrentTool);

        _time.Advance(TimeSpan.FromSeconds(30));
        t.HandleHook(J.Hook("a", "Stop"));
        Assert.Equal(SessionState.Done, Only(t).State);
        Assert.Equal(TimeSpan.FromSeconds(30), Only(t).LastTurnDuration);
    }

    [Fact]
    public void Session_end_removes_session()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "SessionStart"));
        t.HandleHook(J.Hook("a", "SessionEnd", new { reason = "other" }));
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Ask_user_question_is_waiting_question()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "PreToolUse", new { tool_name = "AskUserQuestion" }));
        Assert.Equal(SessionState.Waiting, Only(t).State);
        Assert.Equal(WaitReason.Question, Only(t).WaitReason);
    }

    [Fact]
    public void Notification_types_are_mapped()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        t.HandleHook(J.Hook("a", "Notification", new { notification_type = "permission_prompt", message = "x" }));
        Assert.Equal(SessionState.Waiting, Only(t).State);

        t.HandleHook(J.Hook("b", "UserPromptSubmit"));
        t.HandleHook(J.Hook("b", "Notification", new { notification_type = "elicitation_dialog" }));
        Assert.Equal(WaitReason.Question, t.Snapshot().Single(s => s.Id == "b").WaitReason);
    }

    [Fact]
    public void Idle_prompt_after_interrupt_clears_working()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        t.HandleHook(J.Hook("a", "PreToolUse", new { tool_name = "Bash" }));
        // Échap : pas d'événement Stop, puis Claude attend une saisie.
        t.HandleHook(J.Hook("a", "Notification", new { notification_type = "idle_prompt" }));
        Assert.Equal(SessionState.Idle, Only(t).State);
        Assert.Null(Only(t).CurrentTool);
    }

    [Fact]
    public void Idle_prompt_keeps_done()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "Stop"));
        t.HandleHook(J.Hook("a", "Notification", new { notification_type = "idle_prompt" }));
        Assert.Equal(SessionState.Done, Only(t).State);
    }

    [Fact]
    public void Compaction_and_errors()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        t.HandleHook(J.Hook("a", "PreCompact", new { trigger = "auto" }));
        Assert.Equal(SessionState.Compacting, Only(t).State);
        t.HandleHook(J.Hook("a", "PostCompact"));
        Assert.Equal(SessionState.Working, Only(t).State);
        t.HandleHook(J.Hook("a", "StopFailure"));
        Assert.Equal(SessionState.Error, Only(t).State);
    }

    [Fact]
    public void Internal_agent_stops_do_not_hide_running_agents()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "SubagentStart", new { agent_id = "x", agent_type = "Explore" }));
        t.HandleHook(J.Hook("a", "SubagentStart", new { agent_id = "y", agent_type = "general-purpose" }));
        Assert.Equal(2, Only(t).ActiveSubagents);
        // Agents internes de Claude Code (suggestions, /btw) : SubagentStop sans SubagentStart.
        t.HandleHook(J.Hook("a", "SubagentStop", new { agent_id = "internal-1", agent_type = "" }));
        t.HandleHook(J.Hook("a", "SubagentStop", new { agent_type = "" }));
        Assert.Equal(2, Only(t).ActiveSubagents);
        t.HandleHook(J.Hook("a", "SubagentStop", new { agent_id = "x" }));
        t.HandleHook(J.Hook("a", "SubagentStop", new { agent_id = "y" }));
        t.HandleHook(J.Hook("a", "SubagentStop", new { agent_id = "y" }));
        Assert.Equal(0, Only(t).ActiveSubagents);
    }

    private static object Bg(params (string Type, string Status)[] tasks) =>
        tasks.Select((x, i) => new { id = $"task-{i}", type = x.Type, status = x.Status, description = "..." }).ToArray();

    [Fact]
    public void Stop_with_background_agent_keeps_working_until_it_finishes()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        var turnStart = Only(t).TurnStartedAt;
        t.HandleHook(J.Hook("a", "SubagentStart", new { agent_id = "bg1", agent_type = "general-purpose" }));
        t.HandleHook(J.Hook("a", "Stop", new { background_tasks = Bg(("subagent", "running"), ("shell", "running")) }));
        Assert.Equal(SessionState.Working, Only(t).State);
        Assert.Equal(1, Only(t).ActiveSubagents);
        Assert.Equal(turnStart, Only(t).TurnStartedAt); // le chrono continue

        // Fin d'un agent interne : rien ne change.
        t.HandleHook(J.Hook("a", "SubagentStop", new { agent_type = "", background_tasks = Bg(("subagent", "running")) }));
        Assert.Equal(SessionState.Working, Only(t).State);

        // Fin de l'agent d'arrière-plan : il ne reste qu'un shell (serveur…), la session est terminée.
        t.HandleHook(J.Hook("a", "SubagentStop", new { agent_id = "bg1", background_tasks = Bg(("shell", "running")) }));
        Assert.Equal(SessionState.Done, Only(t).State);
        Assert.Null(Only(t).TurnStartedAt);
    }

    [Fact]
    public void Main_agent_woken_by_background_agent_ends_with_a_plain_stop()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        t.HandleHook(J.Hook("a", "Stop", new { background_tasks = Bg(("subagent", "running")) }));
        Assert.Equal(SessionState.Working, Only(t).State);
        t.HandleHook(J.Hook("a", "PreToolUse", new { tool_name = "Read" }));
        t.HandleHook(J.Hook("a", "PostToolUse", new { tool_name = "Read" }));
        t.HandleHook(J.Hook("a", "Stop", new { background_tasks = Array.Empty<object>() }));
        Assert.Equal(SessionState.Done, Only(t).State);
    }

    [Fact]
    public void Shell_only_background_tasks_and_old_versions_end_the_turn()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        t.HandleHook(J.Hook("a", "Stop", new { background_tasks = Bg(("shell", "running"), ("monitor", "running")) }));
        Assert.Equal(SessionState.Done, Only(t).State);
        t.HandleHook(J.Hook("b", "UserPromptSubmit"));
        t.HandleHook(J.Hook("b", "Stop")); // pas de background_tasks (ancienne version)
        Assert.Equal(SessionState.Done, t.Snapshot().Single(s => s.Id == "b").State);
    }

    [Fact]
    public void Subagent_still_running_without_background_list_keeps_working()
    {
        // Version de Claude Code sans « background_tasks » : le SubagentStart sans SubagentStop suffit.
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        t.HandleHook(J.Hook("a", "SubagentStart", new { agent_id = "tests", agent_type = "test-runner" }));
        t.HandleHook(J.Hook("a", "Stop"));
        Assert.Equal(SessionState.Working, Only(t).State);
        Assert.NotNull(Only(t).TurnStartedAt);
        t.HandleHook(J.Hook("a", "SubagentStop", new { agent_id = "tests", agent_type = "test-runner" }));
        Assert.Equal(SessionState.Done, Only(t).State);
    }

    [Fact]
    public void Subagent_activity_after_stop_revives_the_session()
    {
        // SubagentStart manqué (daemon relancé, version ancienne) : l'activité du sous-agent suffit à revenir au travail.
        var time = new FakeTime();
        var t = new SessionTracker(time);
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        var start = Only(t).TurnStartedAt;
        time.Advance(TimeSpan.FromMinutes(2));
        t.HandleHook(J.Hook("a", "Stop", new { background_tasks = Array.Empty<object>() }));
        Assert.Equal(SessionState.Done, Only(t).State);

        time.Advance(TimeSpan.FromMinutes(1));
        t.HandleHook(J.Hook("a", "PostToolUse", new { tool_name = "Bash", agent_id = "tests", agent_type = "test-runner" }));
        Assert.Equal(SessionState.Working, Only(t).State);
        Assert.Equal(start, Only(t).TurnStartedAt); // chrono du tour repris, pas remis à zéro

        // Un « Stop » portant un agent_id ne termine pas la session.
        t.HandleHook(J.Hook("a", "Stop", new { agent_id = "tests" }));
        Assert.Equal(SessionState.Working, Only(t).State);

        t.HandleHook(J.Hook("a", "SubagentStop", new { agent_id = "tests", agent_type = "test-runner" }));
        Assert.Equal(SessionState.Done, Only(t).State);
    }

    [Fact]
    public void Idle_prompt_while_awaiting_background_means_done()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        t.HandleHook(J.Hook("a", "Stop", new { background_tasks = Bg(("subagent", "running")) }));
        t.HandleHook(J.Hook("a", "Notification", new { notification_type = "idle_prompt" }));
        Assert.Equal(SessionState.Done, Only(t).State);
    }

    [Fact]
    public void Acknowledge_turns_done_into_idle()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "Stop"));
        t.Acknowledge("a");
        Assert.Equal(SessionState.Idle, Only(t).State);
    }

    [Fact]
    public void Sweep_fades_done_and_forgets_dead_or_silent_sessions()
    {
        var t = NewTracker();
        t.DoneFadeAfter = TimeSpan.FromMinutes(30);
        t.HandleHook(J.Hook("done", "Stop"));
        t.HandleHook(J.Hook("dead", "SessionStart"), new ClientProcess(1234, null, "claude"));
        t.HandleHook(J.Hook("waiting", "PermissionRequest", new { tool_name = "Bash" }));

        _time.Advance(TimeSpan.FromMinutes(31));
        t.Sweep(c => c.Pid != 1234);
        var all = t.Snapshot().ToDictionary(s => s.Id);
        Assert.False(all.ContainsKey("dead"));
        Assert.Equal(SessionState.Idle, all["done"].State);
        Assert.Equal(SessionState.Waiting, all["waiting"].State);

        _time.Advance(TimeSpan.FromHours(13));
        t.Sweep();
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Status_line_enriches_session()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "SessionStart"));
        t.HandleStatusLine(J.E(new
        {
            session_id = "a",
            model = new { display_name = "Opus 5.5" },
            workspace = new { current_dir = @"C:\dev\shop\src", project_dir = @"C:\dev\shop" },
            context_window = new { used_percentage = 42.5 },
            cost = new { total_cost_usd = 1.25 },
        }));
        var s = Only(t);
        Assert.Equal("shop", s.DisplayName);
        Assert.Equal("Opus 5.5", s.Model);
        Assert.Equal(42.5, s.ContextUsedPercent);
    }

    [Fact]
    public void Ignores_payload_without_session_or_event()
    {
        var t = NewTracker();
        t.HandleHook(J.E(new { hook_event_name = "Stop" }));
        t.HandleHook(J.E(new { session_id = "a" }));
        Assert.Empty(t.Snapshot());
    }
}

public class ModelTrackingTests
{
    [Fact]
    public void Model_comes_from_session_start_then_model_switch()
    {
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("a", "SessionStart", new { source = "startup", model = "claude-opus-5-5" }));
        Assert.Equal("Opus 5.5", t.Snapshot()[0].Model);
        t.HandleHook(J.Hook("a", "PostModelSwitch", new { from_model = "claude-opus-5-5", to_model = "claude-sonnet-5-5" }));
        Assert.Equal("Sonnet 5.5", t.Snapshot()[0].Model);
        t.HandleHook(J.Hook("a", "SessionStart", new { source = "clear" })); // pas de modèle : conservé
        Assert.Equal("Sonnet 5.5", t.Snapshot()[0].Model);
    }
}

public class TranscriptModelTests
{
    [Fact]
    public void Reads_last_main_agent_model_from_transcript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cd-tr-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                """{"type":"user","message":{"role":"user","content":"x"}}""",
                """{"type":"assistant","message":{"model":"claude-sonnet-5-5","content":[]}}""",
                """{"type":"assistant","message":{"model":"claude-opus-5-5","content":[{"type":"tool_use","input":{"model":"haiku"}}],"usage":{"input_tokens":2,"cache_creation_input_tokens":1000,"cache_read_input_tokens":49000,"output_tokens":300}}}""",
                """{"type":"assistant","isSidechain":true,"message":{"model":"claude-haiku-4-5-20251001","content":[]}}""",
                """{"type":"assistant","message":{"model":"<synthetic>","content":[]}}""",
                """{"type":"user","message":{"role":"user","content":"y"}}""",
            ]);
            Assert.Equal("claude-opus-5-5", TranscriptModelReader.LastModel(path));
            Assert.Equal(50_002, TranscriptModelReader.Read(path)!.ContextTokens);
            Assert.Null(TranscriptModelReader.LastModel(path + ".absent"));

            var time = new FakeTime();
            var t = new SessionTracker(time);
            t.HandleHook(J.Hook("a", "UserPromptSubmit", new { transcript_path = path }));
            Assert.Null(t.Snapshot()[0].Model);
            t.RefreshFromTranscripts(TranscriptModelReader.Read);
            var s = t.Snapshot()[0];
            Assert.Equal("Opus 5.5", s.Model);
            Assert.Equal(25.001, s.ContextUsedPercent!.Value, 3); // fenêtre supposée de 200 000
            Assert.True(s.ContextEstimated);

            // Une status line a révélé une fenêtre de 1 M pour ce modèle : plus d'estimation.
            t.HandleStatusLine(J.E(new { session_id = "b", model = new { id = "claude-opus-5-5[1m]", display_name = "Opus 5.5" },
                context_window = new { used_percentage = 3, context_window_size = 1_000_000 } }));
            time.Advance(TimeSpan.FromSeconds(5));
            t.HandleHook(J.Hook("a", "Stop"));
            time.Advance(TimeSpan.FromSeconds(5));
            t.RefreshFromTranscripts(TranscriptModelReader.Read);
            s = t.Snapshot().Single(x => x.Id == "a");
            Assert.Equal(5.0002, s.ContextUsedPercent!.Value, 3);
            Assert.False(s.ContextEstimated);
        }
        finally
        {
            File.Delete(path);
        }
    }
}


public class QuestionDetectionTests
{
    private static SessionSnapshot Only(SessionTracker t) => Assert.Single(t.Snapshot());

    [Fact]
    public void Vscode_ask_user_question_stays_a_question()
    {
        // Séquence de l'extension VS Code : PreToolUse, PermissionRequest et rappel permission_prompt pour AskUserQuestion.
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        t.HandleHook(J.Hook("a", "PreToolUse", new { tool_name = "AskUserQuestion" }));
        t.HandleHook(J.Hook("a", "PermissionRequest", new { tool_name = "AskUserQuestion" }));
        Assert.Equal(WaitReason.Question, Only(t).WaitReason);
        t.HandleHook(J.Hook("a", "Notification", new { notification_type = "permission_prompt", message = "Claude needs your permission to use AskUserQuestion" }));
        Assert.Equal(WaitReason.Question, Only(t).WaitReason);
        t.HandleHook(J.Hook("a", "PostToolUse", new { tool_name = "AskUserQuestion" }));
        Assert.Equal(SessionState.Working, Only(t).State);
    }

    [Fact]
    public void Permission_request_without_pretooluse_is_classified_by_tool()
    {
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("a", "PermissionRequest", new { tool_name = "ExitPlanMode" }));
        Assert.Equal(WaitReason.Question, Only(t).WaitReason);
        t.HandleHook(J.Hook("b", "PermissionRequest", new { tool_name = "Bash" }));
        Assert.Equal(WaitReason.Permission, t.Snapshot().Single(s => s.Id == "b").WaitReason);
    }

    [Theory]
    [InlineData("elicitation_url_dialog", null)]
    [InlineData("agent_needs_input", null)]
    [InlineData("permission_prompt", "Claude has a question for you")]
    public void Other_question_signals(string type, string? message)
    {
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("a", "UserPromptSubmit"));
        t.HandleHook(J.Hook("a", "Notification", new { notification_type = type, message }));
        Assert.Equal(WaitReason.Question, Only(t).WaitReason);
    }

    [Fact]
    public void Permission_prompt_for_bash_stays_a_permission()
    {
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("a", "PreToolUse", new { tool_name = "Bash" }));
        t.HandleHook(J.Hook("a", "Notification", new { notification_type = "permission_prompt", message = "Claude needs your permission to use Bash" }));
        Assert.Equal(WaitReason.Permission, Only(t).WaitReason);
    }
}

public class QuestionTitleTests
{
    private static object Ask(params (string Q, string? H)[] qs) => new
    {
        questions = qs.Select(x => new { question = x.Q, header = x.H, options = new[] { new { label = "A" } }, multiSelect = false }).ToArray(),
    };

    [Fact]
    public void Header_of_first_question_becomes_the_title_and_is_cleared_after()
    {
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("a", "PreToolUse", new { tool_name = "AskUserQuestion", tool_input = Ask(("Quel framework ?", "Framework"), ("Quel style ?", "Style")) }));
        Assert.Equal("Framework", t.Snapshot()[0].QuestionTitle);
        // Rappel « permission » de l'extension VS Code : le titre est conservé.
        t.HandleHook(J.Hook("a", "PermissionRequest", new { tool_name = "AskUserQuestion", tool_input = Ask(("Quel framework ?", "Framework")) }));
        t.HandleHook(J.Hook("a", "Notification", new { notification_type = "permission_prompt", message = "Claude needs your permission to use AskUserQuestion" }));
        Assert.Equal("Framework", t.Snapshot()[0].QuestionTitle);
        t.HandleHook(J.Hook("a", "PostToolUse", new { tool_name = "AskUserQuestion" }));
        Assert.Null(t.Snapshot()[0].QuestionTitle);
    }

    [Fact]
    public void Fallbacks_question_text_plan_and_elicitation()
    {
        Assert.Equal("Quel framework ?", SessionTracker.QuestionTitleOf("AskUserQuestion", J.E(Ask(("Quel framework ?", null)))));
        Assert.Equal("Plan", SessionTracker.QuestionTitleOf("ExitPlanMode", J.E(new { plan = "..." })));
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("a", "Notification", new { notification_type = "elicitation_dialog", title = "GitHub", message = "Choisir un dépôt" }));
        Assert.Equal("GitHub", t.Snapshot()[0].QuestionTitle);
    }

    [Fact]
    public void Question_tile_shows_title_instead_of_timer()
    {
        var now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        var s = new SessionSnapshot("id", "p", null, null, null, SessionState.Waiting, now.AddSeconds(-95), WaitReason.Question,
            "AskUserQuestion", null, null, null, 0, null, null, now, null, QuestionTitle: "Framework");
        Assert.Equal("Framework", b.Session(s, now, 0).Sub);
        Assert.Null(b.Session(s with { QuestionTitle = null }, now, 0).Sub);
    }
}

/// <summary>Séquences relevées dans le journal d'un utilisateur (extension VS Code, 13/11/2026).</summary>
public class RealWorldSequenceTests
{
    private static object Bg(params (string Type, string Status)[] tasks) =>
        tasks.Select((x, i) => new { id = $"task-{i}", type = x.Type, status = x.Status }).ToArray();

    [Fact]
    public void Compaction_ends_with_internal_agent_stop_without_postcompact()
    {
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("c", "PreCompact"));
        Assert.Equal(SessionState.Compacting, t.Snapshot()[0].State);
        t.HandleHook(J.Hook("c", "SubagentStop", new { agent_id = "aa3743de1e311e8d4", agent_type = "", background_tasks = Bg() }));
        Assert.Equal(SessionState.Idle, t.Snapshot()[0].State);
    }

    [Fact]
    public void Compaction_restores_previous_state_or_times_out()
    {
        var time = new FakeTime();
        var t = new SessionTracker(time);
        t.HandleHook(J.Hook("c", "Stop", new { background_tasks = Bg() }));
        t.HandleHook(J.Hook("c", "PreCompact"));
        t.HandleHook(J.Hook("c", "PostCompact"));
        Assert.Equal(SessionState.Done, t.Snapshot()[0].State);

        t.HandleHook(J.Hook("d", "UserPromptSubmit"));
        t.HandleHook(J.Hook("d", "PreCompact", new { trigger = "auto" }));
        time.Advance(TimeSpan.FromMinutes(4)); // aucune nouvelle : filet de sécurité
        t.Sweep();
        Assert.Equal(SessionState.Working, t.Snapshot().Single(x => x.Id == "d").State);
    }

    [Fact]
    public void Stopping_subagent_listed_as_running_in_its_own_stop_is_not_counted()
    {
        // 23:01:33 → 23:01:47 : sous-agent relancé après la fin du tour principal, puis SubagentStop qui le liste encore.
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("f", "UserPromptSubmit"));
        t.HandleHook(J.Hook("f", "Stop", new { background_tasks = Bg(("shell", "running"), ("shell", "running")) }));
        Assert.Equal(SessionState.Done, t.Snapshot()[0].State);
        t.HandleHook(J.Hook("f", "SubagentStart", new { agent_id = "aa9772ed2572a84dc", agent_type = "general-purpose" }));
        t.HandleHook(J.Hook("f", "PreToolUse", new { tool_name = "Bash", agent_id = "aa9772ed2572a84dc", agent_type = "general-purpose" }));
        Assert.Equal(SessionState.Working, t.Snapshot()[0].State);
        t.HandleHook(J.Hook("f", "SubagentStop", new
        {
            agent_id = "aa9772ed2572a84dc", agent_type = "general-purpose",
            background_tasks = Bg(("subagent", "running"), ("shell", "running")),
        }));
        Assert.Equal(SessionState.Done, t.Snapshot()[0].State);
    }

    [Fact]
    public void Another_background_subagent_still_counts()
    {
        var t = new SessionTracker(new FakeTime());
        t.HandleHook(J.Hook("g", "UserPromptSubmit"));
        t.HandleHook(J.Hook("g", "SubagentStart", new { agent_id = "a1", agent_type = "general-purpose" }));
        t.HandleHook(J.Hook("g", "SubagentStart", new { agent_id = "a2", agent_type = "general-purpose" }));
        t.HandleHook(J.Hook("g", "Stop", new { background_tasks = Bg(("subagent", "running"), ("subagent", "running")) }));
        t.HandleHook(J.Hook("g", "SubagentStop", new { agent_id = "a1", agent_type = "general-purpose", background_tasks = Bg(("subagent", "running"), ("subagent", "running")) }));
        Assert.Equal(SessionState.Working, t.Snapshot()[0].State); // a2 tourne encore
        t.HandleHook(J.Hook("g", "SubagentStop", new { agent_id = "a2", agent_type = "general-purpose", background_tasks = Bg(("subagent", "running")) }));
        Assert.Equal(SessionState.Done, t.Snapshot()[0].State);
    }
}
