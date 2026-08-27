using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Shell.TitleBar;

/// <summary>
/// Pure draft state for the title bar settings UI: the pending per-entry placement (Pinned /
/// Overflow / Hidden) and the pinned display order, before Save commits them to
/// <c>TerminalSettings</c>. Deliberately free of Avalonia, like the rest of Shell/TitleBar, so the
/// MaxPinned cap, the locked-index-0 reorder guard, and the deltas-only save computation can be
/// unit tested without a UI thread. SettingsWindow owns everything else: controls, styling, the
/// validation label, and the SelectionChanged re-entrancy guard.
/// </summary>
public sealed class TitleBarDraftState
{
    private readonly Dictionary<string, TitleBarItemState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pinnedOrder = [];

    /// <summary>
    /// Reseeds the draft from a resolved layout: every catalog id is marked Pinned or Overflow
    /// per the layout's own placement, and anything neither names is Hidden. The pinned order is
    /// taken directly from <paramref name="layout"/>.Pinned. Derives from the resolver's output
    /// rather than re-reading settings independently, so the draft can never disagree with the
    /// resolver about a case-variant id.
    /// </summary>
    public void SeedFrom(TitleBarLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _states.Clear();
        _pinnedOrder.Clear();

        var pinnedIds = new HashSet<string>(layout.Pinned.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        var overflowIds = new HashSet<string>(layout.Overflow.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

        foreach (string id in TitleBarCatalog.GetEntries().Select(entry => entry.Id))
        {
            _states[id] = ResolveSeedState(id, pinnedIds, overflowIds);
        }

        _pinnedOrder.AddRange(layout.Pinned.Select(e => e.Id));
    }

    private static TitleBarItemState ResolveSeedState(
        string id, HashSet<string> pinnedIds, HashSet<string> overflowIds)
    {
        if (pinnedIds.Contains(id))
        {
            return TitleBarItemState.Pinned;
        }

        if (overflowIds.Contains(id))
        {
            return TitleBarItemState.Overflow;
        }

        return TitleBarItemState.Hidden;
    }

    /// <summary>The entry's current draft state. Throws if <paramref name="id"/> was never seeded.</summary>
    public TitleBarItemState GetState(string id) => _states[id];

    public int CountPinned() => _states.Count(kv => kv.Value == TitleBarItemState.Pinned);

    /// <summary>
    /// The ids in the order the settings rows should render: pinned entries first in their
    /// pinned order, then every other catalog entry in catalog order.
    /// </summary>
    public IReadOnlyList<string> GetDisplayOrder()
    {
        var ordered = _pinnedOrder
            .Where(id => _states.TryGetValue(id, out var s) && s == TitleBarItemState.Pinned)
            .ToList();

        ordered.AddRange(TitleBarCatalog.GetEntries()
            .Select(e => e.Id)
            .Where(id => !ordered.Contains(id, StringComparer.OrdinalIgnoreCase)));

        return ordered;
    }

    /// <summary>
    /// Attempts to move <paramref name="id"/> to <paramref name="next"/>. Returns <c>false</c>,
    /// leaving the draft unchanged, when the move would pin more than
    /// <see cref="TitleBarCatalog.MaxPinned"/> entries — the locked New Tab entry counts toward
    /// that cap. The caller (the settings UI) is responsible for surfacing the rejection.
    /// </summary>
    public bool TrySetState(string id, TitleBarItemState next)
    {
        if (next == TitleBarItemState.Pinned && CountPinned() >= TitleBarCatalog.MaxPinned)
        {
            return false;
        }

        _states[id] = next;

        if (next == TitleBarItemState.Pinned)
        {
            if (!_pinnedOrder.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                _pinnedOrder.Add(id);
            }
        }
        else
        {
            _pinnedOrder.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    /// <summary>
    /// Moves the pinned entry <paramref name="id"/> by <paramref name="delta"/> places (-1 up,
    /// +1 down) within the pinned order. Index 0 is the locked New Tab entry, which never moves
    /// and can never be displaced — a move that would touch index 0, or move an entry that is not
    /// currently pinned, is silently ignored.
    /// </summary>
    public void MovePinned(string id, int delta)
    {
        int from = _pinnedOrder.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        int to = from + delta;

        if (from <= 0 || to <= 0 || to >= _pinnedOrder.Count)
        {
            return;
        }

        (_pinnedOrder[from], _pinnedOrder[to]) = (_pinnedOrder[to], _pinnedOrder[from]);
    }

    /// <summary>
    /// The settings delta to persist: deltas only, so an id at its catalog default (or locked) is
    /// omitted and a future catalog change reaches existing users without a migration. Returns the
    /// concrete <see cref="Dictionary{TKey,TValue}"/> type that <c>TerminalSettings.TitleBarItems</c>
    /// is declared as, so the caller can assign it directly.
    /// </summary>
    public Dictionary<string, string> BuildSaveDelta()
    {
        return TitleBarCatalog.GetEntries()
            .Where(e => !e.IsLocked && _states[e.Id] != e.DefaultState)
            .ToDictionary(e => e.Id, e => _states[e.Id].ToString(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pinned order to persist: only ids currently pinned, in their draft order. Returns the
    /// concrete <see cref="List{T}"/> type that <c>TerminalSettings.TitleBarOrder</c> is declared
    /// as, so the caller can assign it directly.
    /// </summary>
    public List<string> BuildSaveOrder()
    {
        return _pinnedOrder
            .Where(id => _states.TryGetValue(id, out var s) && s == TitleBarItemState.Pinned)
            .ToList();
    }
}
