# Shortcut Management And Command Palette Ranking Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a Settings-based shortcut management surface for app, pane-local, and Command Assist actions, and make the command palette open-state list reflect persisted most-used commands.

**Architecture:** Keep all shortcut definition, resolution, validation, and command-palette usage ranking in the app layer. Leave `MainWindow`, `TerminalPane`, and Command Assist responsible for execution, but route them through shared shortcut metadata and persisted usage tracking so Settings and the palette read from the same source of truth.

**Tech Stack:** C#, Avalonia UI, xUnit with Avalonia headless tests, `System.Text.Json`

---

### Task 1: Create shared shortcut domain types and duplicate validation

**Files:**
- Create: `src/NovaTerminal.App/Core/Shortcuts/ShortcutScope.cs`
- Create: `src/NovaTerminal.App/Core/Shortcuts/ShortcutDefinition.cs`
- Create: `src/NovaTerminal.App/Core/Shortcuts/ShortcutBindingRecord.cs`
- Create: `src/NovaTerminal.App/Core/Shortcuts/ShortcutNormalizer.cs`
- Create: `src/NovaTerminal.App/Core/Shortcuts/ShortcutBindingResolver.cs`
- Test: `tests/NovaTerminal.Tests/Core/ShortcutBindingResolverTests.cs`

**Step 1: Write the failing test**

```csharp
using NovaTerminal.Core.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class ShortcutBindingResolverTests
{
    [Fact]
    public void ResolveBindings_UsesOverridesAndRejectsDuplicatesAcrossScopes()
    {
        var definitions = new[]
        {
            new ShortcutDefinition("settings", "Settings", "General", ShortcutScope.App, "Ctrl+,"),
            new ShortcutDefinition("command_assist_help", "Command Assist Help", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+Shift+H")
        };

        var overrides = new Dictionary<string, string>
        {
            ["settings"] = "Ctrl+Alt+S",
            ["command_assist_help"] = "Ctrl+Alt+S"
        };

        var result = ShortcutBindingResolver.Resolve(definitions, overrides);

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, conflict => conflict.Shortcut == "Ctrl+Alt+S");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~ShortcutBindingResolverTests`

Expected: FAIL because the shortcut domain classes and resolver do not exist yet.

**Step 3: Write minimal implementation**

```csharp
namespace NovaTerminal.Core.Shortcuts;

public enum ShortcutScope
{
    App,
    Pane,
    CommandAssist
}

public sealed record ShortcutDefinition(
    string Id,
    string Title,
    string Category,
    ShortcutScope Scope,
    string DefaultShortcut);

public sealed record ShortcutBindingRecord(
    ShortcutDefinition Definition,
    string EffectiveShortcut);

public sealed record ShortcutConflict(
    string Shortcut,
    IReadOnlyList<ShortcutBindingRecord> Bindings);

public sealed record ShortcutBindingResolution(
    IReadOnlyList<ShortcutBindingRecord> Bindings,
    IReadOnlyList<ShortcutConflict> Conflicts)
{
    public bool IsValid => Conflicts.Count == 0;
}

public static class ShortcutBindingResolver
{
    public static ShortcutBindingResolution Resolve(
        IEnumerable<ShortcutDefinition> definitions,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var bindings = definitions
            .Select(definition =>
            {
                string raw = overrides != null && overrides.TryGetValue(definition.Id, out var custom) && !string.IsNullOrWhiteSpace(custom)
                    ? custom
                    : definition.DefaultShortcut;

                return new ShortcutBindingRecord(definition, ShortcutNormalizer.Normalize(raw));
            })
            .ToList();

        var conflicts = bindings
            .Where(binding => !string.IsNullOrWhiteSpace(binding.EffectiveShortcut))
            .GroupBy(binding => binding.EffectiveShortcut, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new ShortcutConflict(group.Key, group.ToList()))
            .ToList();

        return new ShortcutBindingResolution(bindings, conflicts);
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~ShortcutBindingResolverTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/Shortcuts tests/NovaTerminal.Tests/Core/ShortcutBindingResolverTests.cs
git commit -m "feat: add shared shortcut binding resolver"
```

### Task 2: Define the bindable command catalog for app, pane, and Command Assist actions

**Files:**
- Create: `src/NovaTerminal.App/Core/Shortcuts/ShortcutCatalog.cs`
- Modify: `src/NovaTerminal.App/Core/CommandRegistry.cs:5-39`
- Test: `tests/NovaTerminal.Tests/Core/ShortcutCatalogTests.cs`

**Step 1: Write the failing test**

```csharp
using NovaTerminal.Core.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class ShortcutCatalogTests
{
    [Fact]
    public void GetDefinitions_IncludesSettingsAndCommandAssistCommands()
    {
        var definitions = ShortcutCatalog.GetDefinitions();

        Assert.Contains(definitions, d => d.Id == "settings" && d.Scope == ShortcutScope.App);
        Assert.Contains(definitions, d => d.Id == "command_assist_toggle" && d.Scope == ShortcutScope.CommandAssist);
        Assert.Contains(definitions, d => d.Id == "find" && d.Scope == ShortcutScope.Pane);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~ShortcutCatalogTests`

Expected: FAIL because the catalog does not exist and `CommandRegistry` does not carry stable ids.

**Step 3: Write minimal implementation**

```csharp
namespace NovaTerminal.Core.Shortcuts;

public static class ShortcutCatalog
{
    private static readonly IReadOnlyList<ShortcutDefinition> Definitions =
    [
        new("command_palette", "Command Palette", "General", ShortcutScope.App, "Ctrl+Shift+P"),
        new("settings", "Settings", "General", ShortcutScope.App, "Ctrl+,"),
        new("new_tab", "New Tab", "General", ShortcutScope.App, "Ctrl+Shift+T"),
        new("close_tab", "Close Tab", "General", ShortcutScope.App, "Ctrl+W"),
        new("close_pane", "Close Pane", "General", ShortcutScope.Pane, "Ctrl+Shift+W"),
        new("find", "Find in Terminal", "Edit", ShortcutScope.Pane, "Ctrl+Shift+F"),
        new("command_assist_toggle", "Command Assist Toggle", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+Space"),
        new("command_assist_help", "Command Assist Help", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+Shift+H"),
        new("command_assist_history", "Command Assist History", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+R")
    ];

    public static IReadOnlyList<ShortcutDefinition> GetDefinitions() => Definitions;
}
```

Extend `TerminalCommand` to include shortcut metadata needed by the palette:

```csharp
public class TerminalCommand
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "General";
    public string Shortcut { get; set; } = "";
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~ShortcutCatalogTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/Shortcuts/ShortcutCatalog.cs src/NovaTerminal.App/Core/CommandRegistry.cs tests/NovaTerminal.Tests/Core/ShortcutCatalogTests.cs
git commit -m "feat: add bindable shortcut catalog"
```

### Task 3: Add persisted command palette usage storage

**Files:**
- Create: `src/NovaTerminal.App/Core/Shortcuts/CommandPaletteUsageEntry.cs`
- Create: `src/NovaTerminal.App/Core/Shortcuts/CommandPaletteUsageStore.cs`
- Modify: `src/NovaTerminal.App/Core/AppPaths.cs`
- Modify: `src/NovaTerminal.App/Core/AppJsonContext.cs`
- Test: `tests/NovaTerminal.Tests/Core/CommandPaletteUsageStoreTests.cs`

**Step 1: Write the failing test**

```csharp
using NovaTerminal.Core.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class CommandPaletteUsageStoreTests
{
    [Fact]
    public void RecordUse_PersistsCountAcrossReloads()
    {
        using var tempRoot = new TempAppDataRoot();
        var path = Path.Combine(tempRoot.RootPath, "command-palette-usage.json");

        var store = new CommandPaletteUsageStore(path);
        store.RecordUse("settings", new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        store.Save();

        var reloaded = new CommandPaletteUsageStore(path);
        var snapshot = reloaded.Load();

        Assert.Equal(1, snapshot["settings"].UseCount);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~CommandPaletteUsageStoreTests`

Expected: FAIL because no usage store or app path exists yet.

**Step 3: Write minimal implementation**

```csharp
public static class AppPaths
{
    public static string CommandPaletteUsageFilePath => Path.Combine(RootDirectory, "command-palette-usage.json");
}

public sealed record CommandPaletteUsageEntry(string CommandId, int UseCount, DateTimeOffset LastUsedAt);

public sealed class CommandPaletteUsageStore
{
    private readonly string _path;

    public CommandPaletteUsageStore(string path)
    {
        _path = path;
    }

    public Dictionary<string, CommandPaletteUsageEntry> Load() { /* deserialize or return empty */ }
    public void RecordUse(string commandId, DateTimeOffset usedAt) { /* increment count */ }
    public void Save() { /* serialize */ }
}
```

Also add JSON source-generation coverage for the usage entry dictionary in `AppJsonContext.cs`.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~CommandPaletteUsageStoreTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/AppPaths.cs src/NovaTerminal.App/Core/AppJsonContext.cs src/NovaTerminal.App/Core/Shortcuts tests/NovaTerminal.Tests/Core/CommandPaletteUsageStoreTests.cs
git commit -m "feat: persist command palette usage"
```

### Task 4: Route runtime shortcut handling through the shared catalog and add Settings shortcut support

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs:200-280`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs:2171-2259`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs:3972-4004`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs:1305-1363`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs:1808-1828`
- Test: `tests/NovaTerminal.Tests/Core/MainWindowStartupTests.cs`
- Create: `tests/NovaTerminal.Tests/Core/MainWindowShortcutRoutingTests.cs`

**Step 1: Write the failing test**

```csharp
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class MainWindowShortcutRoutingTests
{
    [AvaloniaFact]
    public async Task SettingsShortcut_UsesCatalogBindingAndOpensSettings()
    {
        var window = new SettingsProbeWindow();

        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.OemComma,
            KeyModifiers = KeyModifiers.Control,
            Source = window
        });

        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.True(window.WasOpenSettingsInvoked);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~MainWindowShortcutRoutingTests`

Expected: FAIL because `Settings` has no default shortcut and routing still uses hardcoded fallback strings.

**Step 3: Write minimal implementation**

Key changes:

- add a helper that resolves the effective shortcut from `ShortcutCatalog` + `_settings.Keybindings`
- replace hardcoded fallback strings in `MainWindow` key handling with catalog ids
- keep `TerminalPane` execution local, but expose its actions through catalog ids used by the shared resolver
- register `Settings` in the palette with a real default binding

Example integration shape:

```csharp
private bool IsShortcut(KeyEventArgs e, string id)
{
    string? binding = _shortcutBindings.GetEffectiveShortcut(id);
    return ShortcutNormalizer.Matches(e, binding);
}

if (IsShortcut(e, "settings"))
{
    _ = OpenSettings(0);
    e.Handled = true;
    return;
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~MainWindowShortcutRoutingTests|FullyQualifiedName~MainWindowStartupTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/Core/MainWindowStartupTests.cs tests/NovaTerminal.Tests/Core/MainWindowShortcutRoutingTests.cs
git commit -m "feat: route shortcuts through shared catalog"
```

### Task 5: Rank command palette open-state results by persisted usage

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs:4292-4384`
- Modify: `src/NovaTerminal.App/Core/CommandRegistry.cs:17-39`
- Create: `src/NovaTerminal.App/Core/Shortcuts/CommandPaletteOrdering.cs`
- Test: `tests/NovaTerminal.Tests/Core/CommandPaletteOrderingTests.cs`
- Modify: `tests/NovaTerminal.Tests/Core/MainWindowStartupTests.cs`

**Step 1: Write the failing test**

```csharp
using NovaTerminal.Core;
using NovaTerminal.Core.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class CommandPaletteOrderingTests
{
    [Fact]
    public void OrderEmptyQuery_PrefersMostUsedCommands()
    {
        var commands = new[]
        {
            new TerminalCommand { Id = "settings", Title = "Settings", Category = "General" },
            new TerminalCommand { Id = "open_recording", Title = "Open Recording...", Category = "General" }
        };

        var usage = new Dictionary<string, CommandPaletteUsageEntry>
        {
            ["settings"] = new("settings", 8, DateTimeOffset.UtcNow),
            ["open_recording"] = new("open_recording", 1, DateTimeOffset.UtcNow)
        };

        var ordered = CommandPaletteOrdering.OrderForEmptyQuery(commands, usage);

        Assert.Equal("settings", ordered[0].Id);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~CommandPaletteOrderingTests`

Expected: FAIL because the ordering helper does not exist and palette open state still sorts alphabetically.

**Step 3: Write minimal implementation**

```csharp
public static class CommandPaletteOrdering
{
    public static List<TerminalCommand> OrderForEmptyQuery(
        IEnumerable<TerminalCommand> commands,
        IReadOnlyDictionary<string, CommandPaletteUsageEntry> usage)
    {
        return commands
            .OrderByDescending(command => usage.TryGetValue(command.Id, out var entry) ? entry.UseCount : 0)
            .ThenByDescending(command => usage.TryGetValue(command.Id, out var entry) ? entry.LastUsedAt : DateTimeOffset.MinValue)
            .ThenBy(command => command.Category)
            .ThenBy(command => command.Title)
            .ToList();
    }
}
```

Update `ToggleCommandPalette` and search refresh logic so:

- empty query uses `OrderForEmptyQuery(...)`
- non-empty query preserves search relevance and uses usage only as a tie-breaker
- executing a command records usage before or immediately after action dispatch

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~CommandPaletteOrderingTests|FullyQualifiedName~MainWindowStartupTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/Core/CommandRegistry.cs src/NovaTerminal.App/Core/Shortcuts/CommandPaletteOrdering.cs tests/NovaTerminal.Tests/Core/CommandPaletteOrderingTests.cs tests/NovaTerminal.Tests/Core/MainWindowStartupTests.cs
git commit -m "feat: rank command palette by persisted usage"
```

### Task 6: Add the Shortcuts tab to Settings with inline validation and save/reset behavior

**Files:**
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml:283-324`
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml:584-759`
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml.cs:1113-1375`
- Create: `src/NovaTerminal.App/Core/Shortcuts/ShortcutEditorRow.cs`
- Create: `src/NovaTerminal.App/Core/Shortcuts/ShortcutSettingsState.cs`
- Create: `tests/NovaTerminal.Tests/Core/SettingsWindowShortcutTests.cs`

**Step 1: Write the failing test**

```csharp
using Avalonia.Headless.XUnit;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class SettingsWindowShortcutTests
{
    [AvaloniaFact]
    public void SaveAndClose_PersistsShortcutOverrides_WhenBindingsAreUnique()
    {
        using var tempRoot = new TempAppDataRoot();
        var window = new NovaTerminal.SettingsWindow();

        window.ApplyShortcutOverrideForTest("settings", "Ctrl+Alt+S");
        window.InvokeSaveAndCloseForTest();

        var reloaded = TerminalSettings.Load();
        Assert.Equal("Ctrl+Alt+S", reloaded.Keybindings["settings"]);
    }

    [AvaloniaFact]
    public void SaveAndClose_DoesNotPersistDuplicateShortcut()
    {
        using var tempRoot = new TempAppDataRoot();
        var window = new NovaTerminal.SettingsWindow();

        window.ApplyShortcutOverrideForTest("settings", "Ctrl+Shift+P");
        window.ApplyShortcutOverrideForTest("command_palette", "Ctrl+Shift+P");

        Assert.False(window.CanSaveShortcutsForTest());
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~SettingsWindowShortcutTests`

Expected: FAIL because the `Shortcuts` tab, editor state, and validation hooks do not exist.

**Step 3: Write minimal implementation**

UI and state changes:

- add a `Shortcuts` item to the side nav and a matching `TabItem`
- bind the tab to a collection of editable shortcut rows
- add capture/edit/reset controls and inline error text
- validate the full binding set before save
- write successful overrides back to `_settings.Keybindings`

Suggested state shape:

```csharp
public sealed class ShortcutEditorRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public string Scope { get; init; } = "";
    public string DefaultShortcut { get; init; } = "";
    public string EffectiveShortcut { get; set; } = "";
    public string? ErrorText { get; set; }
}

public sealed class ShortcutSettingsState
{
    public List<ShortcutEditorRow> Rows { get; } = [];

    public bool TryApplyOverride(string id, string shortcut, out string? error) { /* validate unique binding set */ }
    public Dictionary<string, string> BuildOverrides() { /* save only non-default overrides */ }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~SettingsWindowShortcutTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/SettingsWindow.axaml src/NovaTerminal.App/SettingsWindow.axaml.cs src/NovaTerminal.App/Core/Shortcuts/ShortcutEditorRow.cs src/NovaTerminal.App/Core/Shortcuts/ShortcutSettingsState.cs tests/NovaTerminal.Tests/Core/SettingsWindowShortcutTests.cs
git commit -m "feat: add shortcut management to settings"
```

### Task 7: Run focused regression verification and update user-facing docs

**Files:**
- Modify: `docs/USER_MANUAL.md`
- Modify: `docs/PRODUCTION_ROADMAP.md`

**Step 1: Write the failing documentation/test checklist**

Document expected behaviors to verify manually:

```text
1. Press default Settings shortcut and confirm Settings opens.
2. Change a shortcut in Settings, save, restart, and confirm it persists.
3. Try assigning a duplicate shortcut and confirm save is blocked.
4. Open command palette with empty query after repeated command usage and confirm ordering changes.
5. Confirm Command Assist shortcuts still work and hide correctly in alt-screen mode.
```

**Step 2: Run focused automated verification**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~ShortcutBindingResolverTests|FullyQualifiedName~ShortcutCatalogTests|FullyQualifiedName~CommandPaletteUsageStoreTests|FullyQualifiedName~MainWindowShortcutRoutingTests|FullyQualifiedName~CommandPaletteOrderingTests|FullyQualifiedName~SettingsWindowShortcutTests|FullyQualifiedName~MainWindowStartupTests"`

Expected: PASS

**Step 3: Update minimal docs**

Add short user-facing notes for:

- how to open Settings with the new default shortcut
- where shortcut customization lives
- that palette ordering reflects persisted usage

**Step 4: Re-run the focused automated verification**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~ShortcutBindingResolverTests|FullyQualifiedName~ShortcutCatalogTests|FullyQualifiedName~CommandPaletteUsageStoreTests|FullyQualifiedName~MainWindowShortcutRoutingTests|FullyQualifiedName~CommandPaletteOrderingTests|FullyQualifiedName~SettingsWindowShortcutTests|FullyQualifiedName~MainWindowStartupTests"`

Expected: PASS

**Step 5: Commit**

```bash
git add docs/USER_MANUAL.md docs/PRODUCTION_ROADMAP.md
git commit -m "docs: describe shortcut management and palette ranking"
```
