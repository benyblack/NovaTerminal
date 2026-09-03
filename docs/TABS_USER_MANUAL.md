# NovaTerminal Tabs User Manual

Date: 2026-09-03  
Audience: End users of NovaTerminal tabs

## 1. Quick Start

Open the Command Palette with:
- `Ctrl+Shift+P`

Create and switch tabs:
- New tab: `Ctrl+Shift+T`
- Next tab (MRU): `Ctrl+Tab`
- Previous tab (MRU): `Ctrl+Shift+Tab`
- Open tab list: `Ctrl+Shift+O`

Rearrange and lay out:
- Move tab left: `Ctrl+Shift+PageUp`
- Move tab right: `Ctrl+Shift+PageDown`
- Toggle the vertical tab sidebar: `Ctrl+Shift+L`

Close behavior:
- Close tab: `Ctrl+W`
- Close active pane: `Ctrl+Shift+W`

## 2. Core Tab Behavior

### MRU switching
- `Ctrl+Tab` switches by Most Recently Used order, not strict left-to-right.
- This helps you bounce between your two or three active tabs quickly.

### Reordering
- Drag a tab to move it. The dragged header dims, and an insertion indicator marks
  where it will land. Dragging near either end of the strip auto-scrolls it, so you
  can reach a position that is currently off-screen.
- `Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown` move the selected tab one position.
- Both work in either orientation.

### Overflow handling
- When many tabs exist, hidden tabs are accessible via:
  - Tab list button in title bar (horizontal mode)
  - An overflow pill at the bottom of the sidebar, showing how many tabs are hidden
    (vertical mode)
  - `Tab: Open Tab List` command, in either mode
- Selecting from the tab list auto-selects and closes the menu.

### Tab title behavior
Title precedence:
1. User rename
2. Shell-reported title / working directory source
3. Fallback title

Tab labels may be truncated to fit. When labels collide, NovaTerminal appends a uniqueness hint.

## 3. Activity and Status Indicators

On horizontal tab headers, you may see:
- `•` for background output activity
- `🔔` for bell/attention (debounced to avoid spam)
- `✓` or `✖<code>` for last command/process exit status
- `📌` pinned tab
- `🔒` protected tab

Notes:
- Attention indicators clear when you activate the tab.
- Exit status updates on command finish/process exit events.

The vertical sidebar shows a richer, agent-aware version of the same state — see
section 4.

## 4. Vertical Tab Sidebar

`Ctrl+Shift+L` swaps the top tab strip for a left sidebar. You can also set it from
Settings → Appearance → **Tab strip orientation**, or run
`Tabs: Toggle Vertical Tab Sidebar` from the palette. The choice persists.

Vertical mode trades horizontal space for a much richer row per tab, which is what
you want when tabs are running long jobs or agents rather than sitting idle. Each
row shows the tab title, a status dot, any marker chips, and the tab's most recent
non-empty output line as a live preview.

### Status dot

The dot paints exactly one state, in this precedence:

| Dot | Meaning |
|---|---|
| Amber | A bell fired, or the tab wants attention |
| Amber (agent) | An agent typed into this tab and you have not looked yet |
| Blue | Output is streaming, or a command is still running |
| Blue (agent) | An agent is reading this tab (only under the "All" rollup policy) |
| *(none)* | Idle |

Status is heuristic: it is driven by the pane events the window already receives,
re-evaluated about once a second while vertical mode is active, and decays back to
idle on its own.

### Marker chips

Chips trail the title and, unlike the dot, can stack — bell, plain activity,
agent-wrote and agent-watched are tracked separately. Bell and plain activity are
the one mutually exclusive pair; a bell wins.

### Layout

- **Resize:** drag the sidebar's right edge. The width is remembered across restarts
  (Settings stores it alongside the orientation).
- **Overflow:** when rows run past the bottom, a pill pinned to the bottom edge shows
  the hidden-tab count and opens a list of them.
- **Reorder:** drag a row, or use `Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown`.

## 5. Tab Actions (Command Palette)

Open Command Palette (`Ctrl+Shift+P`) and use:
- `Tab: Rename Current`
- `Tab: Copy Current Title`
- `Tab: Close Others`
- `Tab: Toggle Pin`
- `Tab: Toggle Protect`
- `Tab: Move Previous` / `Tab: Move Next`
- `Tabs: Toggle Vertical Tab Sidebar`

## 6. Workspaces

Commands:
- `Workspace: Save Current`
- `Workspace: Load...`

What is saved:
- Tab/pane layout
- Active tab
- Active pane and zoom state
- Broadcast-input flag
- Stable tab identity and tab metadata

## 7. Workspace Templates (Team Workflow)

Templates are reusable session blueprints.

Commands:
- `Workspace Template: Save Current`
- `Workspace Template: Apply...`
- `Workspace Template: Apply <name>` (dynamic entries after template creation)

Use case:
- Create a standard multi-tab setup once.
- Save as template.
- Re-apply whenever needed.

## 8. Per-Profile Template Rules

These rules auto-apply a template when opening a new tab for a specific profile.

Commands:
- `Tab Rule: Set Template for Current Profile...`
- `Tab Rule: Clear Template for Current Profile`

Flow:
1. Focus a tab using the profile you want to target.
2. Set a template rule.
3. Open a new tab with that profile.
4. Template auto-applies instead of plain single-pane default.

## 9. Workspace Bundles (M3)

Bundles are portable `.novaws.json` files for handoff/import/open workflows.

Commands:
- `Workspace: Export Bundle...` (from saved workspace)
- `Workspace: Import Bundle...` (validates, then saves workspace)
- `Workspace: Open Bundle...` (validates, then applies directly without saving)
- `Workspace: Export Current Session Bundle...` (exports current live tabs without requiring a saved workspace first)

Security and integrity:
- Bundle payload is hash-verified (`SHA-256`) before import/open.
- Tampered bundles are rejected.

## 10. Enterprise Policy Hooks (Managed Environments)

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

## 11. Audit Log

Workspace/bundle/template operations are logged to:
- `%LOCALAPPDATA%\NovaTerminal\logs\workspace_audit.log`

Audit includes:
- UTC timestamp
- action name
- success/failure
- workspace/template context
- operation details

## 12. Troubleshooting

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

## 13. Shortcut Reference

- Command palette: `Ctrl+Shift+P`
- New tab: `Ctrl+Shift+T`
- Close tab: `Ctrl+W`
- Close pane: `Ctrl+Shift+W`
- Next tab (MRU): `Ctrl+Tab`
- Previous tab (MRU): `Ctrl+Shift+Tab`
- Move tab left: `Ctrl+Shift+PageUp`
- Move tab right: `Ctrl+Shift+PageDown`
- Open tab list: `Ctrl+Shift+O`
- Toggle vertical tab sidebar: `Ctrl+Shift+L`
- Split vertical: `Ctrl+Shift+D`
- Split horizontal: `Ctrl+Shift+E`
- Equalize panes: `Ctrl+Shift+G`
- Toggle pane zoom: `Ctrl+Shift+Z`
- Toggle pane broadcast input: `Ctrl+Shift+B`

Every shortcut here is the default. Settings → Shortcuts lists them all and lets
you rebind them.

