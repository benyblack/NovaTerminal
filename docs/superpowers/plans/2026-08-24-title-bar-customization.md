# Title Bar Customization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user choose which action icons appear in NovaTerminal's title bar — Pinned, Overflow (`⋯` flyout), or Hidden — so Settings gains a gear icon without the bar getting crowded.

**Architecture:** A declarative catalog of title-bar-worthy actions plus a pure resolver that turns catalog + saved settings + active-toggle state into a `{Pinned, Overflow}` layout. `MainWindow` renders whatever the resolver returns; a view factory builds the buttons so rendering is testable without instantiating `MainWindow`. Settings stores only the user's deltas.

**Tech Stack:** C#, .NET 10 preview, Avalonia 11, xunit.v3 + `Avalonia.Headless.XUnit`, `System.Text.Json` source-generated serialization.

**Spec:** `docs/superpowers/specs/2026-08-24-title-bar-customization-design.md`

## Global Constraints

- **Build only via the wrapper scripts.** `scripts/build.ps1 <args>` (PowerShell) or `scripts/build.sh <args>` (bash). A raw `dotnet build` leaves daemons holding stdout and hangs when a parent captures output. Never use raw `dotnet build`.
- **Fresh worktrees need a CLI restore first.** `scripts/build.ps1 restore src/NovaTerminal.Cli` — the `BuildCliShim` target runs a nested `dotnet build --no-restore` on `NovaTerminal.Cli` and fails with `NETSDK1004` otherwise. Already done in this worktree.
- **Never run the whole test suite.** Solution-wide `dotnet test` takes 20–30 minutes. Run only `tests/NovaTerminal.App.Tests` and filter to the class under test.
- **`Shell/TitleBar/` code must not reference Avalonia.** Catalog, resolver, and state types stay pure so their tests need no UI thread. Only `TitleBarViewFactory` touches Avalonia.
- **Do not resolve actions through `CommandRegistry`.** `SetupCommandPalette()` is lazy — it runs on palette-open and settings-save, never at startup (see the comment at `MainWindow.axaml.cs:2207`). A title bar reading the registry comes up dead on a cold start.
- **No new test project.** Everything lands in `tests/NovaTerminal.App.Tests`, so `ci.yml`'s artifact path list and unit-test loop need no changes.
- **Follow the folder's existing style:** file-scoped namespaces, `sealed record` primitives, collection expressions (`[...]`) for static tables. Mirror `src/NovaTerminal.App/Shell/Shortcuts/`.
- Test namespace is `NovaTerminal.Tests`; test assembly root namespace is `NovaTerminal.AppTests`.

## Deviations from the spec (deliberate, adopted here)

Three refinements found while reading the codebase. They do not change any approved behavior.

1. **Files live in `Shell/TitleBar/`, not flat in `Shell/`.** `Shell/Shortcuts/` is already Catalog + CatalogEntry + Resolver — the exact shape this feature needs. Mirroring it beats inventing a flat layout.
2. **`TitleBarCatalogEntry` has no `ShortcutDefault` field.** `ShortcutCatalog` (`Shell/Shortcuts/ShortcutCatalog.cs`) is already authoritative for default bindings and already contains every id needed: `new_tab`, `open_tab_list`, `connections`, `settings`, `toggle_recording`, `command_palette`, `find`, `split_vertical`, `split_horizontal`. Duplicating defaults in a second catalog would let them drift.
3. **Button construction is extracted into `TitleBarViewFactory`.** `MainWindow` is never instantiated in headless tests — it spawns PTYs, SSH, and the agent host. Putting the button building in a static factory gives the rendering real test coverage and keeps `MainWindow` thin.

## File Structure

**Create**

| File | Responsibility |
|---|---|
| `src/NovaTerminal.App/Shell/TitleBar/TitleBarItemState.cs` | The three-state enum |
| `src/NovaTerminal.App/Shell/TitleBar/TitleBarCatalogEntry.cs` | One catalog row |
| `src/NovaTerminal.App/Shell/TitleBar/TitleBarCatalog.cs` | The static table of 12 entries |
| `src/NovaTerminal.App/Shell/TitleBar/TitleBarLayout.cs` | Resolver output |
| `src/NovaTerminal.App/Shell/TitleBar/TitleBarLayoutResolver.cs` | Pure catalog + settings + toggles → layout |
| `src/NovaTerminal.App/Shell/TitleBar/TitleBarShortcuts.cs` | Shortcut-label lookup |
| `src/NovaTerminal.App/Shell/TitleBar/TitleBarViewFactory.cs` | Builds buttons and the `⋯` flyout |
| `tests/NovaTerminal.App.Tests/TitleBarCatalogTests.cs` | Catalog invariants |
| `tests/NovaTerminal.App.Tests/TitleBarLayoutResolverTests.cs` | Every resolution rule |
| `tests/NovaTerminal.App.Tests/TitleBarSettingsRoundTripTests.cs` | Persistence |
| `tests/NovaTerminal.App.Tests/TitleBarViewFactoryTests.cs` | Rendering, headless |

**Modify**

| File | Change |
|---|---|
| `src/NovaTerminal.App/Shell/TerminalSettings.cs` | `TitleBarItems`, `TitleBarOrder` |
| `src/NovaTerminal.App/Shell/AppJsonContext.cs` | `[JsonSerializable(typeof(List<string>))]` |
| `src/NovaTerminal.App/MainWindow.axaml` | Title bar becomes an empty host + `ContextMenu` |
| `src/NovaTerminal.App/MainWindow.axaml.cs` | Handler map, `RebuildTitleBar`, auto-surface, rebuild on save |
| `src/NovaTerminal.App/SettingsWindow.axaml` | `TITLE BAR` section shell in the Appearance tab |
| `src/NovaTerminal.App/SettingsWindow.axaml.cs` | Populate rows, persist on save |

---

## Task 1: Catalog primitives

**Files:**
- Create: `src/NovaTerminal.App/Shell/TitleBar/TitleBarItemState.cs`
- Create: `src/NovaTerminal.App/Shell/TitleBar/TitleBarCatalogEntry.cs`
- Create: `src/NovaTerminal.App/Shell/TitleBar/TitleBarCatalog.cs`
- Test: `tests/NovaTerminal.App.Tests/TitleBarCatalogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `enum TitleBarItemState { Pinned, Overflow, Hidden }`; `sealed record TitleBarCatalogEntry(string Id, string Title, string IconGeometry, double IconSize, string ShortcutKey, TitleBarItemState DefaultState, bool IsLocked, bool IsToggle)`; `static IReadOnlyList<TitleBarCatalogEntry> TitleBarCatalog.GetEntries()`. Namespace `NovaTerminal.Shell.TitleBar`.

- [ ] **Step 1: Write the failing test**

`tests/NovaTerminal.App.Tests/TitleBarCatalogTests.cs`:

```csharp
using System.Linq;
using NovaTerminal.Shell.Shortcuts;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests
{
    public class TitleBarCatalogTests
    {
        [Fact]
        public void Ids_AreUniqueAndNonEmpty()
        {
            var entries = TitleBarCatalog.GetEntries();

            Assert.NotEmpty(entries);
            Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Id)));
            Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        }

        [Fact]
        public void EveryEntry_HasTitleAndGeometry()
        {
            Assert.All(TitleBarCatalog.GetEntries(), e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Title));
                Assert.False(string.IsNullOrWhiteSpace(e.IconGeometry));
                Assert.True(e.IconSize > 0);
            });
        }

        [Fact]
        public void ExactlyOneEntry_IsLocked_AndItIsNewTab()
        {
            var locked = TitleBarCatalog.GetEntries().Where(e => e.IsLocked).ToList();

            Assert.Single(locked);
            Assert.Equal("new_tab", locked[0].Id);
        }

        [Fact]
        public void LockedEntry_DefaultsToPinned()
        {
            var locked = TitleBarCatalog.GetEntries().Single(e => e.IsLocked);

            Assert.Equal(TitleBarItemState.Pinned, locked.DefaultState);
        }

        [Fact]
        public void DefaultPinnedSet_IsTheFourAgreedEntries()
        {
            var pinned = TitleBarCatalog.GetEntries()
                .Where(e => e.DefaultState == TitleBarItemState.Pinned)
                .Select(e => e.Id)
                .ToList();

            Assert.Equal(new[] { "new_tab", "open_tab_list", "connections", "settings" }, pinned);
        }

        [Fact]
        public void ShortcutKeys_WhenPresent_ExistInShortcutCatalog()
        {
            var known = ShortcutCatalog.GetEntries().Select(e => e.CommandId).ToHashSet();

            Assert.All(
                TitleBarCatalog.GetEntries().Where(e => !string.IsNullOrEmpty(e.ShortcutKey)),
                e => Assert.Contains(e.ShortcutKey, known));
        }

        [Fact]
        public void ToggleEntries_AreOnlyRecording()
        {
            var toggles = TitleBarCatalog.GetEntries().Where(e => e.IsToggle).Select(e => e.Id).ToList();

            Assert.Equal(new[] { "toggle_recording" }, toggles);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TitleBarCatalogTests"
```

Expected: compile failure — `TitleBarCatalog` and `TitleBarItemState` do not exist.

- [ ] **Step 3: Write the enum**

`src/NovaTerminal.App/Shell/TitleBar/TitleBarItemState.cs`:

```csharp
namespace NovaTerminal.Shell.TitleBar;

/// <summary>Where a title bar catalog entry appears.</summary>
public enum TitleBarItemState
{
    /// <summary>Its own icon button in the title bar.</summary>
    Pinned,

    /// <summary>Inside the overflow (…) flyout.</summary>
    Overflow,

    /// <summary>Not in the title bar at all; still reachable by shortcut and command palette.</summary>
    Hidden,
}
```

- [ ] **Step 4: Write the entry record**

`src/NovaTerminal.App/Shell/TitleBar/TitleBarCatalogEntry.cs`:

```csharp
namespace NovaTerminal.Shell.TitleBar;

/// <summary>
/// One customizable title bar action. <paramref name="ShortcutKey"/> is a
/// <see cref="Shortcuts.ShortcutCatalog"/> command id used only to display the current binding —
/// empty for actions with no binding. Ids here are their own namespace, deliberately not
/// CommandRegistry ids: two registry entries register with an empty id (so theirs falls back to
/// their title) and two of these actions have no registry entry at all.
/// </summary>
public sealed record TitleBarCatalogEntry(
    string Id,
    string Title,
    string IconGeometry,
    double IconSize,
    string ShortcutKey,
    TitleBarItemState DefaultState,
    bool IsLocked,
    bool IsToggle);
```

- [ ] **Step 5: Write the catalog**

`src/NovaTerminal.App/Shell/TitleBar/TitleBarCatalog.cs`. The Tab List, Record, and Connections geometries are moved verbatim from `MainWindow.axaml`; the rest are Material Design Icons paths except the two split glyphs, which are hand-authored rectangles.

```csharp
using System.Collections.Generic;

namespace NovaTerminal.Shell.TitleBar;

public static class TitleBarCatalog
{
    // mdi-plus is not used for new_tab: that button renders the literal "+" glyph at FontSize 18,
    // which is what ships today. Its geometry is unused but must stay non-empty so the catalog
    // invariants hold uniformly; TitleBarViewFactory special-cases the id.
    private const string GeometryPlus =
        "M19,13H13V19H11V13H5V11H11V5H13V11H19V13Z";

    private const string GeometryTabList =
        "M4,6H20V8H4V6M4,11H20V13H4V11M4,16H20V18H4V16Z";

    private const string GeometryRecord =
        "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z";

    private const string GeometryConnections =
        "M19,15H5C3.34,15 2,16.34 2,18V20C2,21.66 3.34,23 5,23H19C20.66,23 22,21.66 22,20V18C22,16.34 20.66,15 19,15M8,20C7.45,20 7,19.55 7,19C7,18.45 7.45,18 8,18H9C9.55,18 10,18.45 10,19C10,19.55 9.55,20 9,20H8M19,9H5C3.34,9 2,10.34 2,12V14C2,15.66 3.34,17 5,17H19C20.66,17 22,15.66 22,14V12C22,10.34 20.66,9 19,9M8,14C7.45,14 7,13.55 7,13C7,12.45 7.45,12 8,12H9C9.55,12 10,12.45 10,13C10,13.55 9.55,14 9,14H8M19,3H5C3.34,3 2,4.34 2,6V8C2,9.66 3.34,11 5,11H19C20.66,11 22,9.66 22,8V6C22,4.34 20.66,3 19,3M8,8C7.45,8 7,7.55 7,7C7,6.45 7.45,6 8,6H9C9.55,6 10,6.45 10,7C10,7.55 9.55,8 9,8H8Z";

    // mdi-cog
    private const string GeometrySettings =
        "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z";

    // mdi-apps
    private const string GeometryCommandPalette =
        "M16,20H20V16H16M16,14H20V10H16M10,8H14V4H10M16,8H20V4H16M10,14H14V10H10M4,8H8V4H4M4,14H8V10H4M4,20H8V16H4M10,20H14V16H10V20Z";

    // mdi-magnify
    private const string GeometryFind =
        "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z";

    // Hand-authored: two panes side by side. "Vertical" names the divider, matching the
    // split_vertical command, which calls SplitPane(Orientation.Horizontal).
    private const string GeometrySplitVertical =
        "M3,3H11V21H3V3M13,3H21V21H13V3Z";

    // Hand-authored: two panes stacked.
    private const string GeometrySplitHorizontal =
        "M3,3H21V11H3V3M3,13H21V21H3V13Z";

    // mdi-folder
    private const string GeometryRemoteFiles =
        "M20,18H4V8H20M20,6H12L10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6Z";

    // mdi-swap-vertical
    private const string GeometryTransfers =
        "M9,3L5,7H8V14H10V7H13M16,17V10H14V17H11L15,21L19,17H16Z";

    // mdi-pulse
    private const string GeometryAgentActivity =
        "M3,13H5.79L10.1,4.79L11.28,13.75L14.5,9.66L17.83,13H21V15H17L14.67,12.67L9.92,18.63L8.32,6.43L7,15H3V13Z";

    /// <summary>mdi-dots-horizontal, for the overflow button itself. Not a catalog entry.</summary>
    public const string OverflowGeometry =
        "M16,12A2,2 0 0,1 18,10A2,2 0 0,1 20,12A2,2 0 0,1 18,14A2,2 0 0,1 16,12M10,12A2,2 0 0,1 12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12M4,12A2,2 0 0,1 6,10A2,2 0 0,1 8,12A2,2 0 0,1 6,14A2,2 0 0,1 4,12Z";

    /// <summary>The id of the New Tab entry, which is locked and renders its XAML-declared flyout.</summary>
    public const string NewTabId = "new_tab";

    private static readonly IReadOnlyList<TitleBarCatalogEntry> Entries =
    [
        new(NewTabId, "New Tab", GeometryPlus, 16, "new_tab", TitleBarItemState.Pinned, IsLocked: true, IsToggle: false),
        new("open_tab_list", "Tab List", GeometryTabList, 16, "open_tab_list", TitleBarItemState.Pinned, IsLocked: false, IsToggle: false),
        new("connections", "Connections", GeometryConnections, 16, "connections", TitleBarItemState.Pinned, IsLocked: false, IsToggle: false),
        new("settings", "Settings", GeometrySettings, 16, "settings", TitleBarItemState.Pinned, IsLocked: false, IsToggle: false),
        new("toggle_recording", "Record Session", GeometryRecord, 14, "toggle_recording", TitleBarItemState.Overflow, IsLocked: false, IsToggle: true),
        new("command_palette", "Command Palette", GeometryCommandPalette, 16, "command_palette", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("find", "Find in Terminal", GeometryFind, 16, "find", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("split_vertical", "Split Vertical", GeometrySplitVertical, 16, "split_vertical", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("split_horizontal", "Split Horizontal", GeometrySplitHorizontal, 16, "split_horizontal", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("sftp_remote_files", "Remote Files", GeometryRemoteFiles, 16, "", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("sftp_transfers", "Transfers", GeometryTransfers, 16, "", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("agent_activity", "Agent Activity", GeometryAgentActivity, 16, "", TitleBarItemState.Hidden, IsLocked: false, IsToggle: false),
    ];

    /// <summary>The catalog in default display order.</summary>
    public static IReadOnlyList<TitleBarCatalogEntry> GetEntries() => Entries;

    /// <summary>The most items the settings UI will let the user pin. See the plan's guardrail note.</summary>
    public const int MaxPinned = 8;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TitleBarCatalogTests"
```

Expected: 7 passed.

- [ ] **Step 7: Commit**

```bash
git add src/NovaTerminal.App/Shell/TitleBar tests/NovaTerminal.App.Tests/TitleBarCatalogTests.cs
git commit -m "feat(ui): add the title bar action catalog"
```

---

## Task 2: Layout resolver

**Files:**
- Create: `src/NovaTerminal.App/Shell/TitleBar/TitleBarLayout.cs`
- Create: `src/NovaTerminal.App/Shell/TitleBar/TitleBarLayoutResolver.cs`
- Test: `tests/NovaTerminal.App.Tests/TitleBarLayoutResolverTests.cs`

**Interfaces:**
- Consumes: `TitleBarCatalog.GetEntries()`, `TitleBarCatalogEntry`, `TitleBarItemState` from Task 1.
- Produces: `sealed record TitleBarLayout(IReadOnlyList<TitleBarCatalogEntry> Pinned, IReadOnlyList<TitleBarCatalogEntry> Overflow)` with a computed `bool ShowOverflowButton`; `static TitleBarLayout TitleBarLayoutResolver.Resolve(IReadOnlyDictionary<string,string>? states, IReadOnlyList<string>? order, IReadOnlySet<string>? activeToggleIds)`.

- [ ] **Step 1: Write the failing test**

`tests/NovaTerminal.App.Tests/TitleBarLayoutResolverTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests
{
    public class TitleBarLayoutResolverTests
    {
        private static TitleBarLayout Resolve(
            Dictionary<string, string>? states = null,
            List<string>? order = null,
            params string[] activeToggles)
            => TitleBarLayoutResolver.Resolve(states, order, activeToggles.ToHashSet());

        private static IEnumerable<string> Ids(IEnumerable<TitleBarCatalogEntry> entries)
            => entries.Select(e => e.Id);

        [Fact]
        public void EmptySettings_YieldsCatalogDefaults()
        {
            var layout = Resolve();

            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "connections", "settings" },
                Ids(layout.Pinned));
            Assert.True(layout.ShowOverflowButton);
            Assert.DoesNotContain("agent_activity", Ids(layout.Overflow));
        }

        [Fact]
        public void NullSettings_YieldsCatalogDefaults()
        {
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            Assert.Equal(4, layout.Pinned.Count);
        }

        [Fact]
        public void UnknownIdInSettings_IsIgnored()
        {
            var layout = Resolve(new() { ["not_a_real_action"] = "Pinned" });

            Assert.Equal(4, layout.Pinned.Count);
            Assert.DoesNotContain("not_a_real_action", Ids(layout.Pinned));
        }

        [Fact]
        public void EntryAbsentFromSettings_UsesItsDefaultState()
        {
            var layout = Resolve(new() { ["find"] = "Pinned" });

            Assert.Contains("find", Ids(layout.Pinned));
            Assert.Contains("command_palette", Ids(layout.Overflow));
        }

        [Fact]
        public void UnparseableStateString_FallsBackToDefault()
        {
            var layout = Resolve(new() { ["find"] = "banana" });

            Assert.Contains("find", Ids(layout.Overflow));
            Assert.DoesNotContain("find", Ids(layout.Pinned));
        }

        [Fact]
        public void StateString_IsCaseInsensitive()
        {
            var layout = Resolve(new() { ["find"] = "pinned" });

            Assert.Contains("find", Ids(layout.Pinned));
        }

        [Fact]
        public void HiddenEntry_AppearsInNeitherList()
        {
            var layout = Resolve(new() { ["connections"] = "Hidden" });

            Assert.DoesNotContain("connections", Ids(layout.Pinned));
            Assert.DoesNotContain("connections", Ids(layout.Overflow));
        }

        [Fact]
        public void ExplicitOrder_IsHonored_AndUnnamedEntriesFollowInCatalogOrder()
        {
            var layout = Resolve(
                new() { ["find"] = "Pinned" },
                new List<string> { "settings", "connections" });

            // new_tab is locked and leads; then the named ids in their given order;
            // then the remaining pinned entries in catalog order.
            Assert.Equal(
                new[] { "new_tab", "settings", "connections", "open_tab_list", "find" },
                Ids(layout.Pinned));
        }

        [Fact]
        public void OrderNamingUnknownOrNonPinnedIds_IgnoresThem()
        {
            var layout = Resolve(
                order: new List<string> { "nope", "agent_activity", "settings" });

            Assert.Equal(
                new[] { "new_tab", "settings", "open_tab_list", "connections" },
                Ids(layout.Pinned));
        }

        [Fact]
        public void LockedEntry_LeadsEvenWhenOrderPutsItLast()
        {
            var layout = Resolve(
                order: new List<string> { "settings", "connections", "open_tab_list", "new_tab" });

            Assert.Equal("new_tab", layout.Pinned[0].Id);
        }

        [Fact]
        public void LockedEntry_StaysPinned_WhenSettingsTryToHideIt()
        {
            var layout = Resolve(new() { ["new_tab"] = "Hidden" });

            Assert.Contains("new_tab", Ids(layout.Pinned));
            Assert.DoesNotContain("new_tab", Ids(layout.Overflow));
        }

        [Fact]
        public void LockedEntry_StaysPinned_WhenSettingsMoveItToOverflow()
        {
            var layout = Resolve(new() { ["new_tab"] = "Overflow" });

            Assert.Contains("new_tab", Ids(layout.Pinned));
            Assert.DoesNotContain("new_tab", Ids(layout.Overflow));
        }

        [Fact]
        public void ActiveToggle_IsPromotedOutOfOverflow_ToTheEndOfPinned()
        {
            var layout = Resolve(activeToggles: "toggle_recording");

            Assert.Equal("toggle_recording", layout.Pinned[^1].Id);
            Assert.DoesNotContain("toggle_recording", Ids(layout.Overflow));
        }

        [Fact]
        public void InactiveToggle_StaysInOverflow()
        {
            var layout = Resolve();

            Assert.Contains("toggle_recording", Ids(layout.Overflow));
        }

        [Fact]
        public void ActiveToggle_ThatIsHidden_IsNotPromoted()
        {
            var layout = Resolve(
                new() { ["toggle_recording"] = "Hidden" },
                activeToggles: "toggle_recording");

            Assert.DoesNotContain("toggle_recording", Ids(layout.Pinned));
            Assert.DoesNotContain("toggle_recording", Ids(layout.Overflow));
        }

        [Fact]
        public void ActiveToggle_AlreadyPinned_IsNotDuplicated()
        {
            var layout = Resolve(
                new() { ["toggle_recording"] = "Pinned" },
                activeToggles: "toggle_recording");

            Assert.Single(layout.Pinned, e => e.Id == "toggle_recording");
        }

        [Fact]
        public void ShowOverflowButton_IsFalse_WhenNothingIsInOverflow()
        {
            var states = TitleBarCatalog.GetEntries()
                .ToDictionary(e => e.Id, _ => "Hidden");

            var layout = Resolve(states);

            Assert.False(layout.ShowOverflowButton);
            Assert.Empty(layout.Overflow);
        }

        [Fact]
        public void ShowOverflowButton_IsFalse_WhenTheOnlyOverflowEntryIsAutoSurfaced()
        {
            var states = TitleBarCatalog.GetEntries()
                .ToDictionary(e => e.Id, e => e.Id == "toggle_recording" ? "Overflow" : "Hidden");

            var layout = Resolve(states, activeToggles: "toggle_recording");

            Assert.False(layout.ShowOverflowButton);
            Assert.Contains("toggle_recording", Ids(layout.Pinned));
        }

        [Fact]
        public void Resolve_DoesNotClampPinnedCount()
        {
            var states = TitleBarCatalog.GetEntries().ToDictionary(e => e.Id, _ => "Pinned");

            var layout = Resolve(states);

            Assert.Equal(TitleBarCatalog.GetEntries().Count, layout.Pinned.Count);
            Assert.True(layout.Pinned.Count > TitleBarCatalog.MaxPinned);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TitleBarLayoutResolverTests"
```

Expected: compile failure — `TitleBarLayoutResolver` does not exist.

- [ ] **Step 3: Write the layout record**

`src/NovaTerminal.App/Shell/TitleBar/TitleBarLayout.cs`:

```csharp
using System.Collections.Generic;

namespace NovaTerminal.Shell.TitleBar;

/// <summary>What the title bar should show right now.</summary>
public sealed record TitleBarLayout(
    IReadOnlyList<TitleBarCatalogEntry> Pinned,
    IReadOnlyList<TitleBarCatalogEntry> Overflow)
{
    /// <summary>The … button is worth rendering only when it would have contents.</summary>
    public bool ShowOverflowButton => Overflow.Count > 0;
}
```

- [ ] **Step 4: Write the resolver**

`src/NovaTerminal.App/Shell/TitleBar/TitleBarLayoutResolver.cs`:

```csharp
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

        // Rule 1: saved state when present and parseable, otherwise the catalog default.
        // Rule 2 (partial): a locked entry is pinned whatever settings say.
        var resolved = entries.ToDictionary(
            e => e.Id,
            e => e.IsLocked ? TitleBarItemState.Pinned : ReadState(states, e),
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
        if (activeToggleIds is { Count: > 0 })
        {
            var surfacing = overflow.Where(e => e.IsToggle && activeToggleIds.Contains(e.Id)).ToList();
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
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TitleBarLayoutResolverTests"
```

Expected: 19 passed.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Shell/TitleBar tests/NovaTerminal.App.Tests/TitleBarLayoutResolverTests.cs
git commit -m "feat(ui): resolve title bar layout from catalog, settings, and toggle state"
```

---

## Task 3: Settings persistence

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalSettings.cs` (add two properties beside `Keybindings`, around line 63)
- Modify: `src/NovaTerminal.App/Shell/AppJsonContext.cs:20` (add one attribute)
- Test: `tests/NovaTerminal.App.Tests/TitleBarSettingsRoundTripTests.cs`

**Interfaces:**
- Consumes: `TitleBarLayoutResolver.Resolve` from Task 2.
- Produces: `TerminalSettings.TitleBarItems` (`Dictionary<string,string>`) and `TerminalSettings.TitleBarOrder` (`List<string>`).

- [ ] **Step 1: Write the failing test**

`tests/NovaTerminal.App.Tests/TitleBarSettingsRoundTripTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NovaTerminal.Shell;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests
{
    public class TitleBarSettingsRoundTripTests
    {
        [Fact]
        public void NewSettings_HaveEmptyTitleBarConfig()
        {
            var settings = new TerminalSettings();

            Assert.NotNull(settings.TitleBarItems);
            Assert.Empty(settings.TitleBarItems);
            Assert.NotNull(settings.TitleBarOrder);
            Assert.Empty(settings.TitleBarOrder);
        }

        [Fact]
        public void TitleBarConfig_SurvivesAJsonRoundTrip()
        {
            var settings = new TerminalSettings
            {
                TitleBarItems = new Dictionary<string, string>
                {
                    ["find"] = "Pinned",
                    ["toggle_recording"] = "Hidden",
                },
                TitleBarOrder = ["settings", "find"],
            };

            string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.TerminalSettings);
            var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings);

            Assert.NotNull(restored);
            Assert.Equal("Pinned", restored!.TitleBarItems["find"]);
            Assert.Equal("Hidden", restored.TitleBarItems["toggle_recording"]);
            Assert.Equal(new[] { "settings", "find" }, restored.TitleBarOrder);
        }

        [Fact]
        public void SettingsJsonWithNoTitleBarKeys_DeserializesToCatalogDefaults()
        {
            // A settings file written before this feature shipped.
            var restored = JsonSerializer.Deserialize(
                """{"FontSize":14}""",
                AppJsonContext.Default.TerminalSettings);

            Assert.NotNull(restored);
            var layout = TitleBarLayoutResolver.Resolve(
                restored!.TitleBarItems, restored.TitleBarOrder, null);

            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "connections", "settings" },
                layout.Pinned.Select(e => e.Id));
        }

        [Fact]
        public void RoundTrippedConfig_ResolvesToTheSameLayout()
        {
            var settings = new TerminalSettings
            {
                TitleBarItems = new Dictionary<string, string> { ["find"] = "Pinned" },
                TitleBarOrder = ["find", "settings"],
            };

            string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.TerminalSettings);
            var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings)!;

            var before = TitleBarLayoutResolver.Resolve(settings.TitleBarItems, settings.TitleBarOrder, null);
            var after = TitleBarLayoutResolver.Resolve(restored.TitleBarItems, restored.TitleBarOrder, null);

            Assert.Equal(before.Pinned.Select(e => e.Id), after.Pinned.Select(e => e.Id));
            Assert.Equal(before.Overflow.Select(e => e.Id), after.Overflow.Select(e => e.Id));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TitleBarSettingsRoundTripTests"
```

Expected: compile failure — `TitleBarItems` is not a member of `TerminalSettings`.

- [ ] **Step 3: Add the settings properties**

In `src/NovaTerminal.App/Shell/TerminalSettings.cs`, immediately after the `Keybindings` property (line 63):

```csharp
        // Title bar customization. Deltas only: an id absent here takes its TitleBarCatalog default,
        // so a catalog entry added in a later version appears for existing users without a migration.
        // Values are TitleBarItemState names ("Pinned" / "Overflow" / "Hidden"); anything unparseable
        // falls back to the entry's default rather than throwing.
        public System.Collections.Generic.Dictionary<string, string> TitleBarItems { get; set; } = new();

        // Display order for the pinned set. Ids it does not name follow in catalog order.
        public System.Collections.Generic.List<string> TitleBarOrder { get; set; } = new();
```

- [ ] **Step 4: Register `List<string>` with the JSON context**

In `src/NovaTerminal.App/Shell/AppJsonContext.cs`, after the `Dictionary<string, string>` line (line 20):

```csharp
    [JsonSerializable(typeof(List<string>))]
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TitleBarSettingsRoundTripTests"
```

Expected: 4 passed.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Shell/TerminalSettings.cs src/NovaTerminal.App/Shell/AppJsonContext.cs tests/NovaTerminal.App.Tests/TitleBarSettingsRoundTripTests.cs
git commit -m "feat(ui): persist title bar item placement and order"
```

---

## Task 4: View factory

**Files:**
- Create: `src/NovaTerminal.App/Shell/TitleBar/TitleBarShortcuts.cs`
- Create: `src/NovaTerminal.App/Shell/TitleBar/TitleBarViewFactory.cs`
- Test: `tests/NovaTerminal.App.Tests/TitleBarViewFactoryTests.cs`

**Interfaces:**
- Consumes: `TitleBarLayout`, `TitleBarCatalogEntry`, `TitleBarCatalog.OverflowGeometry`, `TitleBarCatalog.NewTabId`.
- Produces:
  - `static string TitleBarShortcuts.Resolve(string shortcutKey, IReadOnlyDictionary<string,string>? keybindings)`
  - `static string TitleBarShortcuts.FormatTooltip(string title, string shortcut)`
  - `static void TitleBarViewFactory.Populate(Panel host, TitleBarLayout layout, IReadOnlyDictionary<string,string>? keybindings, IReadOnlyDictionary<string,Action> handlers, Control? newTabButton, Action<string> logMissingHandler)`
  - `const string TitleBarViewFactory.OverflowButtonName = "BtnTitleBarOverflow"`
  - `static string TitleBarViewFactory.ButtonName(string id)` → `$"BtnTitleBar_{id}"`

`Populate` clears `host.Children` and rebuilds it. `newTabButton` is the XAML-declared `+` button, reinserted rather than rebuilt so its `MenuFlyout` survives; pass `null` and the factory builds a plain button instead. A pinned id with no entry in `handlers` is reported through `logMissingHandler` and skipped, never thrown.

- [ ] **Step 1: Write the failing test**

`tests/NovaTerminal.App.Tests/TitleBarViewFactoryTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests
{
    public class TitleBarViewFactoryTests
    {
        private static IReadOnlyDictionary<string, Action> AllHandlers(List<string>? invoked = null)
            => TitleBarCatalog.GetEntries().ToDictionary(
                e => e.Id,
                e => new Action(() => invoked?.Add(e.Id)));

        [Fact]
        public void Resolve_UsesTheShortcutCatalogDefault_WhenNoOverride()
        {
            Assert.Equal("Ctrl+,", TitleBarShortcuts.Resolve("settings", null));
        }

        [Fact]
        public void Resolve_PrefersTheUserOverride()
        {
            var keybindings = new Dictionary<string, string> { ["settings"] = "Ctrl+Alt+S" };

            Assert.Equal("Ctrl+Alt+S", TitleBarShortcuts.Resolve("settings", keybindings));
        }

        [Fact]
        public void Resolve_IgnoresAWhitespaceOverride()
        {
            var keybindings = new Dictionary<string, string> { ["settings"] = "   " };

            Assert.Equal("Ctrl+,", TitleBarShortcuts.Resolve("settings", keybindings));
        }

        [Fact]
        public void Resolve_ReturnsEmpty_ForAnActionWithNoShortcutKey()
        {
            Assert.Equal(string.Empty, TitleBarShortcuts.Resolve("", null));
        }

        [Fact]
        public void Resolve_ReturnsEmpty_ForAnUnknownShortcutKey()
        {
            Assert.Equal(string.Empty, TitleBarShortcuts.Resolve("no_such_command", null));
        }

        [Fact]
        public void FormatTooltip_AppendsTheShortcutInParentheses()
        {
            Assert.Equal("Settings (Ctrl+,)", TitleBarShortcuts.FormatTooltip("Settings", "Ctrl+,"));
        }

        [Fact]
        public void FormatTooltip_OmitsTheParenthesesWhenUnbound()
        {
            Assert.Equal("Transfers", TitleBarShortcuts.FormatTooltip("Transfers", ""));
        }

        [AvaloniaFact]
        public void Populate_AddsOneButtonPerPinnedEntry_PlusTheOverflowButton()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.Equal(layout.Pinned.Count + 1, host.Children.Count);
            Assert.Equal(
                TitleBarViewFactory.OverflowButtonName,
                (host.Children[^1] as Button)?.Name);
        }

        [AvaloniaFact]
        public void Populate_NamesEachButtonAfterItsCatalogId()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.Equal(
                layout.Pinned.Select(e => TitleBarViewFactory.ButtonName(e.Id)),
                host.Children.Take(layout.Pinned.Count).Select(c => (c as Button)?.Name));
        }

        [AvaloniaFact]
        public void Populate_OmitsTheOverflowButton_WhenNothingIsInOverflow()
        {
            var host = new StackPanel();
            var states = TitleBarCatalog.GetEntries()
                .ToDictionary(e => e.Id, e => e.IsLocked ? "Pinned" : "Hidden");
            var layout = TitleBarLayoutResolver.Resolve(states, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.Single(host.Children);
            Assert.DoesNotContain(
                TitleBarViewFactory.OverflowButtonName,
                host.Children.Select(c => (c as Button)?.Name));
        }

        [AvaloniaFact]
        public void Populate_IsIdempotent_AcrossRepeatedCalls()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });
            int first = host.Children.Count;
            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.Equal(first, host.Children.Count);
        }

        [AvaloniaFact]
        public void Populate_ReusesTheSuppliedNewTabButton_SoItsFlyoutSurvives()
        {
            var host = new StackPanel();
            var newTab = new Button { Name = "BtnNewTab", Content = "+" };
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), newTab, _ => { });

            Assert.Same(newTab, host.Children[0]);
        }

        [AvaloniaFact]
        public void Populate_ClickingAButton_InvokesItsHandler()
        {
            var host = new StackPanel();
            var invoked = new List<string>();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(invoked), null, _ => { });

            var settingsButton = host.Children
                .OfType<Button>()
                .Single(b => b.Name == TitleBarViewFactory.ButtonName("settings"));
            settingsButton.Command?.Execute(null);

            Assert.Equal(new[] { "settings" }, invoked);
        }

        [AvaloniaFact]
        public void Populate_ReportsAndSkips_APinnedEntryWithNoHandler()
        {
            var host = new StackPanel();
            var handlers = AllHandlers().Where(kv => kv.Key != "settings")
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var missing = new List<string>();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, handlers, null, missing.Add);

            Assert.Equal(new[] { "settings" }, missing);
            Assert.DoesNotContain(
                TitleBarViewFactory.ButtonName("settings"),
                host.Children.Select(c => (c as Button)?.Name));
        }

        [AvaloniaFact]
        public void Populate_PutsEveryOverflowEntryInTheFlyout()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            var overflowButton = host.Children
                .OfType<Button>()
                .Single(b => b.Name == TitleBarViewFactory.OverflowButtonName);
            var flyout = Assert.IsType<MenuFlyout>(overflowButton.Flyout);

            Assert.Equal(layout.Overflow.Count, flyout.Items.Count);
        }

        [AvaloniaFact]
        public void Populate_TooltipsCarryTitleAndShortcut()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            var settingsButton = host.Children
                .OfType<Button>()
                .Single(b => b.Name == TitleBarViewFactory.ButtonName("settings"));

            Assert.Equal("Settings (Ctrl+,)", ToolTip.GetTip(settingsButton));
        }

        [AvaloniaFact]
        public void Populate_ButtonsAreNotFocusable_SoClicksNeverStealTerminalFocus()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.All(host.Children.OfType<Button>(), b => Assert.False(b.Focusable));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TitleBarViewFactoryTests"
```

Expected: compile failure — `TitleBarShortcuts` and `TitleBarViewFactory` do not exist.

- [ ] **Step 3: Write the shortcut helper**

`src/NovaTerminal.App/Shell/TitleBar/TitleBarShortcuts.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Shell.TitleBar;

/// <summary>
/// Shortcut labels for title bar tooltips and settings rows. Defaults come from
/// <see cref="ShortcutCatalog"/> rather than being restated in the title bar catalog, so the two
/// cannot drift apart.
/// </summary>
public static class TitleBarShortcuts
{
    public static string Resolve(string shortcutKey, IReadOnlyDictionary<string, string>? keybindings)
    {
        if (string.IsNullOrWhiteSpace(shortcutKey))
        {
            return string.Empty;
        }

        if (keybindings is not null &&
            keybindings.TryGetValue(shortcutKey, out string? custom) &&
            !string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }

        return ShortcutCatalog.GetEntries()
            .FirstOrDefault(e => e.CommandId == shortcutKey)?.DefaultBinding
            ?? string.Empty;
    }

    public static string FormatTooltip(string title, string shortcut)
        => string.IsNullOrWhiteSpace(shortcut) ? title : $"{title} ({shortcut})";
}
```

- [ ] **Step 4: Write the view factory**

`src/NovaTerminal.App/Shell/TitleBar/TitleBarViewFactory.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NovaTerminal.Shell.TitleBar;

/// <summary>
/// Builds the title bar's buttons from a resolved layout. Separate from MainWindow on purpose:
/// MainWindow cannot be instantiated in a headless test — it spawns PTYs, SSH, and the agent host —
/// so putting the control construction here is what makes the rendering testable.
/// </summary>
public static class TitleBarViewFactory
{
    public const string OverflowButtonName = "BtnTitleBarOverflow";

    public static string ButtonName(string id) => $"BtnTitleBar_{id}";

    public static void Populate(
        Panel host,
        TitleBarLayout layout,
        IReadOnlyDictionary<string, string>? keybindings,
        IReadOnlyDictionary<string, Action> handlers,
        Control? newTabButton,
        Action<string> logMissingHandler)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(logMissingHandler);

        host.Children.Clear();

        foreach (var entry in layout.Pinned)
        {
            // The + button is declared in XAML and carries a MenuFlyout with real content
            // ("New SSH Connection…", "Manage Profiles…", "Agent Activity…"). Reinsert it rather
            // than rebuild it, or that flyout is lost on every rebuild.
            if (entry.Id == TitleBarCatalog.NewTabId && newTabButton is not null)
            {
                host.Children.Add(newTabButton);
                continue;
            }

            if (!handlers.TryGetValue(entry.Id, out var handler))
            {
                logMissingHandler(entry.Id);
                continue;
            }

            host.Children.Add(CreateItemButton(entry, keybindings, handler));
        }

        if (!layout.ShowOverflowButton)
        {
            return;
        }

        host.Children.Add(CreateOverflowButton(layout, keybindings, handlers, logMissingHandler));
    }

    private static Button CreateItemButton(
        TitleBarCatalogEntry entry,
        IReadOnlyDictionary<string, string>? keybindings,
        Action handler)
    {
        string shortcut = TitleBarShortcuts.Resolve(entry.ShortcutKey, keybindings);

        var button = new Button
        {
            Name = ButtonName(entry.Id),
            // Matches the styling the four hardcoded buttons carried inline before this feature.
            // Focusable=false is load-bearing: a focusable title bar button steals keyboard focus
            // from the terminal on click.
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 32,
            Height = 32,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(4, 0, 0, 0),
            Command = new RelayCommand(handler),
            Content = new PathIcon
            {
                Data = Geometry.Parse(entry.IconGeometry),
                Width = entry.IconSize,
                Height = entry.IconSize,
            },
        };

        ToolTip.SetTip(button, TitleBarShortcuts.FormatTooltip(entry.Title, shortcut));
        return button;
    }

    private static Button CreateOverflowButton(
        TitleBarLayout layout,
        IReadOnlyDictionary<string, string>? keybindings,
        IReadOnlyDictionary<string, Action> handlers,
        Action<string> logMissingHandler)
    {
        var flyout = new MenuFlyout();

        foreach (var entry in layout.Overflow)
        {
            if (!handlers.TryGetValue(entry.Id, out var handler))
            {
                logMissingHandler(entry.Id);
                continue;
            }

            string shortcut = TitleBarShortcuts.Resolve(entry.ShortcutKey, keybindings);

            // The shortcut goes in the header text, not into InputGesture: these bindings are
            // dispatched from MainWindow's own key handler rather than Avalonia's gesture system,
            // so an InputGesture here would register a second, competing route to the same action.
            flyout.Items.Add(new MenuItem
            {
                Header = TitleBarShortcuts.FormatTooltip(entry.Title, shortcut),
                Command = new RelayCommand(handler),
                Icon = new PathIcon
                {
                    Data = Geometry.Parse(entry.IconGeometry),
                    Width = entry.IconSize,
                    Height = entry.IconSize,
                },
            });
        }

        var button = new Button
        {
            Name = OverflowButtonName,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 32,
            Height = 32,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(4, 0, 0, 0),
            Flyout = flyout,
            Content = new PathIcon
            {
                Data = Geometry.Parse(TitleBarCatalog.OverflowGeometry),
                Width = 16,
                Height = 16,
            },
        };

        ToolTip.SetTip(button, "More actions");
        return button;
    }

    /// <summary>Minimal ICommand so a plain Action can drive a Button without a Click subscription.</summary>
    private sealed class RelayCommand(Action execute) : ICommand
    {
        // These commands are always executable, so the event never fires. Empty accessors satisfy
        // ICommand without leaving an unraised field behind.
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TitleBarViewFactoryTests"
```

Expected: 17 passed. If `Geometry.Parse` throws on any path, the geometry constant is malformed — fix the constant, do not loosen the test.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Shell/TitleBar tests/NovaTerminal.App.Tests/TitleBarViewFactoryTests.cs
git commit -m "feat(ui): build title bar buttons from a resolved layout"
```

---

## Task 5: MainWindow integration

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml:114-180` (the title bar overlay `Grid`)
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` — handler map + `RebuildTitleBar`, wired near the existing record-button wiring (~line 2151) and into `OpenSettings`'s `if (saved)` block (~line 5254)

**Interfaces:**
- Consumes: `TitleBarViewFactory.Populate`, `TitleBarLayoutResolver.Resolve`, `TitleBarCatalog`.
- Produces: `private void RebuildTitleBar()`; `private IReadOnlyDictionary<string, Action> BuildTitleBarHandlers()`; `private readonly HashSet<string> _activeTitleBarToggles`.

- [ ] **Step 1: Replace the title bar markup**

In `src/NovaTerminal.App/MainWindow.axaml`, replace the whole overlay `Grid` (from `<Grid VerticalAlignment="Top" Height="32" ... x:Name="TitleBar"` through its closing `</Grid>`, currently lines 114–180) with:

```xml
        <!-- Overlay layer for custom buttons (right aligned, leaving space for the system caption
             buttons). Contents are built by MainWindow.RebuildTitleBar from TitleBarCatalog +
             the user's saved placement, so nothing but the + button is declared here. -->
        <Grid VerticalAlignment="Top" Height="32" HorizontalAlignment="Right" ZIndex="100" x:Name="TitleBar" Margin="0,4,140,0" Background="Transparent">
            <Grid.ContextMenu>
                <ContextMenu>
                    <MenuItem Header="Customize Title Bar..." Name="MenuCustomizeTitleBar"/>
                </ContextMenu>
            </Grid.ContextMenu>
            <StackPanel Name="TitleBarItemsHost" Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center" Background="Transparent">
                <!-- The + button is declared rather than generated: it is locked into the bar and
                     carries this flyout's real content. TitleBarViewFactory reinserts it on every
                     rebuild instead of rebuilding it. Focusable=False keeps clicks from stealing
                     keyboard focus from the terminal. -->
                <Button Name="BtnNewTab" Content="+"
                        Foreground="White"
                        Background="Transparent"
                        BorderThickness="0" Focusable="False"
                        FontSize="18"
                        FontWeight="Light"
                        Padding="8,4"
                        CornerRadius="4"
                        Margin="4,0,0,0"
                        ToolTip.Tip="New Tab">
                    <Button.Flyout>
                        <MenuFlyout>
                            <Separator />
                            <MenuItem Header="New SSH Connection..." Name="MenuNewSshConnection" />
                            <MenuItem Header="Manage Profiles..." Name="MenuManageProfiles" />
                            <MenuItem Header="Agent Activity..." Name="MenuAgentActivity" />
                        </MenuFlyout>
                    </Button.Flyout>
                </Button>
            </StackPanel>
            <TextBlock Name="TabOverflowBadge"
                       IsVisible="False"
                       Foreground="#FFD25A"
                       FontSize="11"
                       VerticalAlignment="Center"
                       HorizontalAlignment="Left"
                       Margin="2,0,0,0"/>
        </Grid>
```

`TabOverflowBadge` moves out of the item host so a rebuild cannot clear it — it is driven separately by the tab code.

- [ ] **Step 2: Add the using and the toggle set**

At the top of `src/NovaTerminal.App/MainWindow.axaml.cs`, with the other `using` lines:

```csharp
using NovaTerminal.Shell.TitleBar;
```

With the other private fields (near `_globalHotkey`, around line 41):

```csharp
        // Ids of stateful title bar toggles that are currently ON. An overflowed toggle in this set
        // is auto-surfaced into the bar by TitleBarLayoutResolver, which is how Record stays visible
        // while recording without being permanently pinned.
        private readonly HashSet<string> _activeTitleBarToggles = new(StringComparer.OrdinalIgnoreCase);
```

- [ ] **Step 3: Add the handler map and the rebuild method**

Add both methods next to `SyncRecordingButtonState` (around line 6055):

```csharp
        /// <summary>
        /// Catalog id to action. Deliberately not sourced from CommandRegistry: SetupCommandPalette()
        /// is lazy — it runs on palette-open and settings-save, never at startup (see the comment
        /// near line 2207) — so a title bar reading the registry would come up dead on a cold start.
        /// </summary>
        private IReadOnlyDictionary<string, Action> BuildTitleBarHandlers()
        {
            return new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
            {
                ["new_tab"] = () => AddTab(),
                ["open_tab_list"] = () => PopulateTabListMenu(showFlyout: true),
                ["connections"] = () => ToggleConnections(),
                ["settings"] = () => _ = OpenSettings(0),
                ["toggle_recording"] = () => _currentPane?.ToggleRecording(),
                ["command_palette"] = () => ToggleCommandPalette(),
                ["find"] = () => _currentPane?.ToggleSearch(),
                ["split_vertical"] = () => SplitPane(Avalonia.Layout.Orientation.Horizontal),
                ["split_horizontal"] = () => SplitPane(Avalonia.Layout.Orientation.Vertical),
                ["sftp_remote_files"] = () => _currentPane?.ToggleRemoteFilesSidebar(),
                ["sftp_transfers"] = () => ToggleTransferCenter(),
                ["agent_activity"] = () => _ = ShowAgentActivityJournalAsync(),
            };
        }

        private void RebuildTitleBar()
        {
            var host = this.FindControl<StackPanel>("TitleBarItemsHost");
            if (host == null)
            {
                return;
            }

            var layout = TitleBarLayoutResolver.Resolve(
                _settings.TitleBarItems,
                _settings.TitleBarOrder,
                _activeTitleBarToggles);

            TitleBarViewFactory.Populate(
                host,
                layout,
                _settings.Keybindings,
                BuildTitleBarHandlers(),
                this.FindControl<Button>("BtnNewTab"),
                id => AppLogger.Log($"[TitleBar] no handler wired for catalog id '{id}'; skipping"));

            // The record button is recreated by every rebuild, so its active colouring has to be
            // reapplied against the new instance.
            SyncRecordingButtonState();
        }
```

Every name in that map was verified against the current source while this plan was written:
`AddTab` (line 3984), `PopulateTabListMenu` (754), `ToggleConnections` (156), `OpenSettings` (5183),
`ToggleCommandPalette` (4968), `SplitPane` (4170), `ToggleTransferCenter` (5063),
`ShowAgentActivityJournalAsync` (5388), and on `TerminalPane`: `ToggleRecording` (378),
`ToggleSearch` (3370), `ToggleRemoteFilesSidebar` (467). If a name has since moved, run:

```bash
grep -nE "(private|internal|public|void).*(AddTab|PopulateTabListMenu|ToggleConnections|ToggleCommandPalette|SplitPane|ToggleTransferCenter)\(" src/NovaTerminal.App/MainWindow.axaml.cs
```

- [ ] **Step 4: Retire the per-button wiring and call the rebuild**

Delete the old `BtnRecord` click wiring at line ~2151 (`btnRecord.Click += (s, e) => _currentPane?.ToggleRecording();`) along with the `FindControl<Button>("BtnRecord")` that feeds it, and the equivalent wiring for `BtnTabList` and `BtnConnections` — the handler map replaces all three. Leave the `BtnNewTab`, `MenuNewSshConnection`, `MenuManageProfiles`, and `MenuAgentActivity` wiring alone.

Then, at the very end of the constructor — after `ApplyThemeToUI()` and the rest of the UI wireup — add:

```csharp
            // Built here rather than from SetupCommandPalette(), which is lazy and does not run at
            // startup: the initial window's title bar has to exist before the user opens anything.
            RebuildTitleBar();
```

- [ ] **Step 5: Rebuild after a settings save**

In `OpenSettings`, inside the `if (saved)` block, immediately after `ApplySettingsToAllTabs();` (around line 5273):

```csharp
                RebuildTitleBar();
```

- [ ] **Step 6: Build and run the existing suite for regressions**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Expected: 0 errors. Then:

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TitleBar"
```

Expected: all TitleBar tests still pass (47 total across the four classes).

- [ ] **Step 7: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml src/NovaTerminal.App/MainWindow.axaml.cs
git commit -m "feat(ui): render the title bar from the resolved layout"
```

---

## Task 6: Auto-surface the recording toggle

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` — `OnRecordingStateChanged` and `UpdateRecordButtonUi` (around lines 5854 and 6060)

**Interfaces:**
- Consumes: `_activeTitleBarToggles` and `RebuildTitleBar()` from Task 5.
- Produces: no new public surface. `UpdateRecordButtonUi` looks the button up by its generated name instead of the retired static `BtnRecord`.

- [ ] **Step 1: Track the toggle and rebuild when it flips**

`OnRecordingStateChanged` is at `MainWindow.axaml.cs:5960` and its entire current body is:

```csharp
        private void OnRecordingStateChanged(bool isRecording)
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateRecordButtonUi(isRecording);
            });
        }
```

Replace it with exactly this. The `Dispatcher.UIThread.Post` wrapper is load-bearing — this
callback arrives from the recording session off the UI thread, and both `RebuildTitleBar` and
`UpdateRecordButtonUi` touch Avalonia controls. The rebuild is gated on an actual state change
because an unconditional rebuild would discard and recreate the whole bar on every notification:

```csharp
        private void OnRecordingStateChanged(bool isRecording)
        {
            Dispatcher.UIThread.Post(() =>
            {
                bool changed = isRecording
                    ? _activeTitleBarToggles.Add("toggle_recording")
                    : _activeTitleBarToggles.Remove("toggle_recording");

                if (changed)
                {
                    // Surfaces an overflowed Record button into the bar while recording and drops
                    // it back into the … flyout when it stops. RebuildTitleBar re-syncs the button
                    // colouring itself, so no separate UpdateRecordButtonUi call belongs here.
                    RebuildTitleBar();
                }
                else
                {
                    UpdateRecordButtonUi(isRecording);
                }
            });
        }
```

Leave `OnRecordingNotification` — the toast handling immediately below — untouched.

- [ ] **Step 2: Point the record-button colouring at the generated button**

`UpdateRecordButtonUi` currently looks up `BtnRecord` and `IconRecord`, neither of which exists any more. Replace those two lookups:

```csharp
        private void UpdateRecordButtonUi(bool isRecording)
        {
            var btnRecord = this.FindControl<Button>(TitleBarViewFactory.ButtonName("toggle_recording"));
            var iconRecord = btnRecord?.Content as PathIcon;

            // Absent whenever Record is hidden, or overflowed and not currently active — both
            // legitimate configurations, so this is a quiet no-op rather than a failure.
            if (btnRecord == null || iconRecord == null)
            {
                return;
            }
```

Leave the rest of the method — the brushes and the three assignments — exactly as it is.

- [ ] **Step 3: Check for other references to the retired names**

```bash
grep -n "BtnRecord\|IconRecord\|BtnTabList\|IconTabList\|BtnConnections" src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/MainWindow.axaml
```

Expected: no hits. Fix any that remain — a `FindControl` on a retired name returns null silently, so the compiler will not catch it.

- [ ] **Step 4: Build**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs
git commit -m "feat(ui): surface an overflowed Record button while recording"
```

---

## Task 7: Settings UI

**Files:**
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml` — a `TITLE BAR` section in the Appearance tab (which starts at line 359)
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml.cs` — populate the rows, persist on save (the save handler ends around line 2315)

**Interfaces:**
- Consumes: `TitleBarCatalog`, `TitleBarItemState`, `TitleBarShortcuts`, `TitleBarCatalog.MaxPinned`.
- Produces: writes `_settings.TitleBarItems` and `_settings.TitleBarOrder`. Draft state lives in `_titleBarDraftStates` (`Dictionary<string, TitleBarItemState>`) and `_titleBarDraftOrder` (`List<string>`), mirroring how `_shortcutDraftBindings` already works.

- [ ] **Step 1: Add the section markup**

In `src/NovaTerminal.App/SettingsWindow.axaml`, inside the Appearance tab's outer `StackPanel`, after the existing `THEME` section:

```xml
                            <TextBlock Classes="SectionHeader" Text="TITLE BAR" Margin="0,24,0,14"/>

                            <StackPanel Spacing="10" Margin="0,0,0,16">
                                <TextBlock Classes="RowDesc"
                                           TextWrapping="Wrap"
                                           Text="Pinned actions get their own icon in the title bar. Overflow actions live in the … menu. Hidden actions stay reachable by shortcut and from the command palette."/>
                                <TextBlock Name="TitleBarValidationMessage"
                                           Foreground="{StaticResource NtRed}"
                                           FontSize="12"
                                           IsVisible="False"
                                           TextWrapping="Wrap"/>
                            </StackPanel>

                            <StackPanel Name="TitleBarItemsPanel" Spacing="8"/>
```

- [ ] **Step 2: Add the draft state fields**

In `src/NovaTerminal.App/SettingsWindow.axaml.cs`, beside `_shortcutDraftBindings`:

```csharp
        private readonly Dictionary<string, TitleBarItemState> _titleBarDraftStates =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _titleBarDraftOrder = new();
```

And the using:

```csharp
using NovaTerminal.Shell.TitleBar;
```

- [ ] **Step 3: Seed the draft from settings and build the rows**

Add these methods, and call `LoadTitleBarDraft(); RebuildTitleBarRows();` from the constructor where the other tabs are populated (next to the call that fills `ShortcutBindingsPanel`):

```csharp
        private void LoadTitleBarDraft()
        {
            _titleBarDraftStates.Clear();
            _titleBarDraftOrder.Clear();

            foreach (var entry in TitleBarCatalog.GetEntries())
            {
                // Seed every entry explicitly so the row UI always has a concrete state to show.
                // Only the ids that differ from their default are written back on save.
                _titleBarDraftStates[entry.Id] =
                    entry.IsLocked
                        ? TitleBarItemState.Pinned
                        : ReadDraftState(entry);
            }

            // Resolve once to get the effective pinned order, including catalog-order fallback for
            // ids the saved order does not name.
            var layout = TitleBarLayoutResolver.Resolve(
                _settings.TitleBarItems, _settings.TitleBarOrder, null);
            _titleBarDraftOrder.AddRange(layout.Pinned.Select(e => e.Id));
        }

        private TitleBarItemState ReadDraftState(TitleBarCatalogEntry entry)
        {
            if (_settings.TitleBarItems is not null &&
                _settings.TitleBarItems.TryGetValue(entry.Id, out string? raw) &&
                Enum.TryParse(raw, ignoreCase: true, out TitleBarItemState parsed) &&
                Enum.IsDefined(parsed))
            {
                return parsed;
            }

            return entry.DefaultState;
        }

        private void RebuildTitleBarRows()
        {
            var panel = this.FindControl<StackPanel>("TitleBarItemsPanel");
            if (panel == null)
            {
                return;
            }

            panel.Children.Clear();

            // Pinned entries first, in their configured order, so the ▲/▼ buttons act on a list
            // that reads top-to-bottom the way the bar reads left-to-right. Then the rest in
            // catalog order.
            var ordered = _titleBarDraftOrder
                .Where(id => _titleBarDraftStates.TryGetValue(id, out var s) && s == TitleBarItemState.Pinned)
                .ToList();
            ordered.AddRange(TitleBarCatalog.GetEntries()
                .Select(e => e.Id)
                .Where(id => !ordered.Contains(id, StringComparer.OrdinalIgnoreCase)));

            var byId = TitleBarCatalog.GetEntries()
                .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < ordered.Count; i++)
            {
                panel.Children.Add(CreateTitleBarRow(byId[ordered[i]], i, ordered));
            }
        }

        private Control CreateTitleBarRow(
            TitleBarCatalogEntry entry,
            int index,
            List<string> ordered)
        {
            var state = _titleBarDraftStates[entry.Id];
            bool isPinned = state == TitleBarItemState.Pinned;

            var row = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#23272f")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2a2f38")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
            };

            var icon = new PathIcon
            {
                Data = Geometry.Parse(entry.IconGeometry),
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
            };

            string shortcut = TitleBarShortcuts.Resolve(entry.ShortcutKey, _shortcutDraftBindings);

            var labels = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = entry.Title },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(shortcut) ? "No shortcut" : shortcut,
                        Classes = { "RowDesc" },
                    },
                },
            };

            Control placement;
            if (entry.IsLocked)
            {
                // Locked: New Tab is the primary action and hosts the flyout with
                // "New SSH Connection…" / "Manage Profiles…" / "Agent Activity…". Letting it be
                // hidden would lose that flyout entirely.
                placement = new TextBlock
                {
                    Text = "Always pinned",
                    Classes = { "RowDesc" },
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                var combo = new ComboBox
                {
                    MinWidth = 140,
                    VerticalAlignment = VerticalAlignment.Center,
                    ItemsSource = new[] { "Pinned", "Overflow", "Hidden" },
                    SelectedItem = state.ToString(),
                };

                combo.SelectionChanged += (s, e) =>
                {
                    if (combo.SelectedItem is not string picked ||
                        !Enum.TryParse(picked, out TitleBarItemState next))
                    {
                        return;
                    }

                    if (next == TitleBarItemState.Pinned && CountDraftPinned() >= TitleBarCatalog.MaxPinned)
                    {
                        // Explicit placement with no width-driven spill means nothing else stops a
                        // pinned set from running into the tab strip.
                        ShowTitleBarValidationMessage(
                            $"At most {TitleBarCatalog.MaxPinned} actions can be pinned. Move one to Overflow or Hidden first.");
                        combo.SelectedItem = _titleBarDraftStates[entry.Id].ToString();
                        return;
                    }

                    ClearTitleBarValidationMessage();
                    _titleBarDraftStates[entry.Id] = next;

                    if (next == TitleBarItemState.Pinned)
                    {
                        if (!_titleBarDraftOrder.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
                        {
                            _titleBarDraftOrder.Add(entry.Id);
                        }
                    }
                    else
                    {
                        _titleBarDraftOrder.RemoveAll(
                            id => string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase));
                    }

                    RebuildTitleBarRows();
                };

                placement = combo;
            }

            var up = new Button
            {
                Content = "▲",
                Classes = { "Pill" },
                IsEnabled = isPinned && !entry.IsLocked && index > 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            up.Click += (s, e) => MoveDraftPinned(entry.Id, -1);

            var down = new Button
            {
                Content = "▼",
                Classes = { "Pill" },
                IsEnabled = isPinned && !entry.IsLocked && index < CountDraftPinned() - 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            down.Click += (s, e) => MoveDraftPinned(entry.Id, +1);

            row.Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
                ColumnSpacing = 12,
                Children = { icon, labels, placement, up, down },
            };

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(labels, 1);
            Grid.SetColumn(placement, 2);
            Grid.SetColumn(up, 3);
            Grid.SetColumn(down, 4);

            return row;
        }

        private int CountDraftPinned()
            => _titleBarDraftStates.Count(kv => kv.Value == TitleBarItemState.Pinned);

        private void MoveDraftPinned(string id, int delta)
        {
            int from = _titleBarDraftOrder.FindIndex(
                x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            int to = from + delta;

            // Index 0 is the locked New Tab entry, which never moves and can never be displaced.
            if (from <= 0 || to <= 0 || to >= _titleBarDraftOrder.Count)
            {
                return;
            }

            (_titleBarDraftOrder[from], _titleBarDraftOrder[to]) =
                (_titleBarDraftOrder[to], _titleBarDraftOrder[from]);

            RebuildTitleBarRows();
        }

        private void ShowTitleBarValidationMessage(string message)
        {
            var label = this.FindControl<TextBlock>("TitleBarValidationMessage");
            if (label == null) return;
            label.Text = message;
            label.IsVisible = true;
        }

        private void ClearTitleBarValidationMessage()
        {
            var label = this.FindControl<TextBlock>("TitleBarValidationMessage");
            if (label == null) return;
            label.IsVisible = false;
        }
```

- [ ] **Step 4: Persist on save**

In the save handler, immediately before `_settings.Save();` (around line 2314):

```csharp
            // Deltas only: an id at its catalog default is omitted, so a future catalog change
            // reaches existing users without a migration.
            _settings.TitleBarItems = TitleBarCatalog.GetEntries()
                .Where(e => !e.IsLocked && _titleBarDraftStates[e.Id] != e.DefaultState)
                .ToDictionary(e => e.Id, e => _titleBarDraftStates[e.Id].ToString(), StringComparer.OrdinalIgnoreCase);

            _settings.TitleBarOrder = _titleBarDraftOrder
                .Where(id => _titleBarDraftStates.TryGetValue(id, out var s) && s == TitleBarItemState.Pinned)
                .ToList();
```

- [ ] **Step 5: Build**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Expected: 0 errors. Add whichever `using` lines the compiler asks for (`Avalonia.Layout`, `Avalonia.Media`, `System.Linq`).

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/SettingsWindow.axaml src/NovaTerminal.App/SettingsWindow.axaml.cs
git commit -m "feat(ui): add the title bar section to Appearance settings"
```

---

## Task 8: Right-click entry point

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` — wire `MenuCustomizeTitleBar` next to the existing `MenuManageProfiles` wiring (~line 2130)

**Interfaces:**
- Consumes: the `MenuCustomizeTitleBar` item added to `MainWindow.axaml` in Task 5; `OpenSettings(int)`.
- Produces: no new surface.

- [ ] **Step 1: Wire the menu item**

Beside the `MenuManageProfiles` wiring:

```csharp
            var menuCustomizeTitleBar = this.FindControl<MenuItem>("MenuCustomizeTitleBar");
            if (menuCustomizeTitleBar != null) menuCustomizeTitleBar.Click += async (s, e) =>
            {
                // Appearance is tab 0, and the TITLE BAR section lives there. This right-click is
                // how the feature is actually discoverable; the settings section alone is not.
                await OpenSettings(0);
            };
```

- [ ] **Step 2: Build**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs
git commit -m "feat(ui): open title bar settings from a title bar right-click"
```

---

## Task 9: Full verification and manual smoke test

**Files:** none — verification only.

- [ ] **Step 1: Run the full App.Tests project**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests
```

Expected: no new failures against the baseline. Note that this project is the one with a known flaky host-hang: if the run reports a host hang *after* every test has passed, re-run rather than debugging it.

- [ ] **Step 2: Run the architecture tests**

```bash
scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests
```

Expected: pass. `ProjectFileLayeringTests` will flag a layering violation if anything under `Shell/TitleBar/` reached for a dependency it should not have.

- [ ] **Step 3: Launch the app and check the icons render**

```bash
scripts/build.ps1 run src/NovaTerminal.App
```

Every geometry constant in Task 1 is a literal path string, and a malformed one renders as an empty 32×32 button rather than throwing — the tests cannot catch that, only your eyes can. Walk through:

1. The title bar shows five buttons: `+`, Tab List, Connections, **gear**, `⋯`. Each icon is a recognizable glyph, not a blank gap.
2. Hover each one: the tooltip names the action and its shortcut — "Settings (Ctrl+,)".
3. Click the gear. Settings opens on Appearance. The `TITLE BAR` section lists all 12 actions.
4. Open `⋯`. It lists Record Session, Command Palette, Find in Terminal, Split Vertical, Split Horizontal, Remote Files, Transfers — each with its icon.
5. Right-click an empty part of the title bar → "Customize Title Bar…" → Settings opens on Appearance.
6. In the `TITLE BAR` section set **Find in Terminal** to Pinned, press ▲ twice, Save. The magnifier appears in the bar in the position you moved it to.
7. Try to pin a 9th action. The combo snaps back and the validation message explains the limit.
8. Confirm New Tab's row reads "Always pinned" with no combo and no enabled arrows.
9. Press `Ctrl+Shift+R` to start recording. The Record icon appears in the bar, coloured `#F1636B`. Press it again — recording stops and the icon returns to the `⋯` flyout.
10. Reopen Settings, set Record to Hidden, Save, then press `Ctrl+Shift+R`. Recording still starts, and no icon surfaces — hidden means hidden.
11. Restart the app. Your configuration survived.

- [ ] **Step 4: Confirm the settings file holds only deltas**

```bash
grep -A5 TitleBar "$APPDATA/NovaTerminal/settings.json"
```

That path works from Git Bash. `$env:APPDATA` is PowerShell syntax and expands to nothing
in a bash fence, which would silently read the wrong file.

Expected: `TitleBarItems` contains only the ids you changed — not all 12 — and `TitleBarOrder` lists just the pinned set.

- [ ] **Step 5: Commit any fixes**

```bash
git add -A
git commit -m "fix(ui): correct title bar icon geometry after visual check"
```

Skip this step if nothing needed fixing.

---

## Self-Review

**Spec coverage:** Catalog → Task 1. Deltas-not-resolved-list persistence and the `List<string>` JSON registration → Task 3. All five resolution rules, including auto-surface and the no-clamp decision → Task 2. Rendering, the handler map, the `CommandRegistry` avoidance, the preserved `+` flyout, and rebuild-on-save → Tasks 4 and 5. Auto-surface wiring for Record and the retired `BtnRecord` lookup → Task 6. Settings UI, the row idiom, ▲/▼, and the 8-item guardrail → Task 7. Right-click entry point → Task 8. Every error-handling row in the spec's table has a test in Task 2 or a code comment at the site that handles it.

**Two spec items intentionally not implemented as written**, both recorded under "Deviations" above with reasoning: the flat `Shell/` file placement (now `Shell/TitleBar/`) and the entry's `ShortcutDefault` field (now looked up from `ShortcutCatalog`).

**Known risk carried into implementation:** the eight new icon geometries are path strings I wrote from memory of the Material Design Icons set. `Geometry.Parse` will throw on a malformed path — caught by Task 4's tests — but a *well-formed and wrong* path renders a plausible-looking wrong glyph, or nothing. Task 9 Step 3 exists specifically to catch that, and it is a manual visual check because nothing else can catch it.
