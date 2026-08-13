# Dead pane indicator (#311) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A local pane whose shell dies says so and offers Enter-to-restart, and a pane whose shell exited cleanly closes itself.

**Architecture:** The exit funnel already exists — `RustPtySession.OnExit` → (UI thread) `TerminalPane.HandleSessionExit` → `ProcessExited` → `MainWindow.OnPaneProcessExited`. This plan adds a pure policy function, a banner writer on the pane, a pane-targeted close on the window, and wires the three together at the end of that funnel. SSH panes keep their existing banner and never auto-close.

**Tech Stack:** C# / .NET 10, Avalonia 12, xunit v3 (`[Fact]` for pure logic, `[AvaloniaFact]` for anything touching controls).

Design: [`docs/plans/2026-08-12-dead-pane-indicator-design.md`](2026-08-12-dead-pane-indicator-design.md).

## Global Constraints

- **Build and test only through the wrappers:** `scripts/build.ps1 <args>` (PowerShell) or `scripts/build.sh <args>` (bash). A raw `dotnet build` hangs when stdout is captured. See CLAUDE.md.
- **Never run the whole solution's tests** — that is 20–30 minutes of headless Avalonia. Run the one project, with a `--filter`.
- The first build in a fresh worktree compiles the Rust natives via cargo (several minutes). Do not pass `SKIP_RUST_NATIVE_BUILD=1` for the test tasks here: `NovaTerminal.App.Tests` panes need `rusty_pty.dll` in the output.
- **Setting name and values, exactly:** `ShellExitPolicy`, one of `"Never"`, `"Graceful"`, `"Always"`, default `"Never"`. Unrecognised values behave as `"Graceful"`.
- **Banner text, exactly** (the exit-code line is omitted when the code is 0):
  ```
  [Shell exited]
  [Exit code: N]
  [Press Enter to restart]
  ```
- SSH panes are out of scope for every behaviour change here. The existing `[SSH session disconnected]` banner must remain byte-for-byte identical.
- `ShellExitPolicy` is settings.json-only, with no `SettingsWindow` control. This matches its sibling `PaneClosePolicy`, which has no UI either. (The design doc said "a dropdown beside the existing pane-close policy control"; there is no such control — the design is wrong on that one point and this plan is the correction.)
- Do not add the setting to `TerminalPane.ApplySettings`'s `effectiveSettings` whitelist. The pane never reads this setting; `MainWindow` does.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/NovaTerminal.App/Shell/TerminalSettings.cs` | Holds `ShellExitPolicy` | 1 |
| `src/NovaTerminal.App/MainWindow.axaml.cs` | `ShouldClosePaneOnExit` (pure), `ClosePaneAsync` (pane-targeted), exit wiring | 1, 3, 4 |
| `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs` | `WriteLocalExitBanner` | 2 |
| `src/NovaTerminal.McpServer/Tools/SettingsTools.cs` | Agent-facing settings docs + validation | 5 |
| `tests/NovaTerminal.App.Tests/Core/ShellExitPolicyTests.cs` (new) | Policy matrix | 1 |
| `tests/NovaTerminal.App.Tests/Infra/TerminalBufferText.cs` (new) | Shared "what does the buffer show" test helper | 2 |
| `tests/NovaTerminal.App.Tests/Core/TerminalPaneExitBannerTests.cs` (new) | Banner text | 2 |
| `tests/NovaTerminal.App.Tests/Core/MainWindowShellExitTests.cs` (new) | Close targeting + end-to-end exit behaviour | 3, 4 |

---

### Task 1: The exit policy decision

Pure function plus the setting that feeds it. Nothing is wired yet, so behaviour does not change.

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalSettings.cs:53` (add next to `PaneClosePolicy`)
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs:3493` (add next to `ShouldAutoAcceptRunningPaneClose`)
- Test: `tests/NovaTerminal.App.Tests/Core/ShellExitPolicyTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `TerminalSettings.ShellExitPolicy` (string, default `"Never"`); `internal static bool NovaTerminal.MainWindow.ShouldClosePaneOnExit(string? shellExitPolicy, bool isSsh, int exitCode)`.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Core/ShellExitPolicyTests.cs`:

```csharp
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// #311: which shell exits close the pane. Pure policy — no window, no pane, no Avalonia,
/// mirroring how <see cref="TabClosePolicyTests"/> covers the sibling pane-close policy.
/// </summary>
public sealed class ShellExitPolicyTests
{
    [Theory]
    // Graceful (the default): a clean exit closes, anything else leaves the pane with a banner.
    [InlineData("Graceful", 0, false, true)]
    [InlineData("Graceful", 1, false, false)]
    [InlineData("Graceful", 255, false, false)]
    // Never: nothing ever closes on its own.
    [InlineData("Never", 0, false, false)]
    [InlineData("Never", 1, false, false)]
    // Always: the exit code stops mattering.
    [InlineData("Always", 0, false, true)]
    [InlineData("Always", 1, false, true)]
    // SSH panes never auto-close, whatever the policy says.
    [InlineData("Graceful", 0, true, false)]
    [InlineData("Always", 0, true, false)]
    [InlineData("Always", 1, true, false)]
    public void PolicyDecidesWhetherTheDyingPaneCloses(string policy, int exitCode, bool isSsh, bool expected)
    {
        Assert.Equal(expected, NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh, exitCode));
    }

    [Theory]
    [InlineData("graceful")]
    [InlineData("  Graceful  ")]
    [InlineData("ALWAYS")]
    public void PolicyMatchingIsCaseAndWhitespaceInsensitive(string policy)
    {
        // "ALWAYS" closes on a non-zero code; the two Graceful spellings do not.
        bool expected = policy.Trim().Equals("ALWAYS", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh: false, exitCode: 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Sometimes")]
    public void UnrecognisedPolicyBehavesAsGraceful(string? policy)
    {
        // A typo in a hand-edited settings file must not silently mean "never tell me anything".
        Assert.True(NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh: false, exitCode: 0));
        Assert.False(NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh: false, exitCode: 1));
    }

    [Fact]
    public void DefaultSettingIsNever()
    {
        // Until #313 lands and the real exit status can be captured, defaulting to Never
        // is conservative: every dead local pane gets the banner with its Enter-to-restart hint.
        Assert.Equal("Never", new TerminalSettings().ShellExitPolicy);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~ShellExitPolicyTests"
```

Expected: compile error — `'MainWindow' does not contain a definition for 'ShouldClosePaneOnExit'` and `'TerminalSettings' does not contain a definition for 'ShellExitPolicy'`.

- [ ] **Step 3: Add the setting**

In `src/NovaTerminal.App/Shell/TerminalSettings.cs`, directly below `public string PaneClosePolicy { get; set; } = "Confirm";`:

```csharp
        // What happens to a pane when its shell exits (#311). Three values: "Never" (default for now)
        // always keeps the pane and shows the exit banner; "Graceful" closes it on a clean exit (code 0)
        // and keeps it otherwise; "Always" closes it whatever the code. Default is "Never" — a
        // conservative choice until #313 lands, at which point the real exit status from the child process
        // can be captured (today a local PTY reports 0 for every exit, even when the console host crashed).
        // SSH panes ignore this and always keep their reconnect banner. Unrecognised values behave as
        // "Graceful".
        public string ShellExitPolicy { get; set; } = "Never";
```

- [ ] **Step 4: Add the decision**

In `src/NovaTerminal.App/MainWindow.axaml.cs`, directly above `internal static bool ShouldAutoAcceptRunningPaneClose(`:

```csharp
        /// <summary>
        /// #311: whether a pane whose shell just exited should close itself. Protection and
        /// close-in-progress guards deliberately live outside this decision — the caller attempts
        /// the close and falls back to the banner if it does not happen, so that a pane which
        /// cannot close still says something.
        /// </summary>
        internal static bool ShouldClosePaneOnExit(string? shellExitPolicy, bool isSsh, int exitCode)
        {
            // A dropped SSH session keeps its [Press Enter to reconnect] banner regardless: the
            // remote end may have cost an MFA prompt or a jump host to reach.
            if (isSsh) return false;

            string policy = shellExitPolicy?.Trim() ?? string.Empty;

            if (policy.Equals("Never", StringComparison.OrdinalIgnoreCase)) return false;
            if (policy.Equals("Always", StringComparison.OrdinalIgnoreCase)) return true;

            // "Graceful" and anything unrecognised.
            return exitCode == 0;
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~ShellExitPolicyTests"
```

Expected: PASS, 15 tests.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Shell/TerminalSettings.cs src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/ShellExitPolicyTests.cs
git commit -m "feat(pane): add ShellExitPolicy and the exit-close decision (#311)"
```

---

### Task 2: The local exit banner

The pane gains a way to say its shell is gone. Still not wired — only the new method's own test calls it.

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs:3753` (next to `WriteSshDisconnectedBanner`)
- Create: `tests/NovaTerminal.App.Tests/Infra/TerminalBufferText.cs`
- Test: `tests/NovaTerminal.App.Tests/Core/TerminalPaneExitBannerTests.cs` (create)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `internal void TerminalPane.WriteLocalExitBanner(int code)`; `internal static string NovaTerminal.Tests.Infra.TerminalBufferText.Visible(TerminalBuffer buffer)` — Task 4's tests use it too.

- [ ] **Step 1: Write the shared buffer-text helper**

`TerminalPaneSshDisconnectTests` has this logic as a private static; Task 4 needs it as well, so it
lands in one shared place rather than being copied a third time. Create
`tests/NovaTerminal.App.Tests/Infra/TerminalBufferText.cs`:

```csharp
using NovaTerminal.VT;

namespace NovaTerminal.Tests.Infra;

/// <summary>
/// What a pane actually shows: the viewport rendered as plain text, for asserting on banners and
/// other terminal output. The buffer's viewport is private, hence the reflection.
/// </summary>
internal static class TerminalBufferText
{
    public static string Visible(TerminalBuffer buffer)
    {
        var field = typeof(TerminalBuffer).GetField(
            "_viewport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var viewport = (TerminalRow[])field!.GetValue(buffer)!;
        return string.Join("\n", viewport.Select(RowText)).TrimEnd();
    }

    private static string RowText(TerminalRow row)
    {
        char[] chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
        return new string(chars).TrimEnd();
    }
}
```

Leave `TerminalPaneSshDisconnectTests` alone — migrating it is unrelated churn for this task.

- [ ] **Step 2: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Core/TerminalPaneExitBannerTests.cs`:

```csharp
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Platform;
using NovaTerminal.Tests.Infra;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// #311: a local pane whose shell died must say so, and must say how to get it back — Enter
/// already restarts it (<c>TerminalPane.ShouldReconnectOnEnter</c> is not SSH-gated), which is
/// exactly the part users could not discover.
/// </summary>
public sealed class TerminalPaneExitBannerTests
{
    [AvaloniaFact]
    public void LocalExitBanner_NonZeroCode_NamesTheCodeAndTheRestartKey()
    {
        using var pane = new TerminalPane(LocalProfile());

        pane.WriteLocalExitBanner(1);

        string visibleText = TerminalBufferText.Visible(pane.Buffer!);
        Assert.Contains("[Shell exited]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Exit code: 1]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Press Enter to restart]", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void LocalExitBanner_CleanExit_OmitsTheExitCodeLine()
    {
        using var pane = new TerminalPane(LocalProfile());

        pane.WriteLocalExitBanner(0);

        string visibleText = TerminalBufferText.Visible(pane.Buffer!);
        Assert.Contains("[Shell exited]", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit code", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Press Enter to restart]", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void SshDisconnectBanner_IsUnchanged()
    {
        // #311 must not disturb the banner SSH users already know.
        var profile = new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshHost = "server.example",
            SshUser = "nova"
        };
        using var pane = new TerminalPane(profile);

        pane.HandleSessionExitForTesting(17);

        string visibleText = TerminalBufferText.Visible(pane.Buffer!);
        Assert.Contains("[SSH session disconnected]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Exit code: 17]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Press Enter to reconnect]", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("Shell exited", visibleText, StringComparison.Ordinal);
    }

    private static TerminalProfile LocalProfile() => new()
    {
        Name = "PowerShell",
        Type = ConnectionType.Local,
        Command = "pwsh.exe"
    };
}
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TerminalPaneExitBannerTests"
```

Expected: compile error — `'TerminalPane' does not contain a definition for 'WriteLocalExitBanner'`.

- [ ] **Step 4: Write the banner**

In `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`, directly below `WriteSshDisconnectedBanner`:

```csharp
        /// <summary>
        /// #311: a local pane whose shell exited. Same shape as the SSH banner — including
        /// dropping the exit-code line when the code is 0 — because the restart it advertises is
        /// the same mechanism: Enter on a dead session reaches <see cref="Reconnect"/>.
        /// No interpolated value is caller- or remote-derived, so nothing needs sanitizing.
        /// </summary>
        internal void WriteLocalExitBanner(int code)
        {
            string exitCodeLine = code == 0
                ? string.Empty
                : $"[Exit code: {code}]\r\n";
            WriteBanner(
                $"\r\n[Shell exited]\r\n{exitCodeLine}[Press Enter to restart]\r\n");
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TerminalPaneExitBannerTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.App.Tests/Infra/TerminalBufferText.cs tests/NovaTerminal.App.Tests/Core/TerminalPaneExitBannerTests.cs
git commit -m "feat(pane): banner for a local shell that exited (#311)"
```

---

### Task 3: Close the pane that died, not the one on screen

`CloseActivePaneAsync` reads `_currentPane` for the split case but falls back to `tabs.SelectedItem` when the pane is alone in its tab, and its zoom-exit uses `TryGetSelectedTab`. `UpdateActivePane(pane)` does not select that pane's tab. Auto-close routed through it would close whatever tab the user is looking at when a background shell dies. This task makes the close pane-targeted, and returns whether it happened.

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs:3364-3456` (`CloseActivePaneAsync`)
- Test: `tests/NovaTerminal.App.Tests/Core/MainWindowShellExitTests.cs` (create)

**Interfaces:**
- Consumes: nothing from Tasks 1–2.
- Produces: `private async Task<bool> ClosePaneAsync(TerminalPane pane, bool skipConfirm = false)` — true when the pane (or its tab) actually went away. `CloseActivePaneAsync(bool skipConfirm = false)` stays as the caller for user-initiated closes.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Core/MainWindowShellExitTests.cs`:

```csharp
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// #311. The targeting test is the important one: a shell dying in a background tab — a build, an
/// agent session, exactly the tabs you are not watching — must not close the tab in front of you.
/// </summary>
public sealed class MainWindowShellExitTests
{
    [AvaloniaFact]
    public async Task ClosePaneAsync_ClosesTheGivenPanesTab_NotTheSelectedOne()
    {
        using var fixture = TwoTabFixture.Create();

        bool closed = await fixture.ClosePaneAsync(fixture.BackgroundPane, skipConfirm: true);

        Assert.True(closed);
        Assert.Single(fixture.Tabs.Items);
        Assert.Same(fixture.SelectedTab, fixture.Tabs.Items[0]);
    }

    private sealed class TwoTabFixture : IDisposable
    {
        private TwoTabFixture(NovaTerminal.MainWindow window, TabControl tabs, TabItem selectedTab, TerminalPane backgroundPane)
        {
            Window = window;
            Tabs = tabs;
            SelectedTab = selectedTab;
            BackgroundPane = backgroundPane;
        }

        public NovaTerminal.MainWindow Window { get; }
        public TabControl Tabs { get; }
        public TabItem SelectedTab { get; }
        public TerminalPane BackgroundPane { get; }

        public Task<bool> ClosePaneAsync(TerminalPane pane, bool skipConfirm)
        {
            var method = typeof(NovaTerminal.MainWindow)
                .GetMethod("ClosePaneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task<bool>)method.Invoke(Window, [pane, skipConfirm])!;
        }

        public static TwoTabFixture Create()
        {
            AppServiceBundle bundle = AppServices.BuildForDesigner();
            var window = new NovaTerminal.MainWindow(bundle);
            TabControl tabs = window.FindControl<TabControl>("Tabs")!;
            var settings = (TerminalSettings)typeof(NovaTerminal.MainWindow)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;

            tabs.Items.Clear();
            TabItem background = CreateTab(window, tabs, settings, "Background");
            TabItem selected = CreateTab(window, tabs, settings, "Selected");
            tabs.SelectedItem = selected;

            return new TwoTabFixture(window, tabs, selected, (TerminalPane)background.Content!);
        }

        private static TabItem CreateTab(NovaTerminal.MainWindow window, TabControl tabs, TerminalSettings settings, string title)
        {
            var tabSession = new TabSession
            {
                Title = title,
                Root = new PaneNode
                {
                    Type = NodeType.Leaf,
                    Command = "cmd.exe",
                    Arguments = string.Empty,
                    PaneId = Guid.NewGuid().ToString()
                }
            };

            TabItem tab = SessionManager.CreateRestoredTabItem(tabSession, settings)!;
            tabs.Items.Add(tab);

            // The production entry point for restored content — it is what wires the pane's
            // events to the window (ProcessExited included, which Task 4 depends on).
            typeof(NovaTerminal.MainWindow)
                .GetMethod("InitializeRestoredTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [tabs]);

            return tab;
        }

        public void Dispose() => Window.Close();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~MainWindowShellExitTests"
```

Expected: FAIL — `GetMethod("ClosePaneAsync")` returns null, so the test throws `NullReferenceException`.

- [ ] **Step 3: Extract the pane-targeted close**

In `src/NovaTerminal.App/MainWindow.axaml.cs`, replace the whole of `CloseActivePaneAsync` (lines 3364–3456) with:

```csharp
        private Task CloseActivePaneAsync(bool skipConfirm = false)
        {
            if (_currentPane == null) return Task.CompletedTask;
            return ClosePaneAsync(_currentPane, skipConfirm);
        }

        /// <summary>
        /// Closes a specific pane, resolving everything from that pane rather than from the
        /// selection. The distinction matters for #311: a shell can die in a background tab, and
        /// the old selection-based fallback would have closed the tab the user was looking at.
        /// Returns true when the pane (or its tab) actually went away — callers use that to fall
        /// back to a banner when a protected tab or an in-flight close refuses.
        /// </summary>
        private async Task<bool> ClosePaneAsync(TerminalPane pane, bool skipConfirm = false)
        {
            if (_closePaneInProgress || pane == null) return false;
            _closePaneInProgress = true;

            try
            {
                var paneToClose = pane;
                var paneTab = paneToClose.FindAncestorOfType<TabItem>();
                if (paneTab != null && _paneZoomStateByTab.ContainsKey(paneTab))
                {
                    ExitPaneZoom(paneTab, publishEvent: true);
                }

                // Agent-initiated and exit-driven closes bypass the confirmation dialog: an agent
                // can't answer a modal, a dead shell has nothing left to lose, and an unattended
                // prompt is the stuck state #311 is about.
                if (!skipConfirm && !await ShouldClosePaneAsync(paneToClose))
                {
                    FocusPaneTerminal(paneToClose, defer: true);
                    return false;
                }

                // Check if we are in a split (Parent is Grid with multiple children/splitter)
                if (paneToClose.Parent is Grid parentGrid && parentGrid.Children.Count >= 2)
                {
                    // We are in a split!
                    // 1. Identify Sibling (The non-splitter control that isn't us)
                    var sibling = parentGrid.Children.OfType<Control>()
                                        .FirstOrDefault(c => c != paneToClose && !(c is GridSplitter));

                    if (sibling != null)
                    {
                        // 2. Identify Grandparent
                        var grandParent = parentGrid.Parent;

                        // 3. Detach visuals
                        parentGrid.Children.Clear();

                        // 4. Promote Sibling to Grandparent
                        if (grandParent is ContentPresenter cp) cp.Content = sibling;
                        else if (grandParent is TabItem tab) tab.Content = sibling;
                        else if (grandParent is Grid gpGrid)
                        {
                            Grid.SetRow(sibling, Grid.GetRow(parentGrid));
                            Grid.SetColumn(sibling, Grid.GetColumn(parentGrid));
                            Grid.SetRowSpan(sibling, Grid.GetRowSpan(parentGrid));
                            Grid.SetColumnSpan(sibling, Grid.GetColumnSpan(parentGrid));

                            int index = gpGrid.Children.IndexOf(parentGrid);
                            if (index >= 0)
                            {
                                gpGrid.Children.RemoveAt(index);
                                gpGrid.Children.Insert(index, sibling);
                            }
                            else gpGrid.Children.Add(sibling);
                        }
                        else if (grandParent is Panel p)
                        {
                            int index = p.Children.IndexOf(parentGrid);
                            p.Children.Remove(parentGrid);
                            if (index >= 0) p.Children.Insert(index, sibling);
                            else p.Children.Add(sibling);
                        }

                        // 5. Dispose the closed pane
                        DisposeControlTree(paneToClose);

                        // 6. Focus Sibling
                        FocusFirstPane(sibling);
                        UpdatePaneAutomationLabels();
                        if (paneTab != null)
                        {
                            RefreshLayoutModelForTab(paneTab);
                            PublishPaneEvent(paneTab, paneToClose, PaneAuditEventKind.Close);
                        }
                        return true;
                    }
                }

                // Fallback: If not in a split, close the pane's own tab.
                if (paneTab != null)
                {
                    return await CloseTabAsync(paneTab, skipProcessChecks: true);
                }

                return false;
            }
            finally
            {
                _closePaneInProgress = false;
            }
        }
```

Two behaviour notes for the reviewer: the zoom-exit and the tab fallback now use `paneTab` instead of the selection, and the method reports success. `CloseActivePaneAsync` keeps its signature so its existing callers (`CloseActivePane`, the command registry, `PaneAction.Close`) are untouched.

- [ ] **Step 4: Run the test to verify it passes**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~MainWindowShellExitTests"
```

Expected: PASS, 1 test.

- [ ] **Step 5: Check nothing else regressed**

`CloseActivePaneAsync` is on the user-facing close path, so run the pane and tab suites:

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Pane|FullyQualifiedName~TabClose"
```

Expected: PASS, no new failures.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/MainWindowShellExitTests.cs
git commit -m "refactor(panes): close the pane you name, not the selected one (#311)"
```

---

### Task 4: Wire the exit path

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs:2803` (`OnPaneProcessExited`)
- Test: `tests/NovaTerminal.App.Tests/Core/MainWindowShellExitTests.cs` (extend)

**Interfaces:**
- Consumes: `MainWindow.ShouldClosePaneOnExit` (Task 1), `TerminalPane.WriteLocalExitBanner` (Task 2), `MainWindow.ClosePaneAsync` (Task 3).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

Add to `tests/NovaTerminal.App.Tests/Core/MainWindowShellExitTests.cs`, inside the existing class, above the `TwoTabFixture` nested class. Add `using NovaTerminal.Tests.Infra;` to the file's usings — `TerminalBufferText.Visible` is the shared helper Task 2 created:

```csharp
    [AvaloniaFact]
    public void CleanExit_UnderGraceful_ClosesTheDyingPanesTab_AndLeavesTheSelectedOne()
    {
        using var fixture = TwoTabFixture.Create();
        fixture.Settings.ShellExitPolicy = "Graceful";

        fixture.BackgroundPane.HandleSessionExitForTesting(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Single(fixture.Tabs.Items);
        Assert.Same(fixture.SelectedTab, fixture.Tabs.Items[0]);
    }

    [AvaloniaFact]
    public void NonZeroExit_UnderGraceful_KeepsThePaneAndShowsTheBanner()
    {
        using var fixture = TwoTabFixture.Create();
        fixture.Settings.ShellExitPolicy = "Graceful";

        fixture.BackgroundPane.HandleSessionExitForTesting(1);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fixture.Tabs.Items.Count);
        string visibleText = TerminalBufferText.Visible(fixture.BackgroundPane.Buffer!);
        Assert.Contains("[Shell exited]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Exit code: 1]", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void CleanExit_UnderNever_KeepsThePaneAndShowsTheBanner()
    {
        using var fixture = TwoTabFixture.Create();
        fixture.Settings.ShellExitPolicy = "Never";

        fixture.BackgroundPane.HandleSessionExitForTesting(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fixture.Tabs.Items.Count);
        string visibleText = TerminalBufferText.Visible(fixture.BackgroundPane.Buffer!);
        Assert.Contains("[Shell exited]", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit code", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void CleanExit_OnAProtectedTab_KeepsThePaneAndFallsBackToTheBanner()
    {
        // A dying shell must not be able to defeat tab protection — and a pane that cannot close
        // still has to say something, which is the whole point of #311.
        using var fixture = TwoTabFixture.Create();
        fixture.Settings.ShellExitPolicy = "Graceful";
        fixture.ProtectBackgroundTab();

        fixture.BackgroundPane.HandleSessionExitForTesting(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fixture.Tabs.Items.Count);
        Assert.Contains("[Shell exited]", TerminalBufferText.Visible(fixture.BackgroundPane.Buffer!), StringComparison.Ordinal);
    }
```

Add these members to `TwoTabFixture` (and keep `BackgroundTab` from `Create` by storing the `TabItem`):

```csharp
        public TerminalSettings Settings { get; private init; } = null!;

        public TabItem BackgroundTab { get; private init; } = null!;

        public void ProtectBackgroundTab()
        {
            object state = typeof(NovaTerminal.MainWindow)
                .GetMethod("GetOrCreateTabState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Window, [BackgroundTab])!;
            state.GetType().GetProperty("IsProtected")!.SetValue(state, true);
        }
```

and set `Settings`/`BackgroundTab` in `Create` (`settings` and `background` are already local variables there).

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~MainWindowShellExitTests"
```

Expected: the four new tests FAIL — no banner text in the buffer, and both tabs still present in the Graceful clean-exit case.

- [ ] **Step 3: Wire the handler**

In `src/NovaTerminal.App/MainWindow.axaml.cs`, replace `OnPaneProcessExited` with:

```csharp
        private void OnPaneProcessExited(TerminalPane pane, int exitCode)
        {
            var tab = pane.FindAncestorOfType<TabItem>();
            if (tab == null)
            {
                // No tab to close or mark; the pane still has to say what happened.
                pane.WriteLocalExitBanner(exitCode);
                return;
            }

            var state = GetOrCreateTabState(tab);
            state.LastExitCode = exitCode;
            QueueTabVisualRefresh(tab);

            // SSH panes write their own [SSH session disconnected] banner in HandleSessionExit and
            // never auto-close, so there is nothing left to do for them here.
            if (pane.Profile?.Type == ConnectionType.SSH) return;

            if (!ShouldClosePaneOnExit(_settings.ShellExitPolicy, isSsh: false, exitCode))
            {
                pane.WriteLocalExitBanner(exitCode);
                return;
            }

            _ = HandlePaneExitCloseAsync(pane, exitCode);
        }

        /// <summary>
        /// #311: try to close a pane whose shell exited cleanly, and fall back to the banner when
        /// the close does not happen — a protected tab, an in-flight close, or a pane that has
        /// already left the tree. Every one of those paths has to end with a pane that says
        /// something rather than a pane that silently ignores you.
        /// </summary>
        private async Task HandlePaneExitCloseAsync(TerminalPane pane, int exitCode)
        {
            bool closed = await ClosePaneAsync(pane, skipConfirm: true);
            if (!closed)
            {
                pane.WriteLocalExitBanner(exitCode);
            }
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~MainWindowShellExitTests"
```

Expected: PASS, 5 tests.

- [ ] **Step 5: Run the neighbouring suites**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Pane|FullyQualifiedName~Ssh|FullyQualifiedName~TabClose"
```

Expected: PASS, no new failures — in particular the SSH disconnect tests, which assert the banner this change must not touch.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/MainWindowShellExitTests.cs
git commit -m "feat(pane): announce a dead shell, close it when it exited cleanly (#311)"
```

---

### Task 5: Agent-facing settings surface

`SettingsToolsDriftGuardTests` reflects over `TerminalSettings` and fails when a field is missing from the MCP server's field lists, so this task is not optional.

**Files:**
- Modify: `src/NovaTerminal.McpServer/Tools/SettingsTools.cs:54` (docs table), `:112` (example JSON), `:160` (`StringFields`), `:172` (`KnownFields`)
- Test: `tests/NovaTerminal.McpServer.Tests/SettingsToolsDriftGuardTests.cs` (existing, unmodified)

**Interfaces:**
- Consumes: `TerminalSettings.ShellExitPolicy` (Task 1).
- Produces: nothing.

- [ ] **Step 1: Run the drift guard to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~SettingsToolsDriftGuardTests"
```

Expected: FAIL — `KnownFields_AreExactlyTheSerializedSettings` and `StringFields_AreExactlyTheStringSettings` report `ShellExitPolicy` missing.

- [ ] **Step 2: Add the docs row**

In `src/NovaTerminal.McpServer/Tools/SettingsTools.cs`, directly below the `PaneClosePolicy` row:

```
        | `ShellExitPolicy` | string (enum-like) | "Never"/"Graceful"/"Always". Default "Never". What happens to a pane when its shell exits: keep it with a banner, close it on a clean exit, or always close it. Default is "Never" — a conservative choice until #313 lands and the real exit status from the child process can be captured. SSH panes ignore this and always keep their reconnect banner. Type-checked only; unrecognised values behave as "Graceful". |
```

- [ ] **Step 3: Add the example entry**

Directly below `"PaneClosePolicy": "Confirm",` in the example JSON block:

```
          "ShellExitPolicy": "Never",
```

- [ ] **Step 4: Add it to both field lists**

In `StringFields`, extend the last line:

```csharp
        "BlurEffect", "CursorStyle", "PaneClosePolicy", "ShellExitPolicy", "BackgroundImageStretch",
```

In `KnownFields`, extend the `WheelLinesPerNotch` line:

```csharp
        "WheelLinesPerNotch", "PaneClosePolicy", "ShellExitPolicy", "Keybindings", "TabTemplateRules",
```

- [ ] **Step 5: Run the drift guard to verify it passes**

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~SettingsToolsDriftGuardTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.McpServer/Tools/SettingsTools.cs
git commit -m "docs(mcp): document ShellExitPolicy in the settings tools (#311)"
```

---

### Task 6: Verify the whole thing by hand

Automated tests cover the decision, the banner and the targeting. What they cannot cover is the thing the user reported: a pane that looks alive but is not. Do this once against a real build before opening the PR.

**Files:** none.

- [ ] **Step 1: Build and run the app**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Then launch `src/NovaTerminal.App/bin/Debug/net10.0/NovaTerminal.exe`.

- [ ] **Step 2: Clean exit closes the pane**

In a local tab, type `exit`. Expected: the tab closes. With a second tab open, the window stays.

- [ ] **Step 3: An abnormal death announces itself**

Open a local tab, note its shell PID (`$PID` in pwsh), and kill it from another terminal:

```bash
taskkill /PID <pid> /F
```

Expected: the pane stays and shows `[Shell exited]`, `[Exit code: 1]`, `[Press Enter to restart]`. Press Enter: the shell comes back and the tab's `✖1` glyph clears.

- [ ] **Step 4: A background tab dies without disturbing the front one**

With two tabs open, kill the *background* tab's shell as above while looking at the other tab. Expected: the visible tab is untouched; switching to the background tab shows the banner.

- [ ] **Step 5: SSH is unchanged**

Open an SSH tab, `exit` from the remote shell. Expected: the old `[SSH session disconnected]` / `[Press Enter to reconnect]` banner, and the tab stays open.

- [ ] **Step 6: Commit nothing, open the PR**

```bash
git push -u origin feat/311-dead-pane-indicator
gh pr create --base main --title "feat(pane): a dead pane says so, and a clean exit closes it (#311)" --body "Fixes #311. <summary + the manual verification results from steps 2-5>"
```

---

## Self-Review

**Spec coverage:** behaviour contract → Tasks 1 and 4; banner text → Task 2; protected-tab and reentrancy fallback → Task 4 (with the pane-targeted close it depends on in Task 3); SSH untouched → asserted in Tasks 2 and 4; settings surface → Tasks 1 and 5; data flow → Task 4; testing section → Tasks 1–4 plus the manual pass in Task 6. The spec's `SettingsWindow` dropdown is deliberately not implemented — see Global Constraints.

**Deviations from the design doc, both deliberate:**
1. The decision is `MainWindow.ShouldClosePaneOnExit(...)`, a static on `MainWindow` next to `ShouldAutoAcceptRunningPaneClose`, rather than a new `SessionExitDecision` type. Same purity, same testability (`[Fact]`, no Avalonia harness — see `TabClosePolicyTests`), and it follows the existing house pattern for pane-close policy.
2. No settings UI, because the sibling `PaneClosePolicy` has none either.

**Placeholder scan:** none — every step names its file, its exact code, its command and its expected output.

**Type consistency:** `ShouldClosePaneOnExit(string?, bool, int) → bool` (Task 1) is called with `(_settings.ShellExitPolicy, isSsh: false, exitCode)` in Task 4. `WriteLocalExitBanner(int)` (Task 2) is called in Task 4 in four places. `ClosePaneAsync(TerminalPane, bool) → Task<bool>` (Task 3) is awaited in Task 4's `HandlePaneExitCloseAsync`. Banner strings are identical between Task 2's implementation and Tasks 2/4's assertions.
