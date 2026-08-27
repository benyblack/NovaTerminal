# Connection Manager Delete + Forget Saved Password Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user delete an SSH connection from the connection manager (with confirmation, purging its saved password) and see/remove a saved password for the selected connection.

**Architecture:** Three layers, bottom-up. (1) `VaultService` gains a new `ISavedPasswordAccess` interface — probe and purge over *profile-scoped* vault keys only. (2) `ConnectionManager` (an Avalonia `UserControl`) gains a Danger trash button that raises a new event, and a "Saved password" detail row driven by an injected `ISavedPasswordAccess`. (3) `MainWindow` handles the delete event: themed confirm dialog, store delete, vault purge, `RefreshProfileUIs()`. The control never touches the profile store; it only raises events — matching the existing convention for Edit/Copy/Details.

**Tech Stack:** .NET 10, C#, Avalonia (XAML + imperative code-behind, no MVVM bindings in this control's detail panel), xUnit + `Avalonia.Headless.XUnit` (`[AvaloniaFact]`).

## Global Constraints

- **Build only via wrapper scripts.** `scripts/build.ps1 <args>` (PowerShell) or `scripts/build.sh <args>` (bash). Never raw `dotnet build` — it leaks MSBuild daemons that inherit stdout/stderr and hang the calling tool.
- **Run tests targeted, never solution-wide.** A full-solution `dotnet test` is ~20–30 min (headless Avalonia). Use `scripts/build.ps1 test tests/NovaTerminal.App.Tests` and, where possible, `--filter`.
- Existing `ISshPasswordVault` in `VaultService.cs` must **not** gain members — `tests/NovaTerminal.App.Tests/Ssh/SshConnectionServiceTests.cs:826` has a `RecordingSshPasswordVault` implementing it, and widening the interface would break it. Add a **separate** interface.
- Vault key sets are already defined in `VaultService`. Use `GetProfileScopedSshPasswordKeysForProfile` (canonical + per-profile legacy, **excludes** the shared `SSH:{user}@{host}` alias). Never use `GetSshPasswordKeysForProfile` for either probe or purge.
- Never probe the vault via `ResolveSshPasswordForProfile` — it migrates legacy keys to canonical, i.e. it *writes*.
- The `Button.Danger` style and the `NtRed` brush already exist in `ConnectionManager.axaml`. Do not add new styles or palette keys.
- Test project has `<Using Include="Xunit" />`, so **no** `using Xunit;` line in test files.
- Branch: `feat/connection-manager-delete-and-forget-password` (already created off `origin/main`, spec commit `2615372` on it).

---

## File Structure

**Modified:**

- `src/NovaTerminal.App/Shell/VaultService.cs` — add `ISavedPasswordAccess` interface + three `VaultService` members. Currently 219 lines; grows ~45. Stays focused (it is exactly this file's responsibility).
- `src/NovaTerminal.App/Controls/ConnectionManager.axaml` — one column + button in the action bar (line 522); one row in the CONNECTION grid (line 566).
- `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs` — one event, one property, three handlers/renderers. Currently 654 lines; grows ~60.
- `src/NovaTerminal.App/MainWindow.axaml.cs` — two wiring lines in `EnsureConnectionManagerControl()` (line ~5166) plus two new private methods. Already ~5900 lines; this follows the file's established pattern and is not the place to restructure.

**Test files:**

- `tests/NovaTerminal.App.Tests/Core/VaultServiceSavedPasswordTests.cs` — **new**. Vault-layer behaviour. Kept separate from `VaultServiceDisabledModeTests.cs` so the saved-password contract reads as one unit.
- `tests/NovaTerminal.App.Tests/Ssh/ConnectionManagerTests.cs` — extended. Control-layer behaviour, reusing the file's existing `CreateMeasuredConnectionManager` / `SelectFirstRow` / `FindButtonByToolTip` / `CreateSshProfile` helpers.

**Task order is bottom-up so each task's tests can pass on their own:** Task 1 (vault) → Task 2 (delete button + event) → Task 3 (saved-password row) → Task 4 (MainWindow wiring).

---

### Task 1: `ISavedPasswordAccess` on `VaultService`

**Files:**
- Modify: `src/NovaTerminal.App/Shell/VaultService.cs` (add interface after `ISshPasswordVault` at line 11; add members to `VaultService`)
- Test: `tests/NovaTerminal.App.Tests/Core/VaultServiceSavedPasswordTests.cs` (create)

**Interfaces:**
- Consumes: existing `VaultService.GetProfileScopedSshPasswordKeysForProfile(TerminalProfile)`, `GetSecret(string)`, `RemoveSecret(string)`, `PersistenceAvailable`, `GetCanonicalSshProfileKey(Guid)`, `GetLegacySshKeys(TerminalProfile, bool)`; `NovaTerminal.Shell.Secrets.InMemorySecretStore`.
- Produces: `NovaTerminal.Shell.ISavedPasswordAccess` with `bool IsVaultAvailable { get; }`, `bool HasSavedPassword(TerminalProfile profile)`, `bool ForgetSavedPassword(TerminalProfile profile)`. Implemented by `VaultService`. Tasks 3 and 4 depend on these exact names.

- [ ] **Step 1: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/Core/VaultServiceSavedPasswordTests.cs`:

```csharp
using System;
using NovaTerminal.Shell;
using NovaTerminal.Shell.Secrets;

namespace NovaTerminal.Tests.Core;

// Note: both TerminalProfile and ConnectionType live in NovaTerminal.Shell
// (src/NovaTerminal.App/Shell/TerminalProfile.cs). Do not add a
// `using NovaTerminal.Platform;` — it is not needed here.

public class VaultServiceSavedPasswordTests
{
    private sealed class UnavailableStore : ISecretStore
    {
        public bool IsAvailable => false;
        public string? Read(string key) => "should-not-be-read";
        public void Write(string key, string value) => throw new InvalidOperationException("must not write");
        public bool Delete(string key) => throw new InvalidOperationException("must not delete");
    }

    private static TerminalProfile CreateProfile(string name = "Prod")
    {
        return new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Name = name,
            SshHost = "prod.internal",
            SshUser = "ops"
        };
    }

    [Fact]
    public void IsVaultAvailable_ReflectsStore()
    {
        Assert.True(new VaultService(new InMemorySecretStore()).IsVaultAvailable);
        Assert.False(new VaultService(new UnavailableStore()).IsVaultAvailable);
    }

    [Fact]
    public void HasSavedPassword_IsFalse_WhenNothingStored()
    {
        var vault = new VaultService(new InMemorySecretStore());
        Assert.False(vault.HasSavedPassword(CreateProfile()));
    }

    [Fact]
    public void HasSavedPassword_IsTrue_ForCanonicalKey()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();

        store.Write(VaultService.GetCanonicalSshProfileKey(profile.Id), "secret");

        Assert.True(vault.HasSavedPassword(profile));
    }

    [Fact]
    public void HasSavedPassword_IsTrue_ForPerProfileLegacyKey()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();

        store.Write($"profile_{profile.Id}_password", "secret");

        Assert.True(vault.HasSavedPassword(profile));
    }

    [Fact]
    public void HasSavedPassword_IsFalse_ForSharedAliasOnly()
    {
        // The shared SSH:{user}@{host} alias may be resolved by sibling profiles
        // on the same host, so it is deliberately outside this profile's scope.
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();

        store.Write("SSH:ops@prod.internal", "secret");

        Assert.False(vault.HasSavedPassword(profile));
    }

    [Fact]
    public void HasSavedPassword_DoesNotWriteToStore()
    {
        // Must not go through ResolveSshPasswordForProfile, which migrates a
        // legacy hit to the canonical key as a side effect.
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();
        string legacyKey = $"profile_{profile.Id}_password";

        store.Write(legacyKey, "secret");
        Assert.True(vault.HasSavedPassword(profile));

        Assert.Null(store.Read(VaultService.GetCanonicalSshProfileKey(profile.Id)));
        Assert.Equal("secret", store.Read(legacyKey));
    }

    [Fact]
    public void ForgetSavedPassword_ClearsCanonicalAndPerProfileLegacyKeys()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();
        string canonicalKey = VaultService.GetCanonicalSshProfileKey(profile.Id);
        string legacyIdKey = $"profile_{profile.Id}_password";
        string legacyNamedKey = "SSH:Prod:ops@prod.internal";

        store.Write(canonicalKey, "a");
        store.Write(legacyIdKey, "b");
        store.Write(legacyNamedKey, "c");

        Assert.True(vault.ForgetSavedPassword(profile));

        Assert.Null(store.Read(canonicalKey));
        Assert.Null(store.Read(legacyIdKey));
        Assert.Null(store.Read(legacyNamedKey));
        Assert.False(vault.HasSavedPassword(profile));
    }

    [Fact]
    public void ForgetSavedPassword_LeavesSharedAliasIntact()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();

        store.Write(VaultService.GetCanonicalSshProfileKey(profile.Id), "mine");
        store.Write("SSH:ops@prod.internal", "shared");

        Assert.True(vault.ForgetSavedPassword(profile));

        Assert.Equal("shared", store.Read("SSH:ops@prod.internal"));
    }

    [Fact]
    public void ForgetSavedPassword_ReturnsFalse_WhenNothingStored()
    {
        var vault = new VaultService(new InMemorySecretStore());
        Assert.False(vault.ForgetSavedPassword(CreateProfile()));
    }

    [Fact]
    public void SavedPasswordMembers_WhenVaultUnavailable_AreSafeNoOps()
    {
        // UnavailableStore throws on Read/Write/Delete; the IsAvailable guards in
        // GetSecret/RemoveSecret must short-circuit before touching it.
        var vault = new VaultService(new UnavailableStore());
        TerminalProfile profile = CreateProfile();

        Assert.False(vault.IsVaultAvailable);
        Assert.False(vault.HasSavedPassword(profile));
        Assert.False(vault.ForgetSavedPassword(profile));
    }

    [Fact]
    public void ForgetSavedPassword_Throws_OnNullProfile()
    {
        var vault = new VaultService(new InMemorySecretStore());
        Assert.Throws<ArgumentNullException>(() => vault.ForgetSavedPassword(null!));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter FullyQualifiedName~VaultServiceSavedPasswordTests
```

Expected: compile FAIL — `'VaultService' does not contain a definition for 'IsVaultAvailable'` / `'HasSavedPassword'` / `'ForgetSavedPassword'`.

- [ ] **Step 3: Add the interface**

In `src/NovaTerminal.App/Shell/VaultService.cs`, immediately after the closing brace of `ISshPasswordVault` (line 11):

```csharp
    /// <summary>
    /// Read/clear access to the password a profile has saved in the OS credential store.
    /// </summary>
    /// <remarks>
    /// Both members operate on <em>profile-scoped</em> keys only — the canonical
    /// <c>SSH:PROFILE:{guid}</c> key plus this profile's own legacy keys. The shared
    /// <c>SSH:{user}@{host}</c> alias is excluded on purpose: sibling profiles on the same
    /// host resolve through it, so clearing it here would silently break them.
    ///
    /// Consequence: a password stored <em>only</em> under that shared alias reports
    /// <see cref="HasSavedPassword"/> as <see langword="false"/> even though
    /// <see cref="VaultService.ResolveSshPasswordForProfile"/> will still auto-fill from it at
    /// connect time. Such a secret migrates to a profile-scoped key on first use, after which
    /// this reports correctly. That is a legacy-store artifact, and preferable to letting one
    /// profile delete another's password.
    /// </remarks>
    public interface ISavedPasswordAccess
    {
        /// <summary>True when the underlying credential store can be read and written.</summary>
        bool IsVaultAvailable { get; }

        /// <summary>True when a profile-scoped password is stored for <paramref name="profile"/>.</summary>
        bool HasSavedPassword(TerminalProfile profile);

        /// <summary>Clears every profile-scoped password key; true if anything was removed.</summary>
        bool ForgetSavedPassword(TerminalProfile profile);
    }
```

- [ ] **Step 4: Implement on `VaultService`**

Change the class declaration:

```csharp
    public class VaultService
        : ISshPasswordVault, ISavedPasswordAccess
```

Add these members next to `PersistenceAvailable` (line 27):

```csharp
        public bool IsVaultAvailable => _store.IsAvailable;

        public bool HasSavedPassword(TerminalProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            // GetSecret, not ResolveSshPasswordForProfile: the latter migrates a legacy hit
            // to the canonical key, and this runs on every selection change in the UI.
            foreach (string key in GetProfileScopedSshPasswordKeysForProfile(profile))
            {
                if (!string.IsNullOrEmpty(GetSecret(key)))
                {
                    return true;
                }
            }

            return false;
        }

        public bool ForgetSavedPassword(TerminalProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            bool removedAny = false;
            foreach (string key in GetProfileScopedSshPasswordKeysForProfile(profile))
            {
                removedAny |= RemoveSecret(key);
            }

            return removedAny;
        }
```

`System.Linq` is not needed — the loops are explicit. `System` and `System.Collections.Generic` are already imported.

- [ ] **Step 5: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter FullyQualifiedName~VaultServiceSavedPasswordTests
```

Expected: PASS, 11 tests.

- [ ] **Step 6: Re-run the existing vault tests (no regression)**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter FullyQualifiedName~VaultService
```

Expected: PASS. `VaultServiceDisabledModeTests` must be unaffected.

- [ ] **Step 7: Commit**

```bash
git add src/NovaTerminal.App/Shell/VaultService.cs tests/NovaTerminal.App.Tests/Core/VaultServiceSavedPasswordTests.cs
git commit -m "feat(vault): ISavedPasswordAccess to probe and clear a profile's saved password"
```

---

### Task 2: Delete button raises `OnDeleteProfileRequested`

**Files:**
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml:522` (action bar `Grid`)
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs` (event near line 34; handler near `OnDeleteClick` peers at line 462–487)
- Test: `tests/NovaTerminal.App.Tests/Ssh/ConnectionManagerTests.cs`

**Interfaces:**
- Consumes: existing `ConnectionManager.TryGetRow(object?, out SshProfileRowViewModel)`, `SshProfileRowViewModel.Profile`.
- Produces: `public event Action<TerminalProfile>? OnDeleteProfileRequested;` on `ConnectionManager`. Task 4 subscribes to it. Button tooltip is exactly `"Delete connection"`.

- [ ] **Step 1: Write the failing test**

In `tests/NovaTerminal.App.Tests/Ssh/ConnectionManagerTests.cs`, add after `DetailsAction_RaisesConnectionDetailsRequested_ForSelectedRow` (ends ~line 88):

```csharp
    [AvaloniaFact]
    public void DeleteAction_RaisesDeleteProfileRequested_ForSelectedRow()
    {
        var control = CreateMeasuredConnectionManager();
        TerminalProfile profile = CreateSshProfile("Prod", favorite: false);
        control.LoadProfiles(new[] { profile });
        SelectFirstRow(control);

        TerminalProfile? receivedProfile = null;
        control.OnDeleteProfileRequested += p => receivedProfile = p;

        var deleteButton = FindButtonByToolTip(control, "Delete connection");
        Assert.NotNull(deleteButton);

        deleteButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Same(profile, receivedProfile);
    }

    [AvaloniaFact]
    public void DeleteAction_DoesNotRaise_WhenNoRowSelected()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });

        bool raised = false;
        control.OnDeleteProfileRequested += _ => raised = true;

        var deleteButton = FindButtonByToolTip(control, "Delete connection");
        Assert.NotNull(deleteButton);

        deleteButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void DeleteAction_DoesNotRemoveRowItself()
    {
        // The control only raises; MainWindow owns the store delete and the refresh.
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);
        control.OnDeleteProfileRequested += _ => { };

        FindButtonByToolTip(control, "Delete connection")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, GetListItemCount(control));
        Assert.Single(control.GetAllProfiles());
    }
```

Also update the existing hit-target guard (line 36) so it covers the new button. Replace its `actionTips` array and count assertion:

```csharp
        string[] actionTips =
        {
            "Toggle favorite",
            "Edit connection",
            "Copy launch command",
            "Connection details",
            "Delete connection"
        };
```

and

```csharp
        Assert.Equal(5, actionButtons.Count);
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter FullyQualifiedName~ConnectionManagerTests
```

Expected: compile FAIL — `'ConnectionManager' does not contain a definition for 'OnDeleteProfileRequested'`.

- [ ] **Step 3: Add the XAML button**

In `src/NovaTerminal.App/Controls/ConnectionManager.axaml`, change line 522 to add an eleventh column:

```xml
                        <Grid Margin="0,10,0,0" ColumnDefinitions="Auto,Auto,Auto,Auto,*,Auto,Auto,Auto,Auto,Auto,Auto">
```

Then, immediately after the `Grid.Column="9"` "Connection details" button (closes ~line 555) and before the `</Grid>`, add:

```xml
                            <Button Grid.Column="10" Classes="IconBtn Danger" Click="OnDeleteClick" ToolTip.Tip="Delete connection">
                                <PathIcon Data="M9,3V4H4V6H5V19A2,2 0 0,0 7,21H17A2,2 0 0,0 19,19V6H20V4H15V3H9M7,6H17V19H7V6M9,8V17H11V8H9M13,8V17H15V8H13Z" Width="14" Height="14"/>
                            </Button>
```

- [ ] **Step 4: Add the event and handler**

In `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs`, add to the event block (after line 34, `OnNewConnectionRequested`):

```csharp
        public event Action<TerminalProfile>? OnDeleteProfileRequested;
```

Add the handler after `OnDetailsClick` (ends line 487):

```csharp
        private void OnDeleteClick(object? sender, RoutedEventArgs e)
        {
            if (TryGetRow(sender, out var row))
            {
                e.Handled = true;
                OnDeleteProfileRequested?.Invoke(row.Profile);
            }
        }
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter FullyQualifiedName~ConnectionManagerTests
```

Expected: PASS. `SecondaryActionButtons_ReserveSquareHitTargets` now finds 5 buttons, all ≥30×30 (the `IconBtn` style sets `Width`/`Height` to 30; `Danger` only overrides `Foreground`).

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Controls/ConnectionManager.axaml src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs tests/NovaTerminal.App.Tests/Ssh/ConnectionManagerTests.cs
git commit -m "feat(connections): delete action in the connection manager detail bar"
```

---

### Task 3: "Saved password" detail row + Forget button

**Files:**
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml:566-576` (CONNECTION grid)
- Modify: `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs` (property; `RenderDetail` at line 372; `RenderEmptyDetail` at line 364; new handler + renderer)
- Test: `tests/NovaTerminal.App.Tests/Ssh/ConnectionManagerTests.cs`

**Interfaces:**
- Consumes: `NovaTerminal.Shell.ISavedPasswordAccess` from Task 1; existing `SetText(string, string)`, `TryGetRow`, `RenderDetail`.
- Produces: `public ISavedPasswordAccess? SavedPasswordAccess { get; set; }` on `ConnectionManager`. Task 4 assigns `MainWindow.Vault` to it. Control names: `KvSavedPassword` (`TextBlock`), `BtnForgetSavedPassword` (`Button`).

- [ ] **Step 1: Write the failing tests**

In `tests/NovaTerminal.App.Tests/Ssh/ConnectionManagerTests.cs`, add a test double at the bottom of the class, just above the `FindButtonByToolTip` helper:

```csharp
    private sealed class FakeSavedPasswordAccess : NovaTerminal.Shell.ISavedPasswordAccess
    {
        public bool IsVaultAvailable { get; set; } = true;
        public bool Saved { get; set; }
        public int ForgetCallCount { get; private set; }
        public TerminalProfile? LastForgotten { get; private set; }

        public bool HasSavedPassword(TerminalProfile profile) => Saved;

        public bool ForgetSavedPassword(TerminalProfile profile)
        {
            ForgetCallCount++;
            LastForgotten = profile;
            bool had = Saved;
            Saved = false;
            return had;
        }
    }
```

And add these tests after the Task 2 tests:

```csharp
    [AvaloniaFact]
    public void SavedPasswordRow_ShowsYes_AndEnablesForget_WhenPasswordStored()
    {
        var control = CreateMeasuredConnectionManager();
        control.SavedPasswordAccess = new FakeSavedPasswordAccess { Saved = true };
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);

        Assert.Equal("Yes", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        var forget = FindControl<Button>(control, "BtnForgetSavedPassword");
        Assert.True(forget.IsVisible);
        Assert.True(forget.IsEnabled);
    }

    [AvaloniaFact]
    public void SavedPasswordRow_ShowsNo_AndDisablesForget_WhenNothingStored()
    {
        var control = CreateMeasuredConnectionManager();
        control.SavedPasswordAccess = new FakeSavedPasswordAccess { Saved = false };
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);

        Assert.Equal("No", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        Assert.False(FindControl<Button>(control, "BtnForgetSavedPassword").IsEnabled);
    }

    [AvaloniaFact]
    public void SavedPasswordRow_ShowsVaultUnavailable_WhenStoreIsUnavailable()
    {
        var control = CreateMeasuredConnectionManager();
        control.SavedPasswordAccess = new FakeSavedPasswordAccess { IsVaultAvailable = false, Saved = true };
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);

        Assert.Equal("Vault unavailable", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        Assert.False(FindControl<Button>(control, "BtnForgetSavedPassword").IsEnabled);
    }

    [AvaloniaFact]
    public void SavedPasswordRow_HidesForget_WhenNoAccessorInjected()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);

        Assert.Equal("—", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        Assert.False(FindControl<Button>(control, "BtnForgetSavedPassword").IsVisible);
    }

    [AvaloniaFact]
    public void ForgetSavedPassword_FlipsRowToNo_DisablesButton_AndKeepsSelection()
    {
        var control = CreateMeasuredConnectionManager();
        var access = new FakeSavedPasswordAccess { Saved = true };
        control.SavedPasswordAccess = access;
        TerminalProfile profile = CreateSshProfile("Prod", favorite: false);
        control.LoadProfiles(new[] { profile });
        SelectFirstRow(control);

        var forget = FindControl<Button>(control, "BtnForgetSavedPassword");
        forget.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, access.ForgetCallCount);
        Assert.Same(profile, access.LastForgotten);
        Assert.Equal("No", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        Assert.False(forget.IsEnabled);

        // No LoadProfiles reload — the selection must survive.
        var list = FindControl<ListBox>(control, "ConnectionsList");
        Assert.Equal(0, list.SelectedIndex);
    }

    [AvaloniaFact]
    public void ForgetSavedPassword_DoesNothing_WhenNoRowSelected()
    {
        var control = CreateMeasuredConnectionManager();
        var access = new FakeSavedPasswordAccess { Saved = true };
        control.SavedPasswordAccess = access;
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });

        FindControl<Button>(control, "BtnForgetSavedPassword")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(0, access.ForgetCallCount);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter FullyQualifiedName~ConnectionManagerTests
```

Expected: compile FAIL — `'ConnectionManager' does not contain a definition for 'SavedPasswordAccess'`.

- [ ] **Step 3: Add the XAML row**

In `src/NovaTerminal.App/Controls/ConnectionManager.axaml`, change line 566 to add a sixth row:

```xml
                            <Grid ColumnDefinitions="130,*" RowDefinitions="Auto,Auto,Auto,Auto,Auto,Auto" Margin="0,0,0,0">
```

Then, immediately after the `KvAuth` `TextBlock` (line 576) and before the closing `</Grid>`, add:

```xml
                                <TextBlock Grid.Row="5" Grid.Column="0" Text="Saved password" Foreground="{StaticResource NtFg3}" FontSize="13" Margin="0,4"/>
                                <StackPanel Grid.Row="5" Grid.Column="1" Orientation="Horizontal" Spacing="10" Margin="0,4" VerticalAlignment="Center">
                                    <TextBlock Name="KvSavedPassword" Text="—" FontFamily="{StaticResource NtMono}" FontSize="12.5" Foreground="{StaticResource NtFg}" VerticalAlignment="Center"/>
                                    <Button Name="BtnForgetSavedPassword"
                                            Classes="Pill"
                                            Content="Forget saved password"
                                            Click="OnForgetSavedPasswordClick"
                                            ToolTip.Tip="Remove this connection's password from the OS credential store"/>
                                </StackPanel>
```

- [ ] **Step 4: Add the property, renderer and handler**

In `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs`:

Add `using NovaTerminal.Shell;` — already present at line 8, so no import change.

Add the property after the `CardBackground`/`SecondaryForeground` block (after line 120):

```csharp
        /// <summary>
        /// Vault access used to show and clear the selected connection's saved password.
        /// Left null in tests and design-time; the row then reads "—" and Forget is hidden.
        /// </summary>
        public ISavedPasswordAccess? SavedPasswordAccess { get; set; }
```

Add to the end of `RenderDetail` (line 372), just before `UpdateLaunchPreview();`:

```csharp
            RenderSavedPasswordState(row);
```

Add to `RenderEmptyDetail` (line 364) — the whole detail grid is hidden there, so nothing to reset; leave it unchanged.

Add these two methods after `DescribeAuth` (ends line 413):

```csharp
        private void RenderSavedPasswordState(SshProfileRowViewModel row)
        {
            var forgetButton = this.FindControl<Button>("BtnForgetSavedPassword");

            if (SavedPasswordAccess == null)
            {
                SetText("KvSavedPassword", "—");
                if (forgetButton != null)
                {
                    forgetButton.IsVisible = false;
                    forgetButton.IsEnabled = false;
                }
                return;
            }

            if (forgetButton != null)
            {
                forgetButton.IsVisible = true;
            }

            if (!SavedPasswordAccess.IsVaultAvailable)
            {
                SetText("KvSavedPassword", "Vault unavailable");
                if (forgetButton != null) forgetButton.IsEnabled = false;
                return;
            }

            bool saved = SavedPasswordAccess.HasSavedPassword(row.Profile);
            SetText("KvSavedPassword", saved ? "Yes" : "No");
            if (forgetButton != null) forgetButton.IsEnabled = saved;
        }

        private void OnForgetSavedPasswordClick(object? sender, RoutedEventArgs e)
        {
            if (SavedPasswordAccess == null || !TryGetRow(sender, out var row))
            {
                return;
            }

            e.Handled = true;
            SavedPasswordAccess.ForgetSavedPassword(row.Profile);

            // Re-render this row in place rather than reloading: LoadProfiles rebuilds every
            // SshProfileRowViewModel, so ApplyFilters' identity check drops the selection.
            RenderSavedPasswordState(row);
        }
```

Note on `TryGetRow`: it returns `_selectedRow` when one exists, otherwise falls back to walking `sender`'s `DataContext`/`Tag` ancestors. With no selection, the Forget button lives in the detail panel whose `DataContext` is the `SshManagerViewModel`, not a row — so the walk finds nothing and returns false. That is what `ForgetSavedPassword_DoesNothing_WhenNoRowSelected` asserts.

- [ ] **Step 5: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter FullyQualifiedName~ConnectionManagerTests
```

Expected: PASS, including the Task 2 tests and `ConnectionManager_CanArrangeWithinSmallOverlay` (the new row is inside the already-scrolling `ScrollViewer` at line 560).

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Controls/ConnectionManager.axaml src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs tests/NovaTerminal.App.Tests/Ssh/ConnectionManagerTests.cs
git commit -m "feat(connections): show and clear a connection's saved password"
```

---

### Task 4: Wire delete + vault access into `MainWindow`

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` — `EnsureConnectionManagerControl()` (line 5166–5209); two new private methods placed after `ShowNewSshConnectionDialogAsync` (ends ~line 5535)

**Interfaces:**
- Consumes: `ConnectionManager.OnDeleteProfileRequested` (Task 2), `ConnectionManager.SavedPasswordAccess` (Task 3), `VaultService.ForgetSavedPassword` (Task 1); existing `MainWindow.Vault` (`static VaultService?`, line 3326), `_sshConnectionService.DeleteProfile(Guid)`, `RefreshProfileUIs()`, `CreateThemedDialogWindow(string, double, double, bool)`.
- Produces: nothing consumed by later tasks. Last task.

- [ ] **Step 1: Wire the control**

In `EnsureConnectionManagerControl()`, after `connManager.ApplyTheme(_settings.ActiveTheme);` (line 5180) add:

```csharp
            connManager.SavedPasswordAccess = Vault;
```

And after the `connManager.OnEditProfile += …` block (ends line 5206) add:

```csharp
            connManager.OnDeleteProfileRequested += async (profile) =>
            {
                await DeleteSshProfileAsync(profile);
            };
```

- [ ] **Step 2: Add the delete flow**

Add after `ShowNewSshConnectionDialogAsync` (ends ~line 5535):

```csharp
        private async Task DeleteSshProfileAsync(TerminalProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            string label = string.IsNullOrWhiteSpace(profile.Name)
                ? "this connection"
                : $"\"{profile.Name.Trim()}\"";

            if (!await ShowDeleteConnectionConfirmationAsync(label))
            {
                return;
            }

            try
            {
                _sshConnectionService.DeleteProfile(profile.Id);

                // Purge AFTER the store delete: if the delete throws, the secret stays put
                // rather than being orphaned from a profile that still exists.
                Vault?.ForgetSavedPassword(profile);

                RefreshProfileUIs();
            }
            catch (Exception ex)
            {
                await ShowSimpleMessageDialogAsync("Delete connection", ex.Message);
            }
        }

        private async Task<bool> ShowDeleteConnectionConfirmationAsync(string label)
        {
            bool confirmed = false;

            var dialog = CreateThemedDialogWindow("Delete connection?", 480, 210, canResize: false);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 92
            };
            cancelButton.Click += (_, __) =>
            {
                confirmed = false;
                dialog.Close();
            };

            var deleteButton = new Button
            {
                Content = "Delete",
                Width = 92
            };
            deleteButton.Click += (_, __) =>
            {
                confirmed = true;
                dialog.Close();
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Delete {label}?",
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "The saved connection and any password stored for it are removed. "
                                 + "Panes already connected keep running until you close them.",
                            Opacity = 0.8,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, deleteButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return confirmed;
        }
```

This mirrors `ShowRunningProcessCloseConfirmationAsync` (line 4277) — same `CreateThemedDialogWindow` + local `bool confirmed` + `await dialog.ShowDialog(this)` shape. All types used (`Button`, `Border`, `StackPanel`, `TextBlock`, `Thickness`, `FontWeight`, `HorizontalAlignment`, `TextWrapping`) are already imported in this file.

- [ ] **Step 3: Build the app**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Expected: build succeeds, no warnings introduced.

- [ ] **Step 4: Run the affected test projects**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~ConnectionManager|FullyQualifiedName~VaultService|FullyQualifiedName~SshConnectionService"
```

Expected: PASS. Nothing here changes `TerminalSettings` or connection-profile schema, so the `NovaTerminal.McpServer.Tests` drift guards are untouched — but if the reviewer wants belt-and-braces:

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests
```

- [ ] **Step 5: Manual smoke test**

Automated GUI driving is unreliable here, so verify by hand. Do **not** use
`scripts/build.ps1 run` — the wrapper deliberately omits `run` from its
`-nodeReuse:false` insert, so a captured-stdout run can hang. Build, then launch
the produced executable yourself from an ordinary terminal:

```bash
scripts/build.ps1 build src/NovaTerminal.App -c Debug
```

then run `src/NovaTerminal.App/bin/Debug/net10.0/NovaTerminal.App.exe`
(`net10.0` is the TFM from `Directory.Build.props:20`).

Then:
1. Open the connection manager. Select a connection. Confirm a red trash icon sits at the right end of the action bar with tooltip "Delete connection".
2. In the detail body under CONNECTION, confirm a **Saved password** row reading `No` with a greyed-out "Forget saved password" button.
3. Connect to a host with password auth, tick "Remember password" in the prompt, connect. Reopen the manager, reselect that connection — the row should read `Yes` and the button should be enabled.
4. Click "Forget saved password". The row flips to `No`, the button greys out, **and the row stays selected in the list**.
5. Reconnect — you should be prompted for the password again.
6. Click the trash icon. Confirm the dialog names the connection and mentions the password. Cancel — the connection is still listed.
7. Click trash again, confirm Delete. The row disappears; check the New Tab menu and command palette no longer offer it.
8. Open a session on a connection, then delete that connection while the pane is live. The pane must keep running.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs
git commit -m "feat(connections): confirm and delete a connection, purging its saved password"
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| Feature 1 — UI (Danger trash button, action bar column) | Task 2, Step 3 |
| Feature 1 — Control (`OnDeleteProfileRequested`, `OnDeleteClick`) | Task 2, Step 4 |
| Feature 1 — Host (`DeleteSshProfileAsync`, confirm, purge order, `RefreshProfileUIs`) | Task 4, Steps 1–2 |
| Feature 1 — Deleting a profile in use | Task 4, Step 5 item 8 (manual); no code needed |
| Feature 2 — `ISavedPasswordAccess` + `VaultService` impl | Task 1, Steps 3–4 |
| Feature 2 — profile-scoped keys only; probe without writing | Task 1, Step 4 + tests 5–7 |
| Feature 2 — shared-alias caveat documented in XML docs | Task 1, Step 3 |
| Feature 2 — UI row + Forget button | Task 3, Step 3 |
| Feature 2 — 4-state render table | Task 3, Step 4 (`RenderSavedPasswordState`) |
| Feature 2 — no confirm, no reload, selection preserved | Task 3, Step 4 + `ForgetSavedPassword_FlipsRowToNo…` |
| Testing items 1–7 | Tasks 1–3 |
| Out of scope: editor checkbox, MCP tools | not touched by any task |

**Dropped:** Testing item 8 (a combined `SshConnectionService`-level check
that delete removes the profile from the store *and* purges the
profile-scoped vault keys) is intentionally not implemented. It is not
implementable as specified: `SshConnectionService.DeleteProfile` delegates
straight to `JsonSshProfileStore.DeleteProfile` and never touches the vault —
the purge lives in `MainWindow.DeleteSshProfileAsync` by design. The store-
delete and the vault-purge are covered separately instead, at their own
layers (see the corrected Testing section of the design spec).

**Placeholder scan:** no TBD/TODO; every code step carries the actual code; no "similar to Task N".

**Type consistency:** `ISavedPasswordAccess` / `IsVaultAvailable` / `HasSavedPassword` / `ForgetSavedPassword` are spelled identically in Tasks 1, 3, 4. `OnDeleteProfileRequested` matches between Tasks 2 and 4. `SavedPasswordAccess` matches between Tasks 3 and 4. Control names `KvSavedPassword` / `BtnForgetSavedPassword` match between Task 3's XAML, code and tests. Tooltip `"Delete connection"` matches between Task 2's XAML and both its new test and the amended `actionTips` array.

**One deviation from the spec, deliberate:** the spec's Files-touched list said "`tests/NovaTerminal.App.Tests/Core/` — new or extended `VaultService` tests". The plan creates a new file, `VaultServiceSavedPasswordTests.cs`, rather than extending `VaultServiceDisabledModeTests.cs`, whose name would no longer describe its contents.
