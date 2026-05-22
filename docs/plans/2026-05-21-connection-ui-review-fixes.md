# Connection UI Review Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix the regressions introduced by the recent Settings and Connection Manager UI refresh while preserving the new layout direction.

**Architecture:** Keep the fixes additive and local to `NovaTerminal.App`. Do not change VT/rendering paths. For Connection Manager, restore missing behavior and make filtering stable by keeping a permanent live collection for visible rows instead of rebinding the list to snapshots. For theming, preserve the new structure but drive the named brushes from the active `TerminalTheme` rather than hard-coding a dark palette.

**Tech Stack:** Avalonia UI, C#, xUnit, Avalonia.Headless.XUnit

---

### Task 1: Stabilize Connection Manager filtering and result updates

**Files:**
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs`
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml`
- Test: `tests/NovaTerminal.Tests/Ssh/ConnectionManagerTests.cs`

**Step 1: Write the failing tests**

Add tests that cover:
- The favorites filter removes a row immediately after the selected connection is unfavorited.
- The result count updates after favorite toggles while a filter is active.
- The list stays synchronized after search text changes and group changes.

Suggested test shape:

```csharp
[AvaloniaFact]
public void FavoriteFilter_RemovesRowImmediately_WhenFavoriteIsCleared()
{
    var control = CreateMeasuredConnectionManager();
    control.LoadProfiles(new[]
    {
        new TerminalProfile { Type = ConnectionType.SSH, Name = "Prod", SshHost = "prod", Tags = new() { "favorite" } }
    });

    Click(control, "BtnGroupFav");
    SelectFirstRow(control);
    ClickByToolTip(control, "Toggle favorite");

    Assert.Equal(0, GetConnectionList(control).ItemCount());
    Assert.Equal("0 connections", control.FindControl<TextBlock>("ResultCountText")!.Text);
}
```

**Step 2: Run the focused tests to verify they fail**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter ConnectionManagerTests
```

Expected: at least one failure showing stale list contents or stale count after filter changes.

**Step 3: Implement minimal filtering fix**

In `ConnectionManager.axaml.cs`:
- Add a private `ObservableCollection<SshProfileRowViewModel> _visibleRows = new();`
- Bind `ConnectionsList.ItemsSource` to `_visibleRows` once in the constructor.
- Replace `ApplyFilters()` snapshot rebinding with a `RefreshVisibleRows()` method that:
  - Starts from `_viewModel.FilteredRows`
  - Applies group and supported status filters
  - Clears and repopulates `_visibleRows`
- Call `RefreshVisibleRows()` from:
  - `LoadProfiles()`
  - search input handler
  - group button handler
  - status chip handler
  - `OnFavoriteClick()`
- Keep `UpdateResultCountText()` based on `_visibleRows.Count`.

Implementation sketch:

```csharp
private readonly ObservableCollection<SshProfileRowViewModel> _visibleRows = new();

private void RefreshVisibleRows()
{
    IEnumerable<SshProfileRowViewModel> rows = _viewModel.FilteredRows;
    rows = ApplyGroupFilter(rows);
    rows = ApplyStatusFilter(rows);

    _visibleRows.Clear();
    foreach (var row in rows)
    {
        _visibleRows.Add(row);
    }
}
```

**Step 4: Run tests to verify the fix**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter ConnectionManagerTests
```

Expected: all `ConnectionManagerTests` pass.

**Step 5: Commit**

```powershell
git add src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs src/NovaTerminal.App/Controls/ConnectionManager.axaml tests/NovaTerminal.Tests/Ssh/ConnectionManagerTests.cs
git commit -m "fix: stabilize connection manager filtering"
```

### Task 2: Restore the launch-details action in Connection Manager

**Files:**
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml`
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/ConnectionManagerTests.cs`

**Step 1: Write the failing test**

Add a headless UI test that:
- Loads one SSH profile
- Selects the row
- Clicks the details action in the detail header
- Verifies `OnConnectionDetailsRequested` fires with the selected profile

Suggested test shape:

```csharp
[AvaloniaFact]
public void DetailsAction_RaisesConnectionDetailsRequested_ForSelectedRow()
{
    var control = CreateMeasuredConnectionManager();
    control.LoadProfiles(new[] { CreateSshProfile("Prod") });
    SelectFirstRow(control);

    TerminalProfile? received = null;
    control.OnConnectionDetailsRequested += (profile, _) => received = profile;

    ClickByToolTip(control, "Connection details");

    Assert.NotNull(received);
    Assert.Equal("Prod", received!.Name);
}
```

**Step 2: Run the focused tests to verify they fail**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter ConnectionManagerTests
```

Expected: failure because no button currently advertises or triggers the details action.

**Step 3: Restore the UI affordance**

In `ConnectionManager.axaml`:
- Add a detail-header action button for `"Connection details"` next to copy/edit/favorite.
- Reuse the existing `OnDetailsClick` handler and a square `IconBtn` hit target.

In `ConnectionManager.axaml.cs`:
- Keep `OnDetailsClick()` as-is.
- Verify `TryGetRow()` still prefers `_selectedRow`.

**Step 4: Run tests to verify the fix**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter ConnectionManagerTests
```

Expected: details-action test passes and existing action-hit-target test stays green.

**Step 5: Commit**

```powershell
git add src/NovaTerminal.App/Controls/ConnectionManager.axaml src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs tests/NovaTerminal.Tests/Ssh/ConnectionManagerTests.cs
git commit -m "fix: restore connection manager details action"
```

### Task 3: Remove misleading status filters or make them explicitly unsupported

**Files:**
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml`
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/ConnectionManagerTests.cs`

**Step 1: Decide the minimal supported behavior**

Do not invent live connection-state plumbing in this pass. The safe fix is:
- Keep only `All` and `★ Favs`, or
- Disable `Connected` and `Idle` with a tooltip such as `"Not available yet"`

Recommended option: remove `Connected` and `Idle` entirely for now.

**Step 2: Write or update the tests**

Add a test that asserts only supported filter chips are interactive:

```csharp
[AvaloniaFact]
public void StatusChips_ExposeOnlySupportedFilters()
{
    var control = new ConnectionManager();

    Assert.NotNull(control.FindControl<ToggleButton>("ChipAll"));
    Assert.NotNull(control.FindControl<ToggleButton>("ChipFav"));
    Assert.Null(control.FindControl<ToggleButton>("ChipConn"));
    Assert.Null(control.FindControl<ToggleButton>("ChipIdle"));
}
```

If you choose disabled chips instead, assert `IsEnabled == false` and tooltip text.

**Step 3: Implement the UI change**

In `ConnectionManager.axaml`:
- Remove the unsupported chips, or mark them disabled with explicit copy.

In `ConnectionManager.axaml.cs`:
- Remove dead branch handling for `"connected"` / `"idle"`.
- Keep `_selectedStatusKey` limited to supported values.

**Step 4: Run the focused tests**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter ConnectionManagerTests
```

Expected: no misleading interactive filters remain.

**Step 5: Commit**

```powershell
git add src/NovaTerminal.App/Controls/ConnectionManager.axaml src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs tests/NovaTerminal.Tests/Ssh/ConnectionManagerTests.cs
git commit -m "fix: remove unsupported connection status filters"
```

### Task 4: Restore theme-aware styling for Settings and Connection Manager

**Files:**
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml`
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml.cs`
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml`
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs`
- Verify: `src/NovaTerminal.App/MainWindow.axaml.cs`

**Step 1: Preserve the new layout but move the palette back under theme control**

Use the existing named brushes (`NtWindowBg`, `NtChromeBg`, `NtPanel`, `NtPanelAlt`, `NtHairline`, `NtFg*`, `NtBlue*`) as mutable theme resources instead of fixed dark values.

**Step 2: Implement brush updates**

In `SettingsWindow.axaml.cs`:
- Extend `ApplyTheme()` to update the named brushes in `Resources`.
- Keep `RequestedThemeVariant` behavior.

In `ConnectionManager.axaml.cs`:
- Restore `ApplyTheme(TerminalTheme theme)` to update the same resource set instead of being a no-op.
- Keep the current structure and control classes intact.

Suggested helper shape:

```csharp
private void UpdateBrush(string key, Color color)
{
    if (Resources[key] is SolidColorBrush brush)
    {
        brush.Color = color;
    }
    else
    {
        Resources[key] = new SolidColorBrush(color);
    }
}
```

**Step 3: Choose a conservative palette mapping**

Do not build a large theming subsystem in this pass. Map:
- `NtWindowBg` / `NtPanel*` from `theme.Background`
- `NtFg*` from `theme.Foreground` plus lowered opacity / contrast adjustments
- `NtBlue*` from `theme.Cursor` or a fixed accent fallback if the active theme lacks a usable accent

This keeps the new UI readable without hard-coding a permanent dark mode.

**Step 4: Verify manually**

Run:

```powershell
dotnet run --project src/NovaTerminal.App/NovaTerminal.App.csproj
```

Manual checks:
- Open Settings on at least one dark theme and one light theme.
- Open Connection Manager from `MainWindow`.
- Confirm text, borders, and action buttons remain legible.
- Confirm changing the app theme still updates both surfaces.

**Step 5: Commit**

```powershell
git add src/NovaTerminal.App/SettingsWindow.axaml src/NovaTerminal.App/SettingsWindow.axaml.cs src/NovaTerminal.App/Controls/ConnectionManager.axaml src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs
git commit -m "fix: restore theme-aware settings and connection manager chrome"
```

### Task 5: Make the Connection Manager overlay responsive inside the main window

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml`
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml`
- Verify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/ConnectionManagerTests.cs`

**Step 1: Replace hard fixed overlay sizing**

In `MainWindow.axaml`:
- Replace `Width="1080" Height="720"` with:
  - `MaxWidth="1080"`
  - `MaxHeight="720"`
  - stretch alignment or centered layout with outer margin
- Keep the overlay centered and modal-looking.

Recommended shape:

```xml
<Border Name="ConnectionOverlay"
        HorizontalAlignment="Stretch"
        VerticalAlignment="Stretch"
        Margin="24,56,24,24"
        MaxWidth="1080"
        MaxHeight="720">
```

**Step 2: Remove fixed internal column pressure**

In `ConnectionManager.axaml`:
- Replace `ColumnDefinitions="220,420,*"` with proportional columns such as `220,2*,3*`
- Keep the groups column fixed
- Let list/detail columns shrink before clipping

**Step 3: Add a layout regression test**

Add a headless measurement test:

```csharp
[AvaloniaFact]
public void ConnectionManager_CanArrangeWithinSmallOverlay()
{
    var control = new ConnectionManager();
    var host = new Window { Width = 760, Height = 520, Content = control };

    host.Measure(new Size(760, 520));
    host.Arrange(new Rect(0, 0, 760, 520));

    Assert.True(control.Bounds.Width <= 760);
    Assert.True(control.Bounds.Height <= 520);
}
```

This is a coarse guard against reintroducing hard minimums.

**Step 4: Verify manually**

Run:

```powershell
dotnet run --project src/NovaTerminal.App/NovaTerminal.App.csproj
```

Manual checks:
- Resize the main window near its minimum size.
- Open Connection Manager.
- Confirm the close button, search box, row list, and detail actions remain reachable.

**Step 5: Commit**

```powershell
git add src/NovaTerminal.App/MainWindow.axaml src/NovaTerminal.App/Controls/ConnectionManager.axaml tests/NovaTerminal.Tests/Ssh/ConnectionManagerTests.cs
git commit -m "fix: make connection manager overlay responsive"
```

### Task 6: Run full verification and clean up

**Files:**
- Verify only

**Step 1: Run focused automated tests**

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "ConnectionManagerTests|SshManagerViewModelTests|SettingsWindow"
```

Expected: pass.

**Step 2: Run full solution build**

```powershell
dotnet build
```

Expected: pass with no new warnings or errors caused by the fixes.

**Step 3: Run one manual UX pass**

Check:
- Settings opens to Appearance and Profiles correctly from `OpenSettings(0)` and `OpenSettings(1)`.
- Connection Manager search, group filters, favorite toggles, copy command, edit, details dialog, and open actions all work.
- Theme switching still updates the refreshed UIs.
- Small-window overlay remains usable.

**Step 4: Final commit**

```powershell
git add .
git commit -m "fix: resolve settings and connection manager review regressions"
```
