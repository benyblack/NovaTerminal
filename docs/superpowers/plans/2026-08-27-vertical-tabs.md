# Vertical Tab Sidebar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A toggleable vertical tab sidebar (left, resizable, persisted width) whose rows show session title, a heuristic agent status (Working / Attention / Idle), and a one-line preview of the latest terminal output.

**Architecture:** One `TabControl` ("Tabs"), two `ControlTheme` presentations swapped at runtime by a new `MainWindow.ApplyTabLayout()`. Status comes from a pure `TabStatusTracker` fed by the pane events MainWindow already consumes (`OutputReceived`, `BellReceived`); the preview line comes from a new `TerminalExporter` helper over `TerminalBuffer`. Same `TabItem` instances in both modes, so MRU, broadcast, context menus, and session restore are untouched.

**Tech Stack:** C# / .NET, Avalonia 11 (headless-testable via `Avalonia.Headless.XUnit`), xUnit v3.

**Spec:** `docs/superpowers/specs/2026-08-27-vertical-tabs-design.md` (commit `51e5471`).

## Global Constraints

- **Never run raw `dotnet`.** Always `scripts/build.ps1 <args...>` (PowerShell) or `scripts/build.sh` (bash). Raw `dotnet build` hangs the calling harness (see CLAUDE.md).
- **Prefix git/gh commands with `rtk`** (e.g. `rtk git add ...`).
- Test placement convention (documented in `tests/NovaTerminal.App.Tests/TitleBarUiFreedomTests.cs`): pure logic → plain `[Fact]` in `tests/NovaTerminal.App.Tests/` root; anything touching controls → `[AvaloniaFact]` (from `Avalonia.Headless.XUnit`) in `tests/NovaTerminal.App.Tests/Core/`. Test classes are `public sealed`, namespace `NovaTerminal.Tests.Core` for the Core folder.
- `tests/NovaTerminal.App.Tests` is **non-blocking in CI** (`continue-on-error`) — you must run it locally and it must pass. `Architecture`, `McpServer`, and `VT` test projects ARE gating.
- **No new test projects** (new projects require sln + ci.yml + release.yml edits). Only new test *files* in existing projects — those need no CI changes.
- `NovaTerminal.MainWindow` internals are directly visible to App.Tests (existing tests call `MainWindow.GetTabHeaderViewportMargin`, `CountHiddenTabs` directly). New test seams should be `internal`; use reflection only for existing `private` members.
- Setting values are strings parsed with `Enum.TryParse(..., ignoreCase: true) + Enum.IsDefined`, falling back to the default on anything unrecognized (house pattern; a real enum property would make bad JSON a hard deserialization failure).
- Canonical constants used across tasks: orientation strings `"Horizontal"` (default) / `"Vertical"`; sidebar width default `220`, clamp `140–600`; `WorkingWindow = 2s`; `MinAttentionBurst = 5s`; status decay timer interval `1s`.
- `MainWindow.axaml.cs` is ~7255 lines; line numbers below are from 2026-08-27 and may drift — anchor by symbol name, not line number.

## File Structure

| File | Responsibility |
|---|---|
| `src/NovaTerminal.App/Shell/TerminalSettings.cs` | +2 properties (`TabStripOrientation`, `VerticalTabStripWidth`) |
| `src/NovaTerminal.App/Shell/TabStripLayout.cs` (new) | Pure helpers: orientation parse, width clamp, drag math + `TabStripOrientationKind` enum |
| `src/NovaTerminal.App/Shell/TabStatusTracker.cs` (new) | Pure per-tab status heuristic + `TabTrackerStatus` enum |
| `src/NovaTerminal.VT/Export/TerminalExporter.cs` | +`GetLastNonEmptyRowText(TerminalBuffer)` |
| `src/NovaTerminal.App/MainWindow.axaml` | Tab themes as resources (horizontal = existing template moved; vertical = new), vertical TabItem styles |
| `src/NovaTerminal.App/MainWindow.axaml.cs` | `ApplyTabLayout()`, vertical header host, viewport guards, status wiring, grip wiring, shortcut/palette entries |
| `src/NovaTerminal.App/SettingsWindow.axaml(.cs)` | Orientation dropdown in WINDOW section |
| `src/NovaTerminal.App/Shell/Shortcuts/ShortcutCatalog.cs` | +`toggle_tab_orientation` entry |
| `src/NovaTerminal.McpServer/Tools/SettingsTools.cs` | Schema/example/`StringFields`/`KnownFields`/numeric validation for the 2 new settings |
| `tests/NovaTerminal.Architecture.Tests/TabStripSettingsTests.cs` (new) | Settings default/round-trip/upgrade |
| `tests/NovaTerminal.App.Tests/TabStripLayoutTests.cs` (new) | Pure layout helper tests |
| `tests/NovaTerminal.App.Tests/TabStatusTrackerTests.cs` (new) | Tracker state-machine tests |
| `tests/NovaTerminal.VT.Tests/` (existing exporter test file or new one) | Last-row helper tests |
| `tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs` (new) | Headless window tests for the whole feature |

---

### Task 1: Settings fields

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalSettings.cs` (properties live around lines 14–80)
- Test: `tests/NovaTerminal.Architecture.Tests/TabStripSettingsTests.cs` (new)

**Interfaces:**
- Consumes: nothing.
- Produces: `TerminalSettings.TabStripOrientation` (`string`, default `"Horizontal"`) and `TerminalSettings.VerticalTabStripWidth` (`double`, default `220`). Every later task reads these.

- [ ] **Step 1: Check the namespace/pattern of the canonical settings test**

Read `tests/NovaTerminal.Architecture.Tests/Update/UpdateSettingsTests.cs` (45 lines). Note its namespace and using set — the new test file must match them.

- [ ] **Step 2: Write the failing tests**

Create `tests/NovaTerminal.Architecture.Tests/TabStripSettingsTests.cs` (adjust namespace/usings to what Step 1 found):

```csharp
using System.Text.Json;
using NovaTerminal.Shell;

public sealed class TabStripSettingsTests
{
    [Fact]
    public void Defaults_AreHorizontalWith220Width()
    {
        var settings = new TerminalSettings();
        Assert.Equal("Horizontal", settings.TabStripOrientation);
        Assert.Equal(220, settings.VerticalTabStripWidth);
    }

    [Fact]
    public void RoundTrip_PreservesTabStripSettings()
    {
        var settings = new TerminalSettings { TabStripOrientation = "Vertical", VerticalTabStripWidth = 300 };
        string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.TerminalSettings);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings);
        Assert.NotNull(back);
        Assert.Equal("Vertical", back!.TabStripOrientation);
        Assert.Equal(300, back.VerticalTabStripWidth);
    }

    [Fact]
    public void EmptyJson_UpgradesToDefaults()
    {
        var settings = JsonSerializer.Deserialize("{}", AppJsonContext.Default.TerminalSettings);
        Assert.NotNull(settings);
        Assert.Equal("Horizontal", settings!.TabStripOrientation);
        Assert.Equal(220, settings.VerticalTabStripWidth);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests --filter "FullyQualifiedName~TabStripSettingsTests"
```

Expected: compile FAIL — `TabStripOrientation` not defined.

- [ ] **Step 4: Add the properties**

In `src/NovaTerminal.App/Shell/TerminalSettings.cs`, next to the other window-level settings (near `BlurEffect`), following the house doc-comment style of `ShellExitPolicy`:

```csharp
        /// <summary>
        /// Where the tab strip lives: "Horizontal" (title-bar strip, the default) or
        /// "Vertical" (left sidebar with per-tab agent status and output preview).
        /// Parsed case-insensitively; unrecognized values behave as "Horizontal".
        /// </summary>
        public string TabStripOrientation { get; set; } = "Horizontal";

        /// <summary>
        /// Sidebar width in px when <see cref="TabStripOrientation"/> is "Vertical".
        /// Clamped to 140–600 at apply time.
        /// </summary>
        public double VerticalTabStripWidth { get; set; } = 220;
```

No `AppJsonContext.cs` change is needed (string + double are already handled; only new collection/complex types need registration).

Spec follow-up resolved here: the `TerminalPane.BuildEffectiveSettings` whitelist does NOT need these fields — that whitelist only shapes the copy handed to `TermView`, and these settings are consumed by `MainWindow`, never by panes (same reasoning as `TitleBarItems`, see `docs/superpowers/specs/2026-08-24-title-bar-customization-design.md:119-120`).

- [ ] **Step 5: Run tests to verify they pass**

Same command as Step 3. Expected: 3 PASS.

- [ ] **Step 6: Commit**

```bash
rtk git add src/NovaTerminal.App/Shell/TerminalSettings.cs tests/NovaTerminal.Architecture.Tests/TabStripSettingsTests.cs
rtk git commit -m "feat(settings): TabStripOrientation and VerticalTabStripWidth fields"
```

---

### Task 2: MCP SettingsTools registration

**Files:**
- Modify: `src/NovaTerminal.McpServer/Tools/SettingsTools.cs`
- Test: existing `tests/NovaTerminal.McpServer.Tests/SettingsToolsDriftGuardTests.cs` (no edits — it reflects over `TerminalSettings`)

**Interfaces:**
- Consumes: Task 1's two properties.
- Produces: nothing consumed later; keeps gating drift-guard tests green.

- [ ] **Step 1: Run the drift guard to see it fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~SettingsTools"
```

Expected: FAIL — `KnownFields_AreExactlyTheSerializedSettings` reports both new names missing, `StringFields_AreExactlyTheStringSettings` reports `TabStripOrientation` missing.

- [ ] **Step 2: Register the settings in all four places in SettingsTools.cs**

1. Schema markdown table (inside `GetSettingsSchema()`, lines ~22–147), two new rows next to the other window rows:

```
| `TabStripOrientation` | string (enum-like) | "Horizontal"/"Vertical". Default "Horizontal". Places the tab strip in the title bar (horizontal) or in a left sidebar with per-tab agent status and a last-output preview line (vertical). Type-checked only; unrecognised values behave as "Horizontal". |
| `VerticalTabStripWidth` | number | Sidebar width in px when `TabStripOrientation` is "Vertical". Default 220; applied clamped to 140–600. |
```

2. The `## Example` JSON block in the same string (~line 118) — add, keeping the block valid JSON:

```
          "TabStripOrientation": "Horizontal",
          "VerticalTabStripWidth": 220,
```

3. `StringFields` (~line 156): append `"TabStripOrientation"`.
4. `KnownFields` (~line 176): append `"TabStripOrientation", "VerticalTabStripWidth"`.

Additionally, in `ValidateSettingsJson` (~lines 236–255), next to the existing `CheckNumber(root, "FontSize", ...)` call, add (matching the exact local helper signature used there):

```csharp
        CheckNumber(root, "VerticalTabStripWidth", v => v >= 140 && v <= 600, "must be between 140 and 600", errors);
```

- [ ] **Step 3: Run the tests to verify they pass**

Same command as Step 1. Expected: PASS, including `SchemaExample_PassesItsOwnValidator` (it re-parses the example block you edited).

- [ ] **Step 4: Commit**

```bash
rtk git add src/NovaTerminal.McpServer/Tools/SettingsTools.cs
rtk git commit -m "feat(mcp): register tab strip settings in SettingsTools schema and validators"
```

---

### Task 3: TabStripLayout pure helpers

**Files:**
- Create: `src/NovaTerminal.App/Shell/TabStripLayout.cs`
- Test: `tests/NovaTerminal.App.Tests/TabStripLayoutTests.cs` (new, plain `[Fact]`s, project root — mirror the namespace used by `TabBehaviorTests.cs`' siblings in the root, e.g. `TitleBarLayoutResolverTests.cs`)

**Interfaces:**
- Consumes: nothing.
- Produces (all `internal`, namespace `NovaTerminal.Shell`):
  - `enum TabStripOrientationKind { Horizontal, Vertical }`
  - `static class TabStripLayout` with `const double MinSidebarWidth = 140`, `MaxSidebarWidth = 600`, `DefaultSidebarWidth = 220`; `static bool IsVertical(string? orientation)`; `static double ClampSidebarWidth(double width)`; `static double ComputeDraggedWidth(double startWidth, double startX, double currentX)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using NovaTerminal.Shell;

public sealed class TabStripLayoutTests
{
    [Theory]
    [InlineData("Vertical", true)]
    [InlineData("vertical", true)]
    [InlineData("VERTICAL", true)]
    [InlineData("Horizontal", false)]
    [InlineData("horizontal", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Sideways", false)]
    [InlineData("2", false)] // numeric string must not sneak through Enum.TryParse
    public void IsVertical_ParsesCaseInsensitivelyWithHorizontalFallback(string? raw, bool expected)
        => Assert.Equal(expected, TabStripLayout.IsVertical(raw));

    [Theory]
    [InlineData(220, 220)]
    [InlineData(100, 140)]
    [InlineData(9999, 600)]
    [InlineData(0, 220)]
    [InlineData(-5, 220)]
    [InlineData(double.NaN, 220)]
    [InlineData(double.PositiveInfinity, 220)]
    public void ClampSidebarWidth_ClampsAndDefendsAgainstGarbage(double input, double expected)
        => Assert.Equal(expected, TabStripLayout.ClampSidebarWidth(input));

    [Fact]
    public void ComputeDraggedWidth_AddsDeltaAndClamps()
    {
        Assert.Equal(250, TabStripLayout.ComputeDraggedWidth(startWidth: 220, startX: 100, currentX: 130));
        Assert.Equal(140, TabStripLayout.ComputeDraggedWidth(startWidth: 150, startX: 100, currentX: 0));
        Assert.Equal(600, TabStripLayout.ComputeDraggedWidth(startWidth: 590, startX: 0, currentX: 500));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TabStripLayoutTests"
```

Expected: compile FAIL — `TabStripLayout` not defined.

- [ ] **Step 3: Implement**

`src/NovaTerminal.App/Shell/TabStripLayout.cs`:

```csharp
using System;

namespace NovaTerminal.Shell
{
    internal enum TabStripOrientationKind
    {
        Horizontal,
        Vertical,
    }

    /// <summary>
    /// Pure math/parsing for the tab strip layout modes. No Avalonia types so the
    /// tests stay plain [Fact]s (same split as TitleBarLayoutResolver).
    /// </summary>
    internal static class TabStripLayout
    {
        internal const double MinSidebarWidth = 140;
        internal const double MaxSidebarWidth = 600;
        internal const double DefaultSidebarWidth = 220;

        /// <summary>Settings-string → mode. Anything unrecognized is Horizontal (a typo must not
        /// be more disruptive than the default) — same contract as TitleBarLayoutResolver.ReadState.</summary>
        internal static bool IsVertical(string? orientation)
            => !string.IsNullOrWhiteSpace(orientation)
               && !double.TryParse(orientation, out _)
               && Enum.TryParse(orientation, ignoreCase: true, out TabStripOrientationKind parsed)
               && Enum.IsDefined(parsed)
               && parsed == TabStripOrientationKind.Vertical;

        internal static double ClampSidebarWidth(double width)
            => double.IsFinite(width) && width > 0
                ? Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth)
                : DefaultSidebarWidth;

        internal static double ComputeDraggedWidth(double startWidth, double startX, double currentX)
            => ClampSidebarWidth(startWidth + (currentX - startX));
    }
}
```

(The `double.TryParse` guard exists because `Enum.TryParse("1", ...)` succeeds on numeric strings.)

- [ ] **Step 4: Run to verify pass**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/NovaTerminal.App/Shell/TabStripLayout.cs tests/NovaTerminal.App.Tests/TabStripLayoutTests.cs
rtk git commit -m "feat(tabs): pure layout helpers for tab strip orientation and sidebar width"
```

---

### Task 4: TabStatusTracker

**Files:**
- Create: `src/NovaTerminal.App/Shell/TabStatusTracker.cs`
- Test: `tests/NovaTerminal.App.Tests/TabStatusTrackerTests.cs` (new, plain `[Fact]`s, project root)

**Interfaces:**
- Consumes: nothing.
- Produces (all `internal`, namespace `NovaTerminal.Shell`):
  - `enum TabTrackerStatus { Idle, Working, Attention }`
  - `sealed class TabStatusTracker` with `static readonly TimeSpan WorkingWindow` (2 s), `static readonly TimeSpan MinAttentionBurst` (5 s), `void NoteOutput(DateTime nowUtc)`, `void NoteBell()`, `void NoteSelected()`, `TabTrackerStatus Evaluate(DateTime nowUtc, bool isSelected)`.

**Semantics** (from the spec, plus one refinement): *Working* = output within `WorkingWindow`. *Attention* = a bell, OR a Working→quiet transition while unselected — but only if the output burst lasted at least `MinAttentionBurst`. The burst-length floor exists so a restored background tab printing its one-line shell prompt at startup does not light up every tab with Attention. Attention is sticky until the tab is selected.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using NovaTerminal.Shell;

public sealed class TabStatusTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Continuous output T0..T0+seconds at 1s intervals (every gap &lt; WorkingWindow).</summary>
    private static TabStatusTracker TrackerWithBurst(int seconds)
    {
        var tracker = new TabStatusTracker();
        for (int s = 0; s <= seconds; s++) tracker.NoteOutput(T0.AddSeconds(s));
        return tracker;
    }

    [Fact]
    public void FreshTracker_IsIdle()
        => Assert.Equal(TabTrackerStatus.Idle, new TabStatusTracker().Evaluate(T0, isSelected: false));

    [Fact]
    public void RecentOutput_IsWorking_EvenWhenSelected()
    {
        var tracker = new TabStatusTracker();
        tracker.NoteOutput(T0);
        Assert.Equal(TabTrackerStatus.Working, tracker.Evaluate(T0.AddSeconds(1), isSelected: true));
    }

    [Fact]
    public void LongBurstGoesQuietWhileUnselected_RaisesAttention()
    {
        var tracker = TrackerWithBurst(6); // burst span 6s >= MinAttentionBurst (5s)
        Assert.Equal(TabTrackerStatus.Attention, tracker.Evaluate(T0.AddSeconds(9), isSelected: false));
    }

    [Fact]
    public void ShortBurstGoesQuiet_StaysIdle() // e.g. restored tab printing its prompt
    {
        var tracker = TrackerWithBurst(0);
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0.AddSeconds(9), isSelected: false));
    }

    [Fact]
    public void LongBurstGoesQuietWhileSelected_StaysIdle() // user was watching; nothing to flag
    {
        var tracker = TrackerWithBurst(6);
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0.AddSeconds(9), isSelected: true));
    }

    [Fact]
    public void Attention_IsSticky_AcrossEvaluations()
    {
        var tracker = TrackerWithBurst(6);
        tracker.Evaluate(T0.AddSeconds(9), isSelected: false);
        Assert.Equal(TabTrackerStatus.Attention, tracker.Evaluate(T0.AddSeconds(60), isSelected: false));
    }

    [Fact]
    public void SelectingTab_ClearsAttention()
    {
        var tracker = TrackerWithBurst(6);
        tracker.Evaluate(T0.AddSeconds(9), isSelected: false);
        tracker.NoteSelected();
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0.AddSeconds(10), isSelected: false));
    }

    [Fact]
    public void EvaluatingAsSelected_AlsoClearsAttention()
    {
        var tracker = TrackerWithBurst(6);
        tracker.Evaluate(T0.AddSeconds(9), isSelected: false);
        tracker.Evaluate(T0.AddSeconds(10), isSelected: true);
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0.AddSeconds(11), isSelected: false));
    }

    [Fact]
    public void Bell_RaisesAttention_WithoutAnyOutput()
    {
        var tracker = new TabStatusTracker();
        tracker.NoteBell();
        Assert.Equal(TabTrackerStatus.Attention, tracker.Evaluate(T0, isSelected: false));
    }

    [Fact]
    public void NewBurstAfterAttention_ShowsWorkingAgain()
    {
        var tracker = TrackerWithBurst(6);
        tracker.Evaluate(T0.AddSeconds(9), isSelected: false); // Attention armed
        tracker.NoteOutput(T0.AddSeconds(20));
        Assert.Equal(TabTrackerStatus.Working, tracker.Evaluate(T0.AddSeconds(21), isSelected: false));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TabStatusTrackerTests"
```

Expected: compile FAIL — `TabStatusTracker` not defined.

- [ ] **Step 3: Implement**

`src/NovaTerminal.App/Shell/TabStatusTracker.cs`:

```csharp
using System;

namespace NovaTerminal.Shell
{
    internal enum TabTrackerStatus
    {
        Idle,
        Working,
        Attention,
    }

    /// <summary>
    /// Heuristic per-tab status for the vertical tab sidebar, fed by the pane events the
    /// window already receives. Pure logic — no Avalonia, no timers; the window supplies
    /// "now" and polls <see cref="Evaluate"/> (a 1s DispatcherTimer while vertical mode is
    /// active). Deliberately approximate: works with any agent CLI, zero cooperation needed.
    /// An explicit protocol (OSC / shell-integration marks) would plug in here later.
    /// </summary>
    internal sealed class TabStatusTracker
    {
        /// <summary>Output newer than this counts as "still working".</summary>
        internal static readonly TimeSpan WorkingWindow = TimeSpan.FromSeconds(2);

        /// <summary>A burst must span at least this long for its end to raise Attention.
        /// Filters one-shot output (a restored tab printing its prompt) from "the agent
        /// streamed for a while and stopped — probably finished or waiting for input".</summary>
        internal static readonly TimeSpan MinAttentionBurst = TimeSpan.FromSeconds(5);

        private DateTime _burstStartUtc;
        private DateTime _lastOutputUtc;
        private bool _inBurst;
        private bool _attention;

        public void NoteOutput(DateTime nowUtc)
        {
            if (!_inBurst || nowUtc - _lastOutputUtc > WorkingWindow)
            {
                _inBurst = true;
                _burstStartUtc = nowUtc;
            }

            _lastOutputUtc = nowUtc;
        }

        public void NoteBell() => _attention = true;

        /// <summary>Selecting the tab acknowledges it: Attention clears. The burst history
        /// survives, so a still-streaming agent keeps showing Working after selection.</summary>
        public void NoteSelected() => _attention = false;

        public TabTrackerStatus Evaluate(DateTime nowUtc, bool isSelected)
        {
            bool working = _inBurst && nowUtc - _lastOutputUtc <= WorkingWindow;

            if (_inBurst && !working)
            {
                // The burst just ended. Long burst + nobody watching => the user should look.
                if (!isSelected && _lastOutputUtc - _burstStartUtc >= MinAttentionBurst)
                {
                    _attention = true;
                }

                _inBurst = false;
            }

            if (isSelected)
            {
                _attention = false;
            }

            if (working) return TabTrackerStatus.Working;
            return _attention ? TabTrackerStatus.Attention : TabTrackerStatus.Idle;
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Same command as Step 2. Expected: 10 PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/NovaTerminal.App/Shell/TabStatusTracker.cs tests/NovaTerminal.App.Tests/TabStatusTrackerTests.cs
rtk git commit -m "feat(tabs): heuristic TabStatusTracker (working/attention/idle)"
```

---

### Task 5: Last-non-empty-row helper in NovaTerminal.VT

**Files:**
- Modify: `src/NovaTerminal.VT/Export/TerminalExporter.cs` (existing `ExportToPlainText(TerminalBuffer)` lives here)
- Test: the existing exporter test file in `tests/NovaTerminal.VT.Tests/` (find it with `rtk grep "ExportToPlainText" tests/NovaTerminal.VT.Tests`), or a new `TerminalExporterLastRowTests.cs` beside it if the existing file is unwieldy.

**Interfaces:**
- Consumes: `TerminalBuffer` (`Rows`, `Cols`, `GetCell(col,row)`, `GetGrapheme(col,row)`, `Lock`).
- Produces: `public static string GetLastNonEmptyRowText(TerminalBuffer buffer)` on `NovaTerminal.VT.Export.TerminalExporter` — acquires the buffer read lock itself (callers must NOT hold it; `TerminalBuffer.Lock` is `ReaderWriterLockSlim` with `NoRecursion`, so double-entry throws). Task 7 consumes this.

- [ ] **Step 1: Read the prior art**

Read `src/NovaTerminal.VT/Export/TerminalExporter.cs` in full (it's small). Note exactly how `ExportToPlainText` (a) takes the read lock, (b) iterates cells, (c) handles `IsWideContinuation` and null/`'\0'` cells. The new method must mirror that cell handling verbatim. Then find the existing exporter tests and note how they construct/populate a `TerminalBuffer` (there will be a helper — reuse it).

- [ ] **Step 2: Write the failing tests**

Add to the exporter test file (using whatever buffer-construction helper Step 1 found — shown here as `MakeBuffer(cols, rows)` + feeding text through the same mechanism the existing tests use):

```csharp
    [Fact]
    public void GetLastNonEmptyRowText_ReturnsBottomMostNonEmptyRow()
    {
        // Screen: row0 "alpha", row1 "beta", rows below blank.
        var buffer = MakeBufferWithLines("alpha", "beta");
        Assert.Equal("beta", TerminalExporter.GetLastNonEmptyRowText(buffer));
    }

    [Fact]
    public void GetLastNonEmptyRowText_EmptyScreen_ReturnsEmptyString()
    {
        var buffer = MakeBuffer(80, 24);
        Assert.Equal(string.Empty, TerminalExporter.GetLastNonEmptyRowText(buffer));
    }

    [Fact]
    public void GetLastNonEmptyRowText_TrimsTrailingWhitespace()
    {
        var buffer = MakeBufferWithLines("hello   ");
        Assert.Equal("hello", TerminalExporter.GetLastNonEmptyRowText(buffer));
    }
```

(`MakeBufferWithLines` = write each string on its own row starting at row 0, via the same path the existing exporter tests use — e.g. feeding `"alpha\r\nbeta"` through the parser helper if that's their pattern. Adapt names to the file's conventions; the three behaviors under test are the contract.)

- [ ] **Step 3: Run to verify failure**

```bash
scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~GetLastNonEmptyRowText"
```

Expected: compile FAIL — method not defined.

- [ ] **Step 4: Implement**

In `TerminalExporter`, mirroring `ExportToPlainText`'s locking and cell handling:

```csharp
        /// <summary>
        /// Bottom-most viewport row that has any visible text, trimmed — the tab sidebar's
        /// one-line output preview. Takes the buffer read lock itself; callers must not
        /// already hold it (the lock is NoRecursion).
        /// </summary>
        public static string GetLastNonEmptyRowText(TerminalBuffer buffer)
        {
            buffer.Lock.EnterReadLock();
            try
            {
                for (int r = buffer.Rows - 1; r >= 0; r--)
                {
                    var sb = new StringBuilder(buffer.Cols);
                    for (int c = 0; c < buffer.Cols; c++)
                    {
                        if (buffer.GetCell(c, r).IsWideContinuation) continue;
                        sb.Append(buffer.GetGrapheme(c, r));
                    }

                    string line = sb.ToString().Replace('\0', ' ').TrimEnd();
                    if (line.Length > 0) return line;
                }

                return string.Empty;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }
        }
```

If `ExportToPlainText` handles empty cells differently (e.g. `GetGrapheme` already maps `'\0'`), copy ITS handling and drop the `Replace` — Step 1's reading wins over this snippet.

- [ ] **Step 5: Run to verify pass**

Same command as Step 3. Expected: PASS. Also run the whole exporter test class to confirm no regression.

- [ ] **Step 6: Commit**

```bash
rtk git add src/NovaTerminal.VT/Export/TerminalExporter.cs tests/NovaTerminal.VT.Tests
rtk git commit -m "feat(vt): TerminalExporter.GetLastNonEmptyRowText for tab preview lines"
```

---

### Task 6: Vertical layout mode (theme swap + viewport guards + activation hooks)

> **AMENDED during execution (2026-08-27):** the two-`ControlTheme` runtime swap specified below is
> not implementable — Avalonia 12.0.4 throws `InvalidOperationException` ("already has a visual
> parent") when `TabControl.Theme` is swapped while a tab has live content, because the new
> template's `PART_SelectedContentHost` cannot take ownership of the existing content visual.
> Also, `ItemsPanelTemplate` does not exist as a CLR type in Avalonia 12.0.4 (`TabControl.ItemsPanel`
> is `ITemplate<Panel>`).
>
> **Implemented approach instead (same spec outcome, single source of truth preserved):** ONE
> template — the existing inline template extended in place with three named parts:
> `PART_TitleBandSpacer` (Border, Dock=Top, Height 0 in horizontal / 36 in vertical),
> `PART_TabSidebar` (Grid wrapping the existing `PART_TabHeaderScrollViewer`, docked Top in
> horizontal / Left in vertical), and `PART_TabStripResizeGrip` (Border, IsVisible only in
> vertical). `ApplyTabLayout()` sets `_isVerticalTabStrip` + the `vertical-tabs` class and defers
> to `UpdateTabVisuals()`; `UpdateTabHeaderViewport()` reconfigures the parts at runtime:
> `DockPanel.SetDock`, spacer height, grip visibility, the items `StackPanel.Orientation`
> (via `FindTabItemsPresenter()?.Panel` — the panel instance is mutated, never replaced),
> scrollbar visibilities, and the mode-specific sizing/margins already specified below. No
> `Theme` or `ItemsPanel` property is ever reassigned, so no visual reparenting occurs.
> Tests assert geometry, class, badge, and panel orientation instead of theme identity.
> The keyed-resource XAML in Step 3 and the theme-swap lines of Step 4 are superseded by this;
> everything else in the task (guards, activation hooks, test intent, regression gate) stands.

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml` (TabControl at lines ~71–109, TabItem styles at ~31–45)
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (`UpdateTabHeaderViewport` ~line 736, `EnsureSelectedTabHeaderVisible` ~line 824, ctor end ~line 2699–2722, `OpenSettings` post-save sequence ~line 5935–5961)
- Test: `tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs` (new)

**Interfaces:**
- Consumes: `TabStripLayout.IsVertical`, `TabStripLayout.ClampSidebarWidth` (Task 3); `_settings.TabStripOrientation` / `.VerticalTabStripWidth` (Task 1).
- Produces: `internal void ApplyTabLayout()` and `internal bool IsVerticalTabStripActive => _isVerticalTabStrip;` on `MainWindow`; XAML resources `HorizontalTabsTheme`, `VerticalTabsTheme`, `HorizontalTabItemsPanel`, `VerticalTabItemsPanel`; template part `PART_TabStripResizeGrip` (wired in Task 9). Tasks 7–11 consume `ApplyTabLayout` and `_isVerticalTabStrip`.

- [ ] **Step 1: Write the failing headless tests**

Create `tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs`. Copy the reflection helpers pattern from `MainWindowTitleBarTests.cs` (bottom of that file) for the two private members used here:

```csharp
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

public sealed class VerticalTabStripTests
{
    private static TerminalSettings GetSettings(NovaTerminal.MainWindow window)
        => (TerminalSettings)typeof(NovaTerminal.MainWindow)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static ScrollViewer? InvokeFindTabHeaderScrollViewer(NovaTerminal.MainWindow window)
        => (ScrollViewer?)typeof(NovaTerminal.MainWindow)
            .GetMethod("FindTabHeaderScrollViewer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    private static NovaTerminal.MainWindow CreateShownWindow()
    {
        var window = TestMainWindowFactory.Create();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void ApplyTabLayout_Vertical_SidebarSizedFromSettings_AndOverflowMathSkipped()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        settings.VerticalTabStripWidth = 260;

        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVerticalTabStripActive);

        var scrollViewer = InvokeFindTabHeaderScrollViewer(window);
        Assert.NotNull(scrollViewer);
        Assert.Equal(260, scrollViewer!.Width);
        Assert.True(double.IsNaN(scrollViewer.Height), "vertical strip must not keep the 36px horizontal height");
        Assert.Equal(new Avalonia.Thickness(0), scrollViewer.Margin);

        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(tabs);
        Assert.Contains("vertical-tabs", tabs!.Classes);

        var badge = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(badge);
        Assert.False(badge!.IsVisible);
    }

    [AvaloniaFact]
    public void ApplyTabLayout_GarbageWidth_FallsBackToClampedDefault()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        settings.VerticalTabStripWidth = -1;

        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TabStripLayout.DefaultSidebarWidth, InvokeFindTabHeaderScrollViewer(window)!.Width);
    }

    [AvaloniaFact]
    public void ApplyTabLayout_RoundTrip_RestoresHorizontalStrip_WithoutTouchingTabItems()
    {
        var window = CreateShownWindow();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tabItemsBefore = tabs.Items.Cast<TabItem>().ToList();
        var contentBefore = tabItemsBefore.Select(t => t.Content).ToList();

        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        settings.TabStripOrientation = "Horizontal";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVerticalTabStripActive);
        Assert.DoesNotContain("vertical-tabs", tabs.Classes);

        var scrollViewer = InvokeFindTabHeaderScrollViewer(window)!;
        Assert.Equal(36, scrollViewer.Height);
        Assert.True(double.IsNaN(scrollViewer.Width), "horizontal strip must not keep the sidebar width");
        Assert.True(scrollViewer.Margin.Right > 0, "horizontal strip must reserve title-bar space again");

        // The same TabItem instances with the same Content must survive both swaps —
        // panes/sessions are never disposed or recreated by a layout change.
        Assert.Equal(tabItemsBefore, tabs.Items.Cast<TabItem>().ToList());
        Assert.Equal(contentBefore, tabs.Items.Cast<TabItem>().Select(t => t.Content).ToList());
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VerticalTabStripTests"
```

Expected: compile FAIL — `ApplyTabLayout` / `IsVerticalTabStripActive` not defined.

- [ ] **Step 3: Move the existing TabControl theme into a keyed resource and add the vertical one**

In `MainWindow.axaml`:

(a) Add to the window's resources (create a `<Window.Resources>` section if none exists — check first; `ThemePaletteResources` and friends may already define one):

```xml
        <ItemsPanelTemplate x:Key="HorizontalTabItemsPanel">
            <StackPanel Orientation="Horizontal" />
        </ItemsPanelTemplate>
        <ItemsPanelTemplate x:Key="VerticalTabItemsPanel">
            <StackPanel Orientation="Vertical" />
        </ItemsPanelTemplate>

        <ControlTheme x:Key="HorizontalTabsTheme" TargetType="TabControl">
            <Setter Property="Template">
                <ControlTemplate>
                    <DockPanel>
                        <ScrollViewer Name="PART_TabHeaderScrollViewer"
                                      DockPanel.Dock="Top"
                                      Margin="0,0,440,0"
                                      Height="36"
                                      HorizontalScrollBarVisibility="Hidden"
                                      VerticalScrollBarVisibility="Disabled">
                            <ItemsPresenter Name="PART_ItemsPresenter"
                                          ClipToBounds="True"
                                          ItemsPanel="{TemplateBinding ItemsPanel}" />
                        </ScrollViewer>
                        <ContentPresenter Name="PART_SelectedContentHost"
                                        Margin="0"
                                        Background="Transparent"
                                        Content="{TemplateBinding SelectedContent}"
                                        ContentTemplate="{TemplateBinding SelectedContentTemplate}" />
                    </DockPanel>
                </ControlTemplate>
            </Setter>
        </ControlTheme>

        <ControlTheme x:Key="VerticalTabsTheme" TargetType="TabControl">
            <Setter Property="Template">
                <ControlTemplate>
                    <DockPanel>
                        <!-- Keep the 36px title band clear: the title-bar button overlay and the
                             window-drag area live there (drag bubbles to MainRoot's handler). -->
                        <Border Name="PART_TitleBandSpacer"
                                DockPanel.Dock="Top"
                                Height="36"
                                Background="Transparent" />
                        <Grid Name="PART_TabSidebar" DockPanel.Dock="Left">
                            <ScrollViewer Name="PART_TabHeaderScrollViewer"
                                          Width="220"
                                          HorizontalScrollBarVisibility="Disabled"
                                          VerticalScrollBarVisibility="Auto">
                                <ItemsPresenter Name="PART_ItemsPresenter"
                                              ClipToBounds="True"
                                              ItemsPanel="{TemplateBinding ItemsPanel}" />
                            </ScrollViewer>
                            <Border Name="PART_TabStripResizeGrip"
                                    Width="5"
                                    HorizontalAlignment="Right"
                                    Background="Transparent"
                                    Cursor="SizeWestEast" />
                        </Grid>
                        <ContentPresenter Name="PART_SelectedContentHost"
                                        Margin="0"
                                        Background="Transparent"
                                        Content="{TemplateBinding SelectedContent}"
                                        ContentTemplate="{TemplateBinding SelectedContentTemplate}" />
                    </DockPanel>
                </ControlTemplate>
            </Setter>
        </ControlTheme>
```

The horizontal theme's `ContentPresenter` block must be copied **verbatim from the current inline template** (the snippet above reflects it as of 2026-08-27 — diff against the file, the file wins). Keep the part names `PART_TabHeaderScrollViewer` / `PART_ItemsPresenter` in BOTH themes: `FindTabItemsPresenter` and `FindTabHeaderScrollViewer` locate them by name via `GetVisualDescendants()`.

(b) Replace the TabControl's inline `<TabControl.ItemsPanel>` and `<TabControl.Theme>` blocks with attributes on the element:

```xml
        <TabControl Name="Tabs" Margin="0"
                    TabStripPlacement="Top"
                    Padding="0"
                    BorderThickness="0"
                    ZIndex="60"
                    RenderOptions.BitmapInterpolationMode="HighQuality"
                    ItemsPanel="{StaticResource HorizontalTabItemsPanel}"
                    Theme="{StaticResource HorizontalTabsTheme}">
            <!-- Tabs added dynamically -->
        </TabControl>
```

(c) Add vertical-mode TabItem styles next to the existing `TabItem` styles:

```xml
        <Style Selector="TabControl.vertical-tabs TabItem">
            <Setter Property="MinHeight" Value="44"/>
            <Setter Property="Padding" Value="4,2"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
        </Style>
```

- [ ] **Step 4: Add `ApplyTabLayout` and the mode field to MainWindow.axaml.cs**

Near the other tab fields (~line 80):

```csharp
        private bool _isVerticalTabStrip;
        internal bool IsVerticalTabStripActive => _isVerticalTabStrip;
```

New method (place near `UpdateTabHeaderViewport`):

```csharp
        /// <summary>
        /// Applies the TabStripOrientation setting: swaps the TabControl's theme/items panel
        /// between the title-bar strip and the left sidebar, and rebuilds every tab header for
        /// the new mode. The same TabItem instances (and their pane content) are reused — a
        /// layout swap must never dispose or recreate sessions.
        /// </summary>
        internal void ApplyTabLayout()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            bool vertical = TabStripLayout.IsVertical(_settings.TabStripOrientation);
            _isVerticalTabStrip = vertical;

            tabs.Theme = (Avalonia.Styling.ControlTheme?)Resources[vertical ? "VerticalTabsTheme" : "HorizontalTabsTheme"];
            tabs.ItemsPanel = (ItemsPanelTemplate)Resources[vertical ? "VerticalTabItemsPanel" : "HorizontalTabItemsPanel"]!;
            tabs.Classes.Set("vertical-tabs", vertical);

            foreach (TabItem tab in tabs.Items.Cast<TabItem>().ToList())
            {
                ConfigureTabHeader(tab, GetTabHeaderText(tab));
            }

            // The swapped template materializes on the next layout pass; sizing needs the
            // freshly created PART controls, so defer — same pattern as RebuildTitleBar.
            Dispatcher.UIThread.Post(() => UpdateTabVisuals(), DispatcherPriority.Background);
        }
```

(`UpdateTabVisuals` already ends with `UpdateTabHeaderViewport()`, which does the sizing.)

- [ ] **Step 5: Guard the horizontal-only geometry**

`UpdateTabHeaderViewport()` (~line 736) — add the vertical branch at the top, after the scrollViewer null-check, and clear cross-mode leftovers in both branches:

```csharp
        private void UpdateTabHeaderViewport()
        {
            var scrollViewer = FindTabHeaderScrollViewer();
            if (scrollViewer == null) return;

            if (_isVerticalTabStrip)
            {
                scrollViewer.Margin = new Thickness(0);
                scrollViewer.Height = double.NaN;
                scrollViewer.Width = TabStripLayout.ClampSidebarWidth(_settings.VerticalTabStripWidth);
                scrollViewer.ClipToBounds = true;

                // Horizontal overflow math is meaningless in a scrolling sidebar.
                var badge = this.FindControl<TextBlock>("TabOverflowBadge");
                if (badge != null) badge.IsVisible = false;
                return;
            }

            scrollViewer.Width = double.NaN;
            var titleBar = this.FindControl<Grid>("TitleBar");

            scrollViewer.Margin = GetTabHeaderViewportMargin(
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
                titleBar?.Bounds.Width ?? 0,
                titleBar?.Margin.Right ?? 0);
            scrollViewer.Height = 36;
            scrollViewer.ClipToBounds = true;

            UpdateTabOverflowIndicator();
        }
```

(The lower half is the existing body plus the `scrollViewer.Width = double.NaN;` reset — keep the rest verbatim.)

`EnsureSelectedTabHeaderVisible()` (~line 824) — add at the top:

```csharp
            if (_isVerticalTabStrip)
            {
                (this.FindControl<TabControl>("Tabs")?.SelectedItem as Control)?.BringIntoView();
                return;
            }
```

- [ ] **Step 6: Activate on startup and on settings save**

(a) Ctor end (~line 2715), immediately BEFORE the existing `RebuildTitleBar();` call and its comment:

```csharp
            // Applies TabStripOrientation for the initial window. Like RebuildTitleBar below,
            // this cannot wait for SetupCommandPalette() — that is lazy and never runs at startup.
            ApplyTabLayout();
```

(b) `OpenSettings` post-save sequence (~line 5955): after `RebuildTitleBar();` add `ApplyTabLayout();`.

(c) `OpenSettings` Cancel branch (restores snapshot then re-applies): add `ApplyTabLayout();` after its `UpdateTabVisuals();` — the orientation isn't live-previewed, but the dialog object may have mutated `_settings` before Cancel; re-applying is idempotent and cheap.

- [ ] **Step 7: Run the new tests + the existing tab/title-bar suites**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VerticalTabStripTests|FullyQualifiedName~TabBehaviorTests|FullyQualifiedName~MainWindowTitleBarTests"
```

Expected: all PASS (the horizontal-theme extraction must not change any horizontal behavior — `MainWindowTitleBarTests` is the regression net for that).

- [ ] **Step 8: Commit**

```bash
rtk git add src/NovaTerminal.App/MainWindow.axaml src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs
rtk git commit -m "feat(tabs): vertical tab sidebar layout mode with runtime theme swap"
```

---

### Task 7: Rich vertical rows — status dot, title, preview line

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (`CreateTabHeaderHost` ~line 538, `ConfigureTabHeader` ~line 560, `UpdateTabVisuals` ~line 4768, `TabRuntimeState` ~line 146)
- Test: `tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs` (extend)

**Interfaces:**
- Consumes: `TerminalExporter.GetLastNonEmptyRowText` (Task 5), `TabTrackerStatus` (Task 4), `ResolvePaneForTab(tab)` (existing), `_isVerticalTabStrip` (Task 6).
- Produces: `TabRuntimeState.Status` (`TabStatusTracker`) and `TabRuntimeState.RenderedStatus` (`TabTrackerStatus`) — Task 8 consumes both. `internal static T? FindTabHeaderDescendant<T>(object? node, string name)` — tests consume it. Header part names `"TabStatusDot"` (Ellipse) and `"TabPreviewLine"` (TextBlock).

**Spec deviation note (Task 6, recorded here for reviewers):** the spec proposed a "no tabs" mode on `TitleBarLayoutResolver`. Exploration showed the resolver contains no tab-strip sizing logic at all (it only assigns icons to Pinned/Overflow) — the tab region's reserve actually lives in `GetTabHeaderViewportMargin`/`UpdateTabHeaderViewport`, which Task 6 guards. `TitleBarLayoutResolver` is therefore intentionally untouched.

**Critical constraint:** `FindTabHeaderTextBlock` (line ~515) returns the FIRST `TextBlock` found in child order, and `GetTabHeaderText`/`UpdateTabVisuals` treat that as the title. In the rich row, the title TextBlock must therefore precede the preview TextBlock, and the status indicator must be an `Ellipse` (not a TextBlock).

- [ ] **Step 1: Write the failing tests** (append to `VerticalTabStripTests.cs`)

```csharp
    [AvaloniaFact]
    public void VerticalHeader_TitleIsFirstTextBlock_SoExistingLabelPlumbingStillWorks()
    {
        var window = CreateShownWindow();
        GetSettings(window).TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();

        // The status dot and preview line exist...
        Assert.NotNull(NovaTerminal.MainWindow.FindTabHeaderDescendant<Avalonia.Controls.Shapes.Ellipse>(tab.Header, "TabStatusDot"));
        var preview = NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine");
        Assert.NotNull(preview);

        // ...and the title plumbing (first-TextBlock contract) still resolves the TITLE, not the preview.
        preview!.Text = "PREVIEW_SENTINEL";
        Assert.NotEqual("PREVIEW_SENTINEL", GetTabHeaderTextOf(window, tab));
    }

    private static string GetTabHeaderTextOf(NovaTerminal.MainWindow window, TabItem tab)
        => (string)typeof(NovaTerminal.MainWindow)
            .GetMethod("GetTabHeaderText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab })!;

    [AvaloniaFact]
    public void HorizontalMode_KeepsPlainHeaders()
    {
        var window = CreateShownWindow(); // default settings = horizontal
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();
        Assert.Null(NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine"));
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VerticalTabStripTests"
```

Expected: compile FAIL — `FindTabHeaderDescendant` not defined.

- [ ] **Step 3: Extend `TabRuntimeState` and implement the rich header**

(a) `TabRuntimeState` (~line 146) gains:

```csharp
            public TabStatusTracker Status { get; } = new();
            public TabTrackerStatus RenderedStatus { get; set; }
```

(b) New methods beside `CreateTabHeaderHost`:

```csharp
        private Border CreateVerticalTabHeaderHost(TabItem tab, string text)
        {
            var statusDot = new Avalonia.Controls.Shapes.Ellipse
            {
                Name = "TabStatusDot",
                Width = 8,
                Height = 8,
                Fill = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var headerText = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var previewText = new TextBlock
            {
                Name = "TabPreviewLine",
                Text = string.Empty,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };

            // Title BEFORE preview: FindTabHeaderTextBlock takes the first TextBlock as the
            // title, and UpdateTabVisuals rewrites that one with the display label.
            var textColumn = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical };
            textColumn.Children.Add(headerText);
            textColumn.Children.Add(previewText);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(statusDot, 0);
            Grid.SetColumn(textColumn, 1);
            row.Children.Add(statusDot);
            row.Children.Add(textColumn);

            var headerHost = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 6),
                Child = row
            };

            headerHost.ContextFlyout = new MenuFlyout();
            headerHost.PointerPressed += (_, e) => OnTabHeaderPointerPressed(tab, e);
            ToolTip.SetTip(headerHost, text);
            return headerHost;
        }

        /// <summary>Walks a code-built header object graph by part name (constructed headers
        /// have no name scope, so FindControl can't see inside them).</summary>
        internal static T? FindTabHeaderDescendant<T>(object? node, string name) where T : Control
            => node switch
            {
                T match when match.Name == name => match,
                Border border => FindTabHeaderDescendant<T>(border.Child, name),
                Panel panel => panel.Children.Select(c => FindTabHeaderDescendant<T>(c, name)).FirstOrDefault(c => c != null),
                Decorator decorator => FindTabHeaderDescendant<T>(decorator.Child, name),
                ContentControl contentControl => FindTabHeaderDescendant<T>(contentControl.Content, name),
                _ => null,
            };
```

(c) `ConfigureTabHeader` becomes mode-aware:

```csharp
        private void ConfigureTabHeader(TabItem tab, string text)
        {
            tab.Header = _isVerticalTabStrip
                ? CreateVerticalTabHeaderHost(tab, text)
                : CreateTabHeaderHost(tab, text);
        }
```

(d) In `UpdateTabVisuals` (~line 4768), inside the `foreach (TabItem ti in tabItems)` loop, after the existing tooltip block, add:

```csharp
                if (_isVerticalTabStrip)
                {
                    UpdateVerticalTabExtras(ti, GetOrCreateTabState(ti), borderBrush);
                }
```

and the new method:

```csharp
        private void UpdateVerticalTabExtras(TabItem tab, TabRuntimeState state, IBrush workingBrush)
        {
            if (FindTabHeaderDescendant<Avalonia.Controls.Shapes.Ellipse>(tab.Header, "TabStatusDot") is { } dot)
            {
                dot.Fill = state.RenderedStatus switch
                {
                    TabTrackerStatus.Working => workingBrush,
                    TabTrackerStatus.Attention => new SolidColorBrush(Color.Parse("#FFD25A")),
                    _ => Brushes.Transparent,
                };
            }

            if (FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine") is { } preview)
            {
                preview.Text = ReadPaneLastLine(ResolvePaneForTab(tab));
            }
        }

        private static string ReadPaneLastLine(TerminalPane? pane)
        {
            var buffer = pane?.Buffer;
            if (buffer == null) return string.Empty;

            // GetLastNonEmptyRowText takes the buffer read lock itself (NoRecursion —
            // do NOT wrap this call in another Lock.EnterReadLock).
            return NovaTerminal.VT.Export.TerminalExporter.GetLastNonEmptyRowText(buffer);
        }
```

(`#FFD25A` matches the existing `TabOverflowBadge` accent. `borderBrush` is the theme-blue brush `UpdateTabVisuals` already builds.)

- [ ] **Step 4: Run to verify pass**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs
rtk git commit -m "feat(tabs): rich vertical rows with status dot and last-output preview"
```

---

### Task 8: Status wiring — events → tracker, selection clear, decay timer

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (`OnPaneOutputReceived` ~line 2992, `OnPaneBellReceived` ~line 3010, the `tabs.SelectionChanged` handler in the ctor ~line 2323, `ApplyTabLayout` from Task 6)
- Test: `tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs` (extend)

**Interfaces:**
- Consumes: `TabRuntimeState.Status` / `.RenderedStatus` (Task 7), `TabStatusTracker` (Task 4).
- Produces: `internal void RefreshTabStatuses()` and `internal TabStatusTracker GetTabStatusTracker(TabItem tab)` on `MainWindow` — tests consume both.

- [ ] **Step 1: Write the failing test** (append to `VerticalTabStripTests.cs`)

```csharp
    [AvaloniaFact]
    public void RefreshTabStatuses_WorkingTracker_PaintsThemeDot_AndIdleClearsIt()
    {
        var window = CreateShownWindow();
        GetSettings(window).TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        // Use an UNSELECTED tab: selection clears attention and this test must control state exactly.
        var tab = new TabItem { Content = new Border() };
        tabs.Items.Add(tab);
        window.ApplyTabLayout(); // rebuild headers so the new tab gets a vertical row
        Dispatcher.UIThread.RunJobs();

        window.GetTabStatusTracker(tab).NoteOutput(DateTime.UtcNow);
        window.RefreshTabStatuses();
        Dispatcher.UIThread.RunJobs();

        var dot = NovaTerminal.MainWindow.FindTabHeaderDescendant<Avalonia.Controls.Shapes.Ellipse>(tab.Header, "TabStatusDot");
        Assert.NotNull(dot);
        Assert.NotEqual(Avalonia.Media.Brushes.Transparent, dot!.Fill);

        // 2s later with no output the burst is over (too short for Attention) → dot clears.
        window.GetTabStatusTracker(tab).NoteSelected(); // belt-and-braces: no stale attention
        System.Threading.Thread.Sleep(2100);
        window.RefreshTabStatuses();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Avalonia.Media.Brushes.Transparent, dot.Fill);
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VerticalTabStripTests"
```

Expected: compile FAIL — `GetTabStatusTracker` / `RefreshTabStatuses` not defined.

- [ ] **Step 3: Implement the wiring**

(a) Test seams + refresh pass (near `GetOrCreateTabState`):

```csharp
        internal TabStatusTracker GetTabStatusTracker(TabItem tab) => GetOrCreateTabState(tab).Status;

        /// <summary>Timer-driven decay pass: re-evaluates every tab's heuristic status and queues a
        /// visual refresh only for tabs whose rendered status changed. Vertical mode only.</summary>
        internal void RefreshTabStatuses()
        {
            if (!_isVerticalTabStrip) return;
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var now = DateTime.UtcNow;
            foreach (TabItem tab in tabs.Items.Cast<TabItem>())
            {
                var state = GetOrCreateTabState(tab);
                var status = state.Status.Evaluate(now, isSelected: tab.IsSelected);
                if (status != state.RenderedStatus)
                {
                    state.RenderedStatus = status;
                    QueueTabVisualRefresh(tab);
                }
            }
        }
```

(b) `OnPaneOutputReceived` (~line 2992): immediately after the `if (tab == null) return;` line, add — BEFORE the existing unselected-only block, so the selected tab's Working state is tracked too:

```csharp
            GetOrCreateTabState(tab).Status.NoteOutput(DateTime.UtcNow);
```

(c) `OnPaneBellReceived` (~line 3010): inside the existing debounced unselected-only block, next to `state.HasBell = true;`:

```csharp
                state.Status.NoteBell();
```

(d) The ctor's `tabs.SelectionChanged` handler (~line 2323): where it clears attention for the newly selected tab (it calls `ClearTabAttention` or equivalent — anchor on that), add:

```csharp
                    GetOrCreateTabState(selectedTab).Status.NoteSelected();
```

(adapting the local variable name to the handler's own).

(e) Decay timer — field near `_tabVisualRefreshScheduled`:

```csharp
        private DispatcherTimer? _tabStatusTimer;
```

and in `ApplyTabLayout()` (Task 6), before the final `Dispatcher.UIThread.Post`:

```csharp
            if (vertical && _tabStatusTimer == null)
            {
                _tabStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _tabStatusTimer.Tick += (_, _) => RefreshTabStatuses();
            }

            if (_tabStatusTimer != null)
            {
                _tabStatusTimer.IsEnabled = vertical;
            }
```

No per-tab cleanup is needed: the tracker lives on `TabRuntimeState`, and `CloseTab` already removes `_tabStateByTab[ti]`; the single timer just stops finding the tab in `tabs.Items`.

- [ ] **Step 4: Run to verify pass**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs
rtk git commit -m "feat(tabs): wire pane events and decay timer into tab status heuristics"
```

---

### Task 9: Resize grip with persisted width

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (`ApplyTabLayout`, new grip wiring)
- Test: `tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs` (extend — presence test only; the drag math is already covered by `TabStripLayoutTests.ComputeDraggedWidth_AddsDeltaAndClamps`)

**Interfaces:**
- Consumes: `TabStripLayout.ComputeDraggedWidth` (Task 3), template part `PART_TabStripResizeGrip` (Task 6).
- Produces: nothing consumed later.

- [ ] **Step 1: Write the failing test** (append to `VerticalTabStripTests.cs`)

```csharp
    [AvaloniaFact]
    public void VerticalMode_HasResizeGrip_HorizontalDoesNot()
    {
        var window = CreateShownWindow();
        Assert.Null(FindResizeGrip(window));

        GetSettings(window).TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(FindResizeGrip(window));
    }

    private static Border? FindResizeGrip(NovaTerminal.MainWindow window)
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "PART_TabStripResizeGrip");
```

- [ ] **Step 2: Run to verify failure**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VerticalMode_HasResizeGrip"
```

Expected: FAIL — the grip exists in the template (Task 6) but only materializes after a layout pass; if this already passes, the test still gates the wiring below. (If it passes, continue — the failing part of this task is behavioral, verified manually in Task 12.)

- [ ] **Step 3: Wire the grip**

New method in MainWindow.axaml.cs:

```csharp
        /// <summary>Wires the sidebar's resize grip. The grip is recreated whenever the
        /// TabControl re-templates (every ApplyTabLayout), so wiring is idempotent per
        /// instance via Tag. Width persists to settings on pointer release only.</summary>
        private void WireTabStripResizeGrip()
        {
            if (!_isVerticalTabStrip) return;

            var grip = this.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "PART_TabStripResizeGrip");
            var scrollViewer = FindTabHeaderScrollViewer();
            if (grip == null || scrollViewer == null || Equals(grip.Tag, "wired")) return;
            grip.Tag = "wired";

            double startWidth = 0;
            double startX = 0;

            grip.PointerPressed += (_, e) =>
            {
                startWidth = scrollViewer.Bounds.Width;
                startX = e.GetPosition(this).X;
                e.Pointer.Capture(grip);
                e.Handled = true;
            };

            grip.PointerMoved += (_, e) =>
            {
                if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
                scrollViewer.Width = TabStripLayout.ComputeDraggedWidth(startWidth, startX, e.GetPosition(this).X);
            };

            grip.PointerReleased += (_, e) =>
            {
                if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
                e.Pointer.Capture(null);
                _settings.VerticalTabStripWidth = scrollViewer.Width;
                _settings.Save();
            };
        }
```

In `ApplyTabLayout()`'s deferred block, extend the post to:

```csharp
            Dispatcher.UIThread.Post(() =>
            {
                WireTabStripResizeGrip();
                UpdateTabVisuals();
            }, DispatcherPriority.Background);
```

(check the exact `GetVisualDescendants` using/namespace already imported by `FindTabHeaderScrollViewer`'s implementation and reuse it).

- [ ] **Step 4: Run the class to verify everything passes**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VerticalTabStripTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/VerticalTabStripTests.cs
rtk git commit -m "feat(tabs): resizable sidebar grip with persisted width"
```

---

### Task 10: Settings window row

**Files:**
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml` (WINDOW section, blur row at ~lines 524–534)
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml.cs` (`LoadCurrentSettings` ~line 2264, `SaveAndClose` ~line 2510)
- Test: manual verification in Task 12 (the settings window has no per-row test convention; the round-trip is covered by Task 1's tests + the drift guards)

**Interfaces:**
- Consumes: `_settings.TabStripOrientation` (Task 1).
- Produces: nothing consumed later.

- [ ] **Step 1: Add the XAML row**

In `SettingsWindow.axaml`, in the WINDOW section directly after the Blur-effect row's closing `<Border .../>` hairline (~line 535), following that row's exact structure:

```xml
                            <Grid ColumnDefinitions="*,360">
                                <StackPanel Grid.Column="0" Spacing="2">
                                    <TextBlock Classes="RowLabel" Text="Tab strip orientation"/>
                                    <TextBlock Classes="RowDesc"  Text="Vertical shows tabs in a left sidebar with agent status and a last-output preview."/>
                                </StackPanel>
                                <ComboBox Name="TabOrientationList" Grid.Column="1" HorizontalAlignment="Stretch" VerticalAlignment="Center">
                                    <ComboBoxItem>Horizontal</ComboBoxItem>
                                    <ComboBoxItem>Vertical</ComboBoxItem>
                                </ComboBox>
                            </Grid>
                            <Border BorderBrush="{StaticResource NtHairline}" BorderThickness="0,0,0,1" Margin="0,14,0,14"/>
```

- [ ] **Step 2: Load into UI**

In `LoadCurrentSettings()` (~line 2264), next to the BlurList block (~line 2354), same pattern:

```csharp
            var tabOrientationList = this.FindControl<ComboBox>("TabOrientationList");
            if (tabOrientationList != null)
            {
                foreach (ComboBoxItem item in tabOrientationList.Items.Cast<ComboBoxItem>())
                {
                    if (string.Equals(item.Content?.ToString(), _settings.TabStripOrientation, StringComparison.OrdinalIgnoreCase))
                    {
                        tabOrientationList.SelectedItem = item;
                        break;
                    }
                }
                if (tabOrientationList.SelectedItem == null && tabOrientationList.ItemCount > 0) tabOrientationList.SelectedIndex = 0;
            }
```

- [ ] **Step 3: Save from UI**

In `SaveAndClose()` (~line 2510), next to the BlurList save (~line 2560):

```csharp
            var tabOrientationList = this.FindControl<ComboBox>("TabOrientationList");
            if (tabOrientationList?.SelectedItem is ComboBoxItem tabOrientationItem)
                _settings.TabStripOrientation = tabOrientationItem.Content?.ToString() ?? "Horizontal";
```

Do NOT add a live-preview event (no `OnTabOrientationChanged`): live preview would require joining the `previewSnapshot`/Cancel-revert dance in `OpenSettings` (#167 regression class). Apply-on-save only; MainWindow already calls `ApplyTabLayout()` in its post-save sequence (Task 6 Step 6).

- [ ] **Step 4: Build + run the settings-adjacent suites**

```bash
scripts/build.ps1 build src/NovaTerminal.App
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~SettingsWindow"
```

Expected: build OK, existing SettingsWindow tests PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/NovaTerminal.App/SettingsWindow.axaml src/NovaTerminal.App/SettingsWindow.axaml.cs
rtk git commit -m "feat(settings): tab strip orientation dropdown in Window section"
```

---

### Task 11: Shortcut + command palette entry

**Files:**
- Modify: `src/NovaTerminal.App/Shell/Shortcuts/ShortcutCatalog.cs` (entries list, lines ~9–59)
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (KeyDown dispatch chain ~lines 2331–2560, `SetupCommandPalette` ~line 4582)
- Test: existing shortcut-catalog tests (run the App.Tests shortcut suites; no new file needed)

**Interfaces:**
- Consumes: `ApplyTabLayout` (Task 6), `TabStripLayout.IsVertical` (Task 3).
- Produces: command id `"toggle_tab_orientation"`, default binding `"Ctrl+Alt+B"`.

- [ ] **Step 1: Verify the default binding is free**

```bash
rtk grep "Ctrl+Alt+B" src/NovaTerminal.App
```

Expected: no hits in `ShortcutCatalog.cs` or the MainWindow dispatch chain. If taken, fall back to `"Ctrl+Alt+U"` (re-grep) and use that consistently in every step below.

- [ ] **Step 2: Add the catalog entry**

In `ShortcutCatalog.cs`, in the "General" group next to the other tab entries:

```csharp
        new("toggle_tab_orientation", "Tabs: Toggle Vertical Tab Sidebar", "General", ShortcutScope.App, "Ctrl+Alt+B"),
```

- [ ] **Step 3: Add the toggle method + KeyDown dispatch block**

Method near `ApplyTabLayout` in MainWindow.axaml.cs:

```csharp
        private void ToggleTabOrientation()
        {
            _settings.TabStripOrientation = TabStripLayout.IsVertical(_settings.TabStripOrientation)
                ? "Horizontal"
                : "Vertical";
            _settings.Save();
            ApplyTabLayout();
        }
```

Dispatch block in the ctor's KeyDown chain, next to the `new_tab` block (~line 2442), same shape:

```csharp
                if (IsShortcut(e, "toggle_tab_orientation", "Ctrl+Alt+B"))
                {
                    RecordCommandUsage("toggle_tab_orientation");
                    ToggleTabOrientation();
                    e.Handled = true;
                    return;
                }
```

- [ ] **Step 4: Register in the command palette**

In `SetupCommandPalette()` (~line 4582), next to the other "General" tab commands (~line 4679):

```csharp
            CommandRegistry.Register("Tabs: Toggle Vertical Tab Sidebar", "General", () => ToggleTabOrientation(), GetEffectiveShortcutBinding("toggle_tab_orientation", "Ctrl+Alt+B"), "toggle_tab_orientation");
```

- [ ] **Step 5: Run the shortcut/palette suites**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Shortcut|FullyQualifiedName~CommandPalette"
```

Expected: PASS (catalog conflict/consistency tests included, if present).

- [ ] **Step 6: Commit**

```bash
rtk git add src/NovaTerminal.App/Shell/Shortcuts/ShortcutCatalog.cs src/NovaTerminal.App/MainWindow.axaml.cs
rtk git commit -m "feat(tabs): shortcut and palette command to toggle vertical tab sidebar"
```

---

### Task 12: Full verification + manual smoke test

**Files:** none (verification only).

- [ ] **Step 1: Run the gating test projects**

```bash
scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests
```

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests
```

```bash
scripts/build.ps1 test tests/NovaTerminal.VT.Tests
```

Expected: all PASS. (Do NOT run whole-solution `test` — it takes 20–30 min.)

- [ ] **Step 2: Run the App.Tests project with CI's filter**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng"
```

Expected: PASS (this lane is non-blocking in CI, so a local pass is the only gate).

- [ ] **Step 3: Build and hand the user a manual smoke script**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Then report done and give the user these manual steps (do not attempt UI automation — SendKeys/foreground automation is unreliable on this machine):

1. Launch NovaTerminal. Open Settings → WINDOW → set "Tab strip orientation" to Vertical → Save. Tabs should move into a left sidebar; title-bar buttons stay put; the top band still drags the window.
2. Open 3+ tabs. In one, run something chatty for >5 s (e.g. `ping -t localhost`, or a Claude Code session). Its row should show a colored "working" dot while streaming; the preview line under the title should show the latest output line. Switch to another tab; when the chatty one goes quiet, its dot should turn amber (Attention) until you select it.
3. Drag the sidebar's right edge to resize; restart the app; the width and vertical mode should persist.
4. Press Ctrl+Alt+B (or the chosen binding) to flip back to horizontal; verify the classic strip returns, tabs/sessions intact, overflow badge working.
5. Middle-click a sidebar row to close a tab; right-click for the context menu; Ctrl+Tab MRU switching should behave exactly as horizontal mode.

- [ ] **Step 4: Final commit (if any stragglers) and wrap-up**

```bash
rtk git status
```

Expected: clean tree; all work committed task-by-task.
