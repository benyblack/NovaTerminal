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

        var normalizedStates = NormalizeStates(states);
        var normalizedActiveToggleIds = NormalizeActiveToggleIds(activeToggleIds);

        // Rule 1: saved state when present and parseable, otherwise the catalog default.
        // Rule 2 (partial): a locked entry is pinned whatever settings say.
        var resolved = ResolveEntryStates(entries, normalizedStates);

        // Rule 2 (rest): locked entries lead, in catalog order among themselves.
        var pinned = SeedLockedPinnedEntries(entries);

        // Rule 3: the saved order first, for the ids it names that are actually pinned and
        // unlocked; then everything else still pinned, in catalog order.
        PlacePinnedEntriesInOrder(entries, order, resolved, pinned);

        var overflow = entries
            .Where(e => resolved[e.Id] == TitleBarItemState.Overflow)
            .ToList();

        // Rule 4 (auto-surface): an overflowed toggle that is currently ON moves into the bar,
        // at the end, and returns to the flyout when it turns off. Stated generally rather than
        // for Record specifically, so any future stateful toggle inherits it. Hidden entries are
        // never promoted — hidden means hidden.
        AutoSurfaceActiveToggles(overflow, pinned, normalizedActiveToggleIds);

        // No clamp on pinned.Count: the MaxPinned limit is enforced by the settings UI so that no
        // previously-saved configuration can silently lose an icon here. An auto-surfaced toggle
        // may push the count one past MaxPinned while it is active, which is intended.
        return new TitleBarLayout(pinned, overflow);
    }

    // Normalizes the two caller-supplied lookups to OrdinalIgnoreCase up front. `order` is
    // already matched case-insensitively below via the OrdinalIgnoreCase `byId` dictionary,
    // but a plain Dictionary<string,string> / HashSet<string> (what System.Text.Json produces
    // when deserializing settings.json, and what callers pass in practice) defaults to an
    // ordinal, case-sensitive comparer. Without this, a hand-edited settings.json key like
    // "Find" would silently miss the catalog id "find" and fall back to the default — a
    // different failure mode than "unknown id" but observably identical, which undermines the
    // resolver's contract of tolerating malformed user input.
    //
    // `states` is built with an explicit assignment loop rather than the
    // `new Dictionary(source, comparer)` copy constructor: that constructor adds entries one
    // by one under the *new* comparer and throws ArgumentException the moment two source keys
    // collapse to the same key (e.g. sibling JSON entries "find" and "Find", which survive
    // deserialization intact because JSON keys are ordinal-distinct). These keys come from a
    // hand-editable file, so a typo-shaped duplicate must degrade, not crash — last one wins,
    // by plain dictionary-indexer assignment in source enumeration order.
    private static Dictionary<string, string>? NormalizeStates(IReadOnlyDictionary<string, string>? states)
    {
        if (states is null)
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in states)
        {
            normalized[kv.Key] = kv.Value;
        }

        return normalized;
    }

    // HashSet's equivalent constructor is safe here: HashSet<T>.Add (which is what the
    // IEnumerable-and-comparer constructor calls internally) silently ignores an item that
    // already collides under the given comparer instead of throwing, so a case-variant
    // duplicate id (e.g. "toggle_recording" and "Toggle_Recording") is just deduplicated.
    private static HashSet<string>? NormalizeActiveToggleIds(IReadOnlySet<string>? activeToggleIds)
        => activeToggleIds is null
            ? null
            : new HashSet<string>(activeToggleIds, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, TitleBarItemState> ResolveEntryStates(
        IReadOnlyList<TitleBarCatalogEntry> entries,
        IReadOnlyDictionary<string, string>? normalizedStates)
    {
        return entries.ToDictionary(
            e => e.Id,
            e => e.IsLocked ? TitleBarItemState.Pinned : ReadState(normalizedStates, e),
            StringComparer.OrdinalIgnoreCase);
    }

    private static List<TitleBarCatalogEntry> SeedLockedPinnedEntries(IReadOnlyList<TitleBarCatalogEntry> entries)
    {
        var pinned = new List<TitleBarCatalogEntry>();
        pinned.AddRange(entries.Where(e => e.IsLocked));
        return pinned;
    }

    private static void PlacePinnedEntriesInOrder(
        IReadOnlyList<TitleBarCatalogEntry> entries,
        IReadOnlyList<string>? order,
        IReadOnlyDictionary<string, TitleBarItemState> resolved,
        List<TitleBarCatalogEntry> pinned)
    {
        var byId = entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(pinned.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

        foreach (string? id in order ?? [])
        {
            // `order` comes straight from a hand-editable settings.json deserialized into
            // List<string>; System.Text.Json accepts a JSON `null` array element into that list
            // without complaint. TryGetValue would throw ArgumentNullException on a null key, so
            // a null id must be skipped here, exactly like an id the catalog doesn't recognize —
            // do not simplify this guard away, it is load-bearing against a startup crash.
            if (id is null) continue;                                           // null id
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
    }

    private static void AutoSurfaceActiveToggles(
        List<TitleBarCatalogEntry> overflow,
        List<TitleBarCatalogEntry> pinned,
        HashSet<string>? normalizedActiveToggleIds)
    {
        if (normalizedActiveToggleIds is not { Count: > 0 })
        {
            return;
        }

        var surfacing = overflow.Where(e => e.IsToggle && normalizedActiveToggleIds.Contains(e.Id)).ToList();
        foreach (var entry in surfacing)
        {
            overflow.Remove(entry);
            pinned.Add(entry);
        }
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
