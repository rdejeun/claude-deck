using ClaudeDeck.Core.Engine;
using ClaudeDeck.Core.Model;

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
    public void Subagent_counter_never_negative()
    {
        var t = NewTracker();
        t.HandleHook(J.Hook("a", "SubagentStart", new { agent_id = "x" }));
        t.HandleHook(J.Hook("a", "SubagentStart", new { agent_id = "y" }));
        Assert.Equal(2, Only(t).ActiveSubagents);
        t.HandleHook(J.Hook("a", "SubagentStop"));
        t.HandleHook(J.Hook("a", "SubagentStop"));
        t.HandleHook(J.Hook("a", "SubagentStop"));
        Assert.Equal(0, Only(t).ActiveSubagents);
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
