# NovaTerminal Tabs User Manual

Date: 2026-02-14  
Audience: End users of NovaTerminal tabs

## 1. Quick Start

Open the Command Palette with:
- `Ctrl+Shift+P`

Create and switch tabs:
- New tab: `Ctrl+Shift+T`
- Next tab (MRU): `Ctrl+Tab`
- Previous tab (MRU): `Ctrl+Shift+Tab`
- Open tab list: `Ctrl+Shift+O`

Close behavior:
- Close tab: `Ctrl+W`
- Close active pane: `Ctrl+Shift+W`

## 2. Core Tab Behavior

### MRU switching
- `Ctrl+Tab` switches by Most Recently Used order, not strict left-to-right.
- This helps you bounce between your two or three active tabs quickly.

### Overflow handling
- When many tabs exist, hidden tabs are accessible via:
  - Tab list button in title bar
  - `Tab: Open Tab List` command
- Selecting from the tab list auto-selects and closes the menu.

### Tab title behavior
Title precedence:
1. User rename
2. Shell-reported title / working directory source
3. Fallback title

Tab labels may be truncated to fit. When labels collide, NovaTerminal appends a uniqueness hint.

## 3. Activity and Status Indicators

On tab headers, you may see:
- `•` for background output activity
- `🔔` for bell/attention (debounced to avoid spam)
- `✓` or `✖<code>` for last command/process exit status
- `📌` pinned tab
- `🔒` protected tab

Notes:
- Attention indicators clear when you activate the tab.
- Exit status updates on command finish/process exit events.

## 4. Tab Actions (Command Palette)

Open Command Palette (`Ctrl+Shift+P`) and use:
- `Tab: Rename Current`
- `Tab: Copy Current Title`
- `Tab: Close Others`
- `Tab: Toggle Pin`
- `Tab: Toggle Protect`

## 5. Workspaces

Commands:
- `Workspace: Save Current`
- `Workspace: Load...`

What is saved:
- Tab/pane layout
- Active tab
- Active pane and zoom state
- Broadcast-input flag
- Stable tab identity and tab metadata

## 6. Workspace Templates (Team Workflow)

Templates are reusable session blueprints.

Commands:
- `Workspace Template: Save Current`
- `Workspace Template: Apply...`
- `Workspace Template: Apply <name>` (dynamic entries after template creation)

Use case:
- Create a standard multi-tab setup once.
- Save as template.
- Re-apply whenever needed.

## 7. Per-Profile Template Rules

These rules auto-apply a template when opening a new tab for a specific profile.

Commands:
- `Tab Rule: Set Template for Current Profile...`
- `Tab Rule: Clear Template for Current Profile`

Flow:
1. Focus a tab using the profile you want to target.
2. Set a template rule.
3. Open a new tab with that profile.
4. Template auto-applies instead of plain single-pane default.

## 8. Workspace Bundles (M3)

Bundles are portable `.novaws.json` files for handoff/import/open workflows.

Commands:
- `Workspace: Export Bundle...` (from saved workspace)
- `Workspace: Import Bundle...` (validates, then saves workspace)
- `Workspace: Open Bundle...` (validates, then applies directly without saving)
- `Workspace: Export Current Session Bundle...` (exports current live tabs without requiring a saved workspace first)

Security and integrity:
- Bundle payload is hash-verified (`SHA-256`) before import/open.
- Tampered bundles are rejected.

## 9. Enterprise Policy Hooks (Managed Environments)

Policy file:
- `%LOCALAPPDATA%\NovaTerminal\policy\workspace_policy.json`

Supported policy fields:
- `AllowWorkspaceBundleExport` (bool)
- `AllowWorkspaceBundleImport` (bool)
- `MaxTabsPerWorkspace` (int, `0` means unlimited)
- `RequireSsoForWorkspaceBundles` (bool)
- `SsoAuthorityUrl` (string, placeholder)
- `SsoClientId` (string, placeholder)

Behavior:
- If export/import is blocked, related bundle operations are denied.
- `MaxTabsPerWorkspace` can block oversize bundle import/open.
- SSO gating is placeholder-only today:
  - If required but not configured, bundle ops fail closed.
  - If configured, it still reports placeholder-not-implemented (by design for now).

## 10. Audit Log

Workspace/bundle/template operations are logged to:
- `%LOCALAPPDATA%\NovaTerminal\logs\workspace_audit.log`

Audit includes:
- UTC timestamp
- action name
- success/failure
- workspace/template context
- operation details

## 11. Troubleshooting

### I do not see bundle commands
- Your policy may disable bundle export/import.
- Check `%LOCALAPPDATA%\NovaTerminal\policy\workspace_policy.json`.

### Import/open fails with hash mismatch
- Bundle content was modified/corrupted.
- Re-export from the source machine.

### New tab did not auto-apply template
- Check that a rule exists for the current profile.
- Confirm the referenced template still exists.
- Re-run `Tab Rule: Set Template for Current Profile...`.

### Close warning appears unexpectedly
- Pane close behavior depends on configured policy (`Confirm`, `Graceful`, `Force`) and whether process interaction was detected.

## 12. Shortcut Reference

- Command palette: `Ctrl+Shift+P`
- New tab: `Ctrl+Shift+T`
- Close tab: `Ctrl+W`
- Close pane: `Ctrl+Shift+W`
- Next tab (MRU): `Ctrl+Tab`
- Previous tab (MRU): `Ctrl+Shift+Tab`
- Open tab list: `Ctrl+Shift+O`
- Split vertical: `Ctrl+Shift+D`
- Split horizontal: `Ctrl+Shift+E`
- Equalize panes: `Ctrl+Shift+G`
- Toggle pane zoom: `Ctrl+Shift+Z`
- Toggle pane broadcast input: `Ctrl+Shift+B`

