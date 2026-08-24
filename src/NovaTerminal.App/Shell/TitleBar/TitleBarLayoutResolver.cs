using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Shell.TitleBar;

/// <summary>
/// Turns the catalog plus the user's saved placement plus the currently-active toggles into the
/// concrete title bar layout. Pure by design: no Avalonia, no MainWindow, no I/O, so every rule
/// below is unit-testable without a UI thread.
/// </summary>
public static class TitleBarLayoutResolver
{
    public static TitleBarLayout Resolve(
        IReadOnlyDictionary<string, string>? states,
        IReadOnlyList<string>? order,
        IReadOnlySet<string>? activeToggleIds)
    {
        var entries = TitleBarCatalog.GetEntries();

        // Normalize the two caller-supplied lookups to OrdinalIgnoreCase up front. `order` is
        // already matched case-insensitively below via the OrdinalIgnoreCase `byId` dictionary,
        // but a plain Dictionary<string,string> / HashSet<string> (what System.Text.Json produces
        // when deserializing settings.json, and what callers pass in practice) defaults to an
        // ordinal, case-sensitive comparer. Without this, a hand-edited settings.json key like
        // "Find" would silently miss the catalog id "find" and fall back to the default — a
        // different failure mode than "unknown id" but observably identical, which undermines the
        // resolver's contract of tolerating malformed user input.
        var normalizedStates = states is null
            ? null
            : new Dictionary<string, string>(states, StringComparer.OrdinalIgnoreCase);
        var normalizedActiveToggleIds = activeToggleIds is null
            ? null
            : new HashSet<string>(activeToggleIds, StringComparer.OrdinalIgnoreCase);

        // Rule 1: saved state when present and parseable, otherwise the catalog default.
        // Rule 2 (partial): a locked entry is pinned whatever settings say.
        var resolved = entries.ToDictionary(
            e => e.Id,
            e => e.IsLocked ? TitleBarItemState.Pinned : ReadState(normalizedStates, e),
            StringComparer.OrdinalIgnoreCase);

        var pinned = new List<TitleBarCatalogEntry>();

        // Rule 2 (rest): locked entries lead, in catalog order among themselves.
        pinned.AddRange(entries.Where(e => e.IsLocked));

        // Rule 3: the saved order first, for the ids it names that are actually pinned and
        // unlocked; then everything else still pinned, in catalog order.
        var byId = entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(pinned.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

        foreach (string id in order ?? [])
        {
            if (!byId.TryGetValue(id, out var entry)) continue;                  // unknown id
            if (resolved[entry.Id] != TitleBarItemState.Pinned) continue;        // not pinned
            if (!placed.Add(entry.Id)) continue;                                 // already placed
            pinned.Add(entry);
        }

        foreach (var entry in entries)
        {
            if (resolved[entry.Id] != TitleBarItemState.Pinned) continue;
            if (!placed.Add(entry.Id)) continue;
            pinned.Add(entry);
        }

        var overflow = entries
            .Where(e => resolved[e.Id] == TitleBarItemState.Overflow)
            .ToList();

        // Rule 4 (auto-surface): an overflowed toggle that is currently ON moves into the bar,
        // at the end, and returns to the flyout when it turns off. Stated generally rather than
        // for Record specifically, so any future stateful toggle inherits it. Hidden entries are
        // never promoted — hidden means hidden.
        if (normalizedActiveToggleIds is { Count: > 0 })
        {
            var surfacing = overflow.Where(e => e.IsToggle && normalizedActiveToggleIds.Contains(e.Id)).ToList();
            foreach (var entry in surfacing)
            {
                overflow.Remove(entry);
                pinned.Add(entry);
            }
        }

        // No clamp on pinned.Count: the MaxPinned limit is enforced by the settings UI so that no
        // previously-saved configuration can silently lose an icon here. An auto-surfaced toggle
        // may push the count one past MaxPinned while it is active, which is intended.
        return new TitleBarLayout(pinned, overflow);
    }

    private static TitleBarItemState ReadState(
        IReadOnlyDictionary<string, string>? states,
        TitleBarCatalogEntry entry)
    {
        if (states is not null &&
            states.TryGetValue(entry.Id, out string? raw) &&
            Enum.TryParse(raw, ignoreCase: true, out TitleBarItemState parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        return entry.DefaultState;
    }
}
