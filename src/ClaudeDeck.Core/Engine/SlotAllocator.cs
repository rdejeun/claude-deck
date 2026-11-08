using ClaudeDeck.Core.Model;

namespace ClaudeDeck.Core.Engine;

/// <summary>
/// Répartit les sessions sur les N touches de la ligne du haut.
/// Priorité : en attente &gt; erreur &gt; travaille/compaction &gt; terminé &gt; au repos, puis activité la plus récente.
/// Les affectations sont « collantes » : une session déjà affichée garde sa touche tant qu'elle reste dans la sélection,
/// pour éviter que les touches ne se réordonnent en permanence.
/// </summary>
public sealed class SlotAllocator
{
    private readonly string?[] _slots;

    public SlotAllocator(int slotCount) => _slots = new string?[slotCount];

    public int SlotCount => _slots.Length;

    public static int Priority(SessionState state) => state switch
    {
        SessionState.Waiting => 0,
        SessionState.Error => 1,
        SessionState.Working => 2,
        SessionState.Compacting => 2,
        SessionState.Done => 3,
        _ => 4,
    };

    public sealed record Result(IReadOnlyList<SessionSnapshot?> Slots, int HiddenCount, int HiddenWaiting);

    public Result Allocate(IReadOnlyList<SessionSnapshot> sessions)
    {
        var desired = sessions
            .OrderBy(s => Priority(s.State))
            .ThenByDescending(s => s.LastEventAt)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Take(_slots.Length)
            .ToList();
        var desiredIds = desired.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        // 1. Libère les touches dont la session n'est plus sélectionnée.
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] is { } id && !desiredIds.Contains(id)) _slots[i] = null;
        }

        // 2. Place les nouvelles sessions dans les touches libres, dans l'ordre de priorité.
        var placed = _slots.Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var s in desired)
        {
            if (placed.Contains(s.Id)) continue;
            var free = Array.IndexOf(_slots, null);
            if (free < 0) break;
            _slots[free] = s.Id;
            placed.Add(s.Id);
        }

        var byId = sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var result = _slots.Select(id => id is not null && byId.TryGetValue(id, out var s) ? s : null).ToList();
        var hidden = sessions.Where(s => !placed.Contains(s.Id)).ToList();
        return new Result(result, hidden.Count, hidden.Count(s => s.State == SessionState.Waiting));
    }
}
