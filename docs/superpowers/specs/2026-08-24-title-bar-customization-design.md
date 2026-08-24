# Title Bar Customization

**Date:** 2026-08-24
**Status:** Approved design, not yet implemented

## Problem

NovaTerminal's title bar has no Settings affordance. Settings is reachable only by
`Ctrl+,`, the command palette, and a "Manage Profiles…" item buried in the New Tab
flyout. A gear icon is the conventional, discoverable answer — but the bar already
carries four buttons and adding icons one at a time is how title bars get crowded.

So instead of hardcoding a fifth button, the user chooses which actions appear.

## Solution overview

A curated catalog of title-bar-worthy actions. Each one is **Pinned** (own icon in
the bar), **Overflow** (inside a `⋯` flyout), or **Hidden** (shortcut and command
palette only). Placement is explicit — the user decides, and the bar is exactly what
they configured. Settings ships Pinned by default.

## Non-goals

Deliberately excluded to keep this one implementable change:

- **Width-driven spill.** No measuring the bar at layout time and spilling the tail
  into `⋯` when the window narrows. Explicit placement only. See the pinned-count
  guardrail below for how crowding is bounded instead.
- **Drag-to-reorder.** The pinned set is 4–6 items; ▲/▼ buttons are enough, and
  drag-reorder in Avalonia is disproportionate machinery for that.
- **Promoting arbitrary `CommandRegistry` commands.** The catalog is curated so every
  entry has a hand-picked icon and tooltip. This is the natural place to grow later.
- **User-supplied icons** and **per-profile title bars.**

## The catalog

| Action | Catalog id | Shortcut key | Default |
|---|---|---|---|
| New Tab (`+`, keeps its flyout) | `new_tab` | `new_tab` | **Pinned, locked** |
| Tab List | `open_tab_list` | `open_tab_list` | Pinned |
| Connections | `connections` | `connections` | Pinned |
| Settings | `settings` | `settings` | Pinned |
| Record Session | `toggle_recording` | `toggle_recording` | Overflow *(toggle)* |
| Command Palette | `command_palette` | `command_palette` | Overflow |
| Find in Terminal | `find` | `find` | Overflow |
| Split Vertical | `split_vertical` | `split_vertical` | Overflow |
| Split Horizontal | `split_horizontal` | `split_horizontal` | Overflow |
| Remote Files | `sftp_remote_files` | — | Overflow |
| Transfers | `sftp_transfers` | — | Overflow |
| Agent Activity | `agent_activity` | — | Hidden |

Defaults give 4 pinned plus `⋯` — one button more than today, with the gear present
and every existing icon still working.

**Catalog ids are their own namespace, not `CommandRegistry` ids.** They mostly
coincide, but not always: `Remote Files` and `Transfers` register with an empty id and
so fall back to their titles (`"SFTP: Toggle Remote Files"`), while `Command Palette`
and `Agent Activity` have no registry entry at all. Owning the ids keeps the persisted
config stable regardless of how the registry is refactored.

The `Shortcut key` column is a `GetEffectiveShortcutBinding` lookup key, used only to
display the current binding in the settings row. It is empty for actions with no
binding.

### Icon geometry

Path data for Tab List, Record, and Connections moves out of
`MainWindow.axaml` into the catalog unchanged. New entries need Material Design Icons
geometries, matching the existing icons' source: gear, `⋯`, palette/search,
magnifier, two split glyphs, folder-remote, transfer arrows, and a robot/activity
glyph. These are sourced during implementation, not invented here.

## Data model

### `Shell/TitleBarCatalog.cs` (new)

No Avalonia dependency.

```csharp
enum TitleBarItemState { Pinned, Overflow, Hidden }

sealed record TitleBarCatalogEntry(
    string Id,
    string Title,
    string IconGeometry,
    double IconSize,
    string ShortcutKey,
    string ShortcutDefault,
    TitleBarItemState DefaultState,
    bool IsLocked,
    bool IsToggle);

static class TitleBarCatalog
{
    public static IReadOnlyList<TitleBarCatalogEntry> Entries { get; }  // default order
}
```

`new_tab` is the only `IsLocked` entry. `toggle_recording` is the only `IsToggle`
entry today.

### `Shell/TerminalSettings.cs`

```csharp
public Dictionary<string, string> TitleBarItems { get; set; } = new();  // id → state
public List<string> TitleBarOrder { get; set; } = new();                // pinned order
```

Both empty by default, meaning "use catalog defaults".

**Deltas, not the resolved list.** Storing only what the user changed means a catalog
entry added in a future version appears for existing users at its default state with
no migration step.

`Dictionary<string,string>` is already registered in `Shell/AppJsonContext.cs` for
`Keybindings`, so AOT serialization needs nothing new there. `List<string>` must be
added to that context.

`TitleBarItems` is read by `MainWindow`, not by panes, so it is unaffected by the
`BuildEffectiveSettings` whitelist in `Controls/TerminalPane.axaml.cs`.

## Resolution — `Shell/TitleBarLayoutModel.cs` (new)

One pure function. No Avalonia, no `MainWindow`, no I/O.

```csharp
TitleBarLayout Resolve(
    IReadOnlyDictionary<string, string> settingsStates,
    IReadOnlyList<string> settingsOrder,
    ISet<string> activeToggleIds);

sealed record TitleBarLayout(
    IReadOnlyList<TitleBarCatalogEntry> Pinned,
    IReadOnlyList<TitleBarCatalogEntry> Overflow,
    bool ShowOverflowButton);
```

Rules, applied in order:

1. Each catalog entry's state is `settingsStates[id]` when present and parseable,
   otherwise its `DefaultState`. Ids in settings that are not in the catalog are
   ignored. Unparseable state strings fall back to the default.
2. Locked entries are forced to `Pinned` and sorted first, whatever settings say —
   including an attempt to hide them.
3. Pinned order follows `settingsOrder` for the ids it names; entries it does not
   name follow in catalog order, after the named ones.
4. Any `Overflow` entry whose id is in `activeToggleIds` is appended to `Pinned`.
   This is the **auto-surface** rule, stated generally rather than for Record
   specifically: an overflowed toggle that is currently ON shows in the bar and drops
   back to `⋯` when it turns off.
5. `ShowOverflowButton` is true when the `Overflow` list is non-empty after step 4.

Every approved behavior lives here, testable with no UI.

`Resolve` does **not** clamp the pinned count. The 8-item limit is a UI-level
guardrail (below), so no previously-saved config can silently lose an icon. An
auto-surfaced toggle from step 4 can therefore push the bar to 9 while it is active;
that is intended, since the guardrail bounds what the user *configures*, not the
transient active-toggle state.

## Rendering — `MainWindow`

`MainWindow.axaml`'s title bar `StackPanel` loses its four hardcoded children and
becomes an empty `x:Name="TitleBarItemsHost"`, plus a `ContextMenu` holding a single
"Customize Title Bar…" item.

The `+` button's `MenuFlyout` stays declared in XAML. It is locked, it carries real
content ("New SSH Connection…", "Manage Profiles…", "Agent Activity…"), and
re-authoring that flyout in C# would be churn for nothing.

`RebuildTitleBar()` walks the resolved layout and, per entry, builds a `Button`
carrying the styling already inline today — transparent background, no border,
`Focusable="False"` so clicks never steal keyboard focus from the terminal, 32×32,
`CornerRadius="4"`, `Margin="4,0,0,0"` — wrapping a `PathIcon` built from
`IconGeometry`. `ToolTip.Tip` is the title plus the resolved shortcut.

Handlers come from a `Dictionary<string, Action>` built once in the constructor.
**Not from `CommandRegistry`:** `SetupCommandPalette()` is lazy, running on
palette-open and settings-save rather than at startup, so a title bar resolving its
actions through the registry would come up dead on a cold start. A catalog id with no
handler is logged via `AppLogger` and skipped, never thrown.

`RebuildTitleBar()` is called:

- at the end of the constructor — initial-window UI must be built there, not from
  `SetupCommandPalette()`;
- on settings save;
- when a toggle's active state flips, so Record can surface and retreat.

`⋯` is a `Button` whose `MenuFlyout` is populated from `Overflow`, each item showing
the same icon, title, and shortcut as the pinned form.

## Settings UI — Appearance tab

A `TITLE BAR` section in the existing Appearance tab, following the row idiom already
used by the Shortcuts tab. One row per catalog entry:

- icon preview,
- title,
- current shortcut (dimmed; blank when unbound),
- a three-way state `ComboBox` — Pinned / Overflow / Hidden,
- ▲ / ▼, enabled only for unlocked pinned rows.

Locked rows show their state as static text instead of a `ComboBox`.

Changes write `TitleBarItems` and `TitleBarOrder` on save, and `MainWindow` rebuilds.

**Pinned-count guardrail:** the `ComboBox` refuses to select Pinned when 8 items are
already pinned — counting the locked `new_tab` entry — and says why. With explicit placement and no width-driven spill,
nothing else stops a pinned set from running into the tab strip — 8 × 36px plus the
140px caption-button reserve is roughly where that starts to hurt.

### Right-click entry point

Right-clicking the title bar opens the context menu; "Customize Title Bar…" calls
`OpenSettings(0)` — Appearance is already index 0 — and scrolls the `TITLE BAR`
section into view. This is how people will actually find the feature; the settings
section alone is not discoverable.

## Error handling

| Case | Behavior |
|---|---|
| Unknown id in `TitleBarItems` | Ignored |
| Unparseable state string | Falls back to the entry's `DefaultState` |
| Settings tries to hide a locked entry | Forced back to Pinned |
| `TitleBarOrder` names unknown or non-pinned ids | Those names ignored |
| Catalog id with no wired handler | Logged, entry skipped |
| Corrupt settings file | Existing `TerminalSettings` load path already handles this; empty dictionaries yield catalog defaults |

## Testing

In `NovaTerminal.App.Tests`. All pure logic — no Avalonia, no Skia, so it is safe on
the Linux gating leg.

`TitleBarLayoutModelTests`:

- empty settings produces the documented defaults (4 pinned, `⋯` shown);
- unknown id in settings is ignored;
- catalog entry absent from settings falls back to its default;
- garbage state string falls back to the default;
- explicit `TitleBarOrder` is honored, and unnamed entries follow in catalog order;
- locked entry is first even when settings order puts it last;
- locked entry stays pinned when settings mark it Hidden;
- an active toggle is promoted out of overflow, and returns when inactive;
- `ShowOverflowButton` is false when everything is Pinned or Hidden;
- `ShowOverflowButton` is false when the only overflow entry is currently
  auto-surfaced.

`TitleBarCatalogTests`:

- ids are unique and non-empty;
- every entry has non-empty title and geometry;
- exactly one entry is locked;
- every entry's `DefaultState` is a valid enum value.

## Files

**New**

- `src/NovaTerminal.App/Shell/TitleBarCatalog.cs`
- `src/NovaTerminal.App/Shell/TitleBarLayoutModel.cs`
- `tests/NovaTerminal.App.Tests/TitleBarLayoutModelTests.cs`
- `tests/NovaTerminal.App.Tests/TitleBarCatalogTests.cs`

**Modified**

- `src/NovaTerminal.App/Shell/TerminalSettings.cs` — two properties
- `src/NovaTerminal.App/Shell/AppJsonContext.cs` — `List<string>`
- `src/NovaTerminal.App/MainWindow.axaml` — title bar host, context menu
- `src/NovaTerminal.App/MainWindow.axaml.cs` — `RebuildTitleBar`, handler map,
  context menu wiring, rebuild on save and on toggle change
- `src/NovaTerminal.App/SettingsWindow.axaml` — `TITLE BAR` section
- `src/NovaTerminal.App/SettingsWindow.axaml.cs` — populate and persist

No new test project, so `ci.yml`'s artifact path list and unit-test loop need no
changes.
