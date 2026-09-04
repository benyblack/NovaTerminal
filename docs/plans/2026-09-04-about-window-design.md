# About window + embedded update check — design

Date: 2026-09-04
Status: implemented

## Problem

NovaTerminal had no About surface anywhere: no way to see the running version without
reading a log, and the manual update check existed only as the palette's
"Update: Check for updates", answered through main-window toasts.

## Decision

One modal dialog, `UI/About/AboutWindow`, entered only from the "+" title-bar flyout
("About NovaTerminal..."). The window shows the app icon, name, and version (resolved from
`AssemblyInformationalVersionAttribute`, the same attribute-based resolution
`BackupService` uses), an inline update-check result, a "Restart now" button when an update
is staged, and repo/releases links.

## How the check is shared

The window does not own any update machinery. `MainWindow.ShowAboutWindowAsync` injects
three delegates (property injection, the same way `SettingsWindow` is wired):

- `RunUpdateCheck` → `CheckForUpdatesInteractiveAsync(about)` — so a check started from
  About shares the coordinator, `_updateCheckInFlight` guard, and announce-once state with
  the palette's manual check and the deferred startup check;
- `ApplyStagedUpdate` → `MainWindow.ApplyStagedUpdate` (teardown included);
- `StagedVersionProvider` → lets the window show an already-staged update on open and keep
  the restart affordance visible if a re-check fails while an update is on disk.

`CheckForUpdatesInteractiveAsync` gained an optional
`NovaTerminal.Update.IUpdateCheckFeedback`. Without one it constructs
`ToastUpdateCheckFeedback` (the pre-existing toast behavior, strings now sourced from
`NovaTerminal.Update.UpdateCheckMessages`); the About window implements the interface to
render inline. `UpdateCheckMessages` is UI-free so both surfaces cannot drift and the
mapping is unit-tested in the gating loop
(`tests/NovaTerminal.Architecture.Tests/Update/UpdateCheckMessagesTests.cs`).

## Deliberately out of scope

- No palette entry for About; the existing "Update: Check for updates" palette command is
  unchanged.
- No pinnable title-bar button, no new `TerminalSettings` fields (hence no `SettingsTools`
  schema/drift-guard edits and no effective-settings whitelist change), no MCP app-info tool.
