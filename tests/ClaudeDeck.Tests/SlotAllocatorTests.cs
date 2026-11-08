using ClaudeDeck.Core.Engine;
using ClaudeDeck.Core.Model;

namespace ClaudeDeck.Tests;

public class SlotAllocatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SessionSnapshot S(string id, SessionState state, int ageSeconds = 0) =>
        new(id, id, null, null, null, state, T0, WaitReason.None, null, null, null, null, 0, null, null,
            T0.AddSeconds(-ageSeconds), null);

    [Fact]
    public void Waiting_sessions_take_priority_and_hidden_are_counted()
    {
        var a = new SlotAllocator(3);
        var r = a.Allocate([
            S("idle1", SessionState.Idle), S("idle2", SessionState.Idle), S("work", SessionState.Working),
            S("wait", SessionState.Waiting, 100), S("done", SessionState.Done),
        ]);
        var shown = r.Slots.Select(s => s?.Id).ToList();
        Assert.Contains("wait", shown);
        Assert.Contains("work", shown);
        Assert.Contains("done", shown);
        Assert.Equal(2, r.HiddenCount);
        Assert.Equal(0, r.HiddenWaiting);
    }

    [Fact]
    public void Slots_are_sticky()
    {
        var a = new SlotAllocator(3);
        var first = a.Allocate([S("x", SessionState.Idle, 3), S("y", SessionState.Idle, 2), S("z", SessionState.Idle, 1)]);
        var positions = first.Slots.Select(s => s!.Id).ToList();

        // « z » devient prioritaire : il ne doit pas changer de touche.
        var second = a.Allocate([S("x", SessionState.Idle, 3), S("y", SessionState.Idle, 2), S("z", SessionState.Waiting, 1)]);
        Assert.Equal(positions, second.Slots.Select(s => s!.Id).ToList());
    }

    [Fact]
    public void New_urgent_session_replaces_least_important()
    {
        var a = new SlotAllocator(3);
        a.Allocate([S("x", SessionState.Working), S("y", SessionState.Done), S("z", SessionState.Idle)]);
        var r = a.Allocate([S("x", SessionState.Working), S("y", SessionState.Done), S("z", SessionState.Idle), S("w", SessionState.Waiting)]);
        var ids = r.Slots.Select(s => s?.Id).ToList();
        Assert.Contains("w", ids);
        Assert.DoesNotContain("z", ids);
        Assert.Equal("w", r.Slots[ids.IndexOf("w")]!.Id);
        Assert.Equal(1, r.HiddenCount);
    }

    [Fact]
    public void Empty_slots_are_null()
    {
        var r = new SlotAllocator(3).Allocate([S("x", SessionState.Idle)]);
        Assert.Equal(2, r.Slots.Count(s => s is null));
    }
}
