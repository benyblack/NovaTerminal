# Connection Manager: delete a connection, forget a saved password

Date: 2026-08-27

## Problem

The connection manager detail panel exposes Favorite, Edit, Copy launch command,
and Connection details. It has no way to **delete** a connection, and the app has
no way at all to **remove a saved SSH password**.

Current state of the code:

- `SshConnectionService.DeleteProfile(Guid)` exists and is reachable from no UI.
- `ConnectionManager.axaml` already carries an unused `Button.Danger` style,
  commented `<!-- Danger icon button (delete) -->`.
- `VaultService.ApplyRememberPasswordPreference` is defined but called from
  nowhere in `src/`. Passwords are only ever *written*, from the
  "Remember password" checkbox in `AuthPromptDialog` via
  `SshInteractionService.HandlePasswordAsync`.
- `NewSshConnectionViewModel.RememberPasswordInVault` exists but is not bound in
  `NewSshConnectionView.axaml`.

So a saved password is write-only: once stored, nothing in the product removes it.

## Scope

In scope:

1. Delete a connection from the connection manager, with confirmation, purging
   the profile's vault password.
2. Show whether a password is saved for the selected connection, and forget it.

Out of scope:

- Wiring `NewSshConnectionViewModel.RememberPasswordInVault` into the editor
  dialog. `ApplyRememberPasswordPreference` stays uncalled.
- MCP tooling. `ConnectionProfileTools` is schema/validation-only and has no
  mutating tools. No `TerminalSettings` field changes, so the `SettingsTools`
  drift guards are unaffected.
- Disconnecting live panes that are running a deleted profile (see
  "Deleting a profile in use" below).

## Feature 1 — Delete a connection

### UI

`src/NovaTerminal.App/Controls/ConnectionManager.axaml`: the detail action bar is
a `Grid` with `ColumnDefinitions="Auto,Auto,Auto,Auto,*,Auto,Auto,Auto,Auto,Auto"`.
Add one more `Auto` column and place a trash `Button` in it:

- `Classes="IconBtn Danger"`
- `ToolTip.Tip="Delete connection"`
- `Click="OnDeleteClick"`

`Button.Danger` already exists in that file's styles and sets `Foreground` to
`{StaticResource NtRed}`. `ThemePaletteResources.Apply` already remaps `NtRed`
from `theme.Red`, so the button themes correctly with no palette work.

### Control

`ConnectionManager.axaml.cs`:

```csharp
public event Action<TerminalProfile>? OnDeleteProfileRequested;

private void OnDeleteClick(object? sender, RoutedEventArgs e)
{
    if (TryGetRow(sender, out var row))
    {
        e.Handled = true;
        OnDeleteProfileRequested?.Invoke(row.Profile);
    }
}
```

This mirrors `OnEditClick` / `OnCopyCommandClick` / `OnDetailsClick` exactly. The
control deletes nothing itself; it only raises. That keeps the control free of
service and vault dependencies for the destructive path, and matches the existing
event-out / MainWindow-handles convention.

### Host

`MainWindow.axaml.cs`, in `EnsureConnectionManagerControl()`:

```csharp
connManager.OnDeleteProfileRequested += async profile =>
{
    await DeleteSshProfileAsync(profile);
};
```

`DeleteSshProfileAsync(TerminalProfile profile)`:

1. Show a themed modal built the way `ConfirmBundleCommandsAsync` builds one —
   `CreateThemedDialogWindow("Delete connection?", ..., canResize: false)`, a
   wrapped message naming the connection, Cancel and Delete buttons, a local
   `bool confirmed`, `await dialog.ShowDialog(this)`. The message states that any
   saved password will also be forgotten.
2. On cancel, return.
3. `_sshConnectionService.DeleteProfile(profile.Id)`.
4. Purge the vault: `MainWindow.Vault?.ForgetSavedPassword(profile)` (see
   Feature 2). Purge after the store delete so a store failure leaves the secret
   in place rather than orphaning a profile from its password.
5. `RefreshProfileUIs()` — already re-runs `PopulateNewTabMenu()`,
   `SetupCommandPalette()`, and
   `_connectionManagerControl?.LoadProfiles(_sshConnectionService.GetConnectionProfiles())`,
   which drops the row and clears the detail panel.

### Deleting a profile in use

Deleting does not disconnect a pane already running that profile. A pane holds
its own `TerminalProfile` reference, and `ApplySettingsRecursive` already
null-guards `GetConnectionProfile(pane.Profile.Id)` returning null and keeps the
pane's existing copy. So the live session survives until closed normally. This is
deliberate: killing a running shell as a side effect of tidying the connection
list would be worse than leaving it be.

## Feature 2 — Forget a saved password

### Vault surface

`src/NovaTerminal.App/Shell/VaultService.cs` gains an interface alongside the
existing `ISshPasswordVault`:

```csharp
public interface ISavedPasswordAccess
{
    bool IsVaultAvailable { get; }
    bool HasSavedPassword(TerminalProfile profile);
    bool ForgetSavedPassword(TerminalProfile profile);
}
```

`VaultService` implements it:

- `IsVaultAvailable` => the existing `PersistenceAvailable` (`_store.IsAvailable`).
- `HasSavedPassword` => any of `GetProfileScopedSshPasswordKeysForProfile(profile)`
  reads back a non-empty value via `GetSecret`.
- `ForgetSavedPassword` => `RemoveSecret` over every key in
  `GetProfileScopedSshPasswordKeysForProfile(profile)`; returns true if any
  removal returned true.

Two deliberate choices:

**Profile-scoped keys only.** `GetProfileScopedSshPasswordKeysForProfile` yields
the canonical `SSH:PROFILE:{guid}` key plus the per-profile legacy keys, and
excludes the shared `SSH:{user}@{host}` alias that
`GetSshPasswordKeysForProfile` includes. Forgetting one profile's password must
not delete a secret a sibling profile on the same host also resolves through.
Probe and purge use the same key set so the UI can never show a state the button
cannot clear.

**Probe with `GetSecret`, not `ResolveSshPasswordForProfile`.** The latter
migrates a legacy hit to the canonical key, i.e. it *writes*. A probe that runs
on every selection change must not have that side effect.

Known caveat, to be documented in the interface XML docs: a password stored only
under the shared `SSH:{user}@{host}` alias reads as "not saved" here, while
`ResolveSshPasswordForProfile` will still auto-fill from it at connect time. Such
a secret migrates to a profile-scoped key on first use, after which the row
reports correctly. This is a legacy-store artifact and is preferred to the
alternative, where forgetting profile A's password silently breaks profile B.

### UI

The detail body's `CONNECTION` grid is `ColumnDefinitions="130,*"` with
`RowDefinitions="Auto,Auto,Auto,Auto,Auto"` (Host, Port, User, Group, Auth). Add
a sixth `Auto` row, **Saved password**. Column 0 is the `Saved password` label,
matching the five above it. Column 1 holds a horizontal `StackPanel`
(`Spacing="10"`, `VerticalAlignment="Center"`) containing:

- `KvSavedPassword` `TextBlock` reading `Yes`, `No`, or `Vault unavailable`,
  styled like the other value cells (`NtMono`, `FontSize="12.5"`).
- A `Forget saved password` button (`Classes="Pill"`, name
  `BtnForgetSavedPassword`, `Click="OnForgetSavedPasswordClick"`).

`ConnectionManager` gains:

```csharp
public ISavedPasswordAccess? SavedPasswordAccess { get; set; }
```

set by `EnsureConnectionManagerControl()` to `MainWindow.Vault`.

`RenderDetail(row)` calls a new `RenderSavedPasswordState(row)`:

| `SavedPasswordAccess` state          | Row text            | Button        |
|--------------------------------------|---------------------|---------------|
| null                                 | `—`                 | not visible   |
| `IsVaultAvailable == false`          | `Vault unavailable` | disabled      |
| available, `HasSavedPassword` true   | `Yes`               | enabled       |
| available, `HasSavedPassword` false  | `No`                | disabled      |

`OnForgetSavedPasswordClick` resolves the row via `TryGetRow`, calls
`ForgetSavedPassword`, then re-runs `RenderSavedPasswordState(row)` for that same
row.

No confirmation dialog, and no `LoadProfiles` refresh:

- Forgetting a password is low-stakes — worst case is one extra prompt on the
  next connect. A modal for that is friction without a payoff.
- The row flipping to `No` and the button greying out *is* the feedback.
- Re-rendering in place rather than reloading keeps the list selection.
  `LoadProfiles` rebuilds every `SshProfileRowViewModel`, so `ApplyFilters`'
  `nextRows.Contains(_selectedRow)` check fails and selection is lost. That is
  acceptable for Delete (the row is gone) but wrong here.

## Testing

`tests/NovaTerminal.App.Tests/Ssh/ConnectionManagerTests.cs`, headless
`[AvaloniaFact]`, following the existing `CreateMeasuredConnectionManager` /
`SelectFirstRow` / `FindButtonByToolTip` helpers:

1. Delete button raises `OnDeleteProfileRequested` with the selected profile.
2. `SecondaryActionButtons_ReserveSquareHitTargets` — add `"Delete connection"`
   to `actionTips` and change `Assert.Equal(4, actionButtons.Count)` to `5`.
   Without this the new button is silently uncovered by the hit-target guard.
3. Saved-password row renders `Yes` / `No` / `Vault unavailable` across the three
   vault states, driven by a test double for `ISavedPasswordAccess`.
4. Forget flips the row to `No`, disables the button, and leaves the list
   selection intact.

`VaultService` unit tests over `InMemorySecretStore`:

5. `ForgetSavedPassword` clears the canonical key and the per-profile legacy
   keys, and leaves the shared `SSH:{user}@{host}` alias intact.
6. `HasSavedPassword` is true for a canonical-key secret and for a per-profile
   legacy secret, false for a shared-alias-only secret, and performs no write
   (the store's contents are unchanged after a probe).
7. With an unavailable store, `IsVaultAvailable` is false, `HasSavedPassword` is
   false, and `ForgetSavedPassword` returns false without throwing.

A combined `SshConnectionService`-level check ("delete removes the profile
from the store and purges the profile-scoped vault keys") is not implementable
as originally written: `SshConnectionService.DeleteProfile` delegates straight
to `JsonSshProfileStore.DeleteProfile` and never touches the vault
(`src/NovaTerminal.App/Services/Ssh/SshConnectionService.cs:161-164`) — the
purge lives in `MainWindow.DeleteSshProfileAsync` by design, after the store
delete succeeds (see the vault-scoping remarks on `ISavedPasswordAccess`).
The store-delete and the vault-purge are instead verified separately, at
their own layers: `SshConnectionServiceTests` covers `DeleteProfile` against
the store, and items 5-7 above cover `VaultService.ForgetSavedPassword`
against the vault. The combined end-to-end flow (delete a connection that has
a saved password, confirm both the profile and its secret are gone) is
covered by the manual smoke test, not an automated `SshConnectionService`
test.

## Files touched

- `src/NovaTerminal.App/Controls/ConnectionManager.axaml`
- `src/NovaTerminal.App/Controls/ConnectionManager.axaml.cs`
- `src/NovaTerminal.App/Shell/VaultService.cs`
- `src/NovaTerminal.App/MainWindow.axaml.cs`
- `tests/NovaTerminal.App.Tests/Ssh/ConnectionManagerTests.cs`
- `tests/NovaTerminal.App.Tests/Core/` — new or extended `VaultService` tests
