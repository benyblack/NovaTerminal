# A pane whose shell dies says so (#311)

A local pane whose PTY dies just freezes: last screen still up, cursor still drawn, keystrokes
swallowed, nothing on screen about what happened. This gives it a sentence, and closes the pane
when the shell exited cleanly.

Written to `docs/plans/` rather than `docs/superpowers/specs/` to sit next to the rest of this
repository's design documents.

## The problem

A user reported "I can't exit opencode" with a screen recording. opencode had exited fine; the
console host crashed on its way out (#310) and took the shell with it. The pane looked exactly like
a hung foreground program, so they kept typing into a dead session, gave up, and switched to another
terminal.

The information all existed:

- `novaterminal.get_session_status` reported `exited`.
- `debug.log` recorded `[RustPtySession] EOF received.`
- `TerminalPane.ProcessExited` fired, and `MainWindow.OnPaneProcessExited` stored the exit code.

None of it reached the screen. Three specifics:

1. **The recovery already works and is invisible.** `ShouldReconnectOnEnter` is
   `session == null || !session.IsProcessRunning` — not SSH-specific — and `Reconnect()` re-runs
   `InitializeSession`. Enter in a dead local pane already restarts the shell today. `TerminalView`
   even goes out of its way to let Enter bubble on a dead session, twice (plain and kitty encoders).
   Nothing tells the user.
2. **The banner is gated on SSH.** `HandleSessionExit` writes
   `[SSH session disconnected] / [Exit code: N] / [Press Enter to reconnect]` only when
   `Profile?.Type == ConnectionType.SSH`. Local panes get silence.
3. **The tab glyph is ambiguous.** `state.LastExitCode` is written both by `OnPaneCommandFinished`
   (per-command, from shell integration) and by `OnPaneProcessExited`, and renders as `✓` / `✖N`
   either way. A dead shell looks identical to a command that failed. In the reported case the shell
   died with code 0, so the tab showed `✓`.

## The behaviour contract

`ShellExitPolicy` is a string setting, `"Never" | "Graceful" | "Always"`, default `"Graceful"`.

| Pane | Exit | Policy | Behaviour |
|---|---|---|---|
| Local | 0 | `Graceful` (default) | Pane closes. Last pane in the tab closes the tab; last tab closes the window. |
| Local | 0 | `Never` | Banner, pane stays. |
| Local | non-zero, or host/PTY died | `Graceful`, `Never` | Banner, pane stays. |
| Local | any | `Always` | Pane closes. |
| SSH | any | any | Unchanged: existing SSH banner, Enter reconnects, never auto-closes. |

Local banner text, mirroring the SSH one's shape (exit-code line omitted when the code is 0, as SSH
already does):

```
[Shell exited]
[Exit code: 1]
[Press Enter to restart]
```

Unstyled, written through the existing `WriteBanner` (which parses it as terminal input, so it lands
in the buffer and therefore in scrollback, `read_screen`, and replay exports).

### What overrides the close

- **A protected tab never auto-closes.** `CloseTabAsync` already returns false for
  `IsProtected`; the pane gets the banner instead. Protection cannot be defeated by a dying shell.
- **Auto-close skips the confirmation dialog** (`PaneClosePolicy = "Confirm"`). The shell is already
  gone, and an unattended modal is the stuck state this issue is about. Same reasoning as the
  existing agent-initiated close.
- **`exit` in the only tab quits the app.** `CloseTab` calls `Close()` when the last tab goes. This
  matches every other terminal and is the accepted consequence of the `Graceful` default.

## Composition

The decision is a pure function; the two side effects live where they already live.

**`SessionExitDecision.For(policy, isSsh, exitCode)` → `TryClose | BannerOnly`** — new static, no
Avalonia, no I/O. SSH is always `BannerOnly`. `Never` is always `BannerOnly`. `Always` is
`TryClose`. `Graceful` is `TryClose` when `exitCode == 0`. Protection and reentrancy deliberately do
**not** appear here; see the fallback rule below.

**`TerminalPane`** keeps `HandleSessionExit` as the single exit funnel and its SSH branch untouched.
It gains `internal void WriteLocalExitBanner(int code)`. The pane does not read the setting and does
not decide anything, so no `ApplySettings` whitelist entry is needed.

**`MainWindow.OnPaneProcessExited`** owns the decision, because it is the only party that knows the
tab, its protected state, and the settings. It asks `SessionExitDecision`, then either writes the
banner or attempts the close. It stays a `void` event handler and hands the close to a private
`async Task` helper (`HandlePaneExitAsync`) rather than becoming `async void`, so the fallback below
observes the close result instead of firing and forgetting it.

**The fallback rule:** on `TryClose`, attempt the close and write the banner if the close did not
happen. A refusal can come from a protected tab, from the `_closePaneInProgress` /
`_closeTabInProgress` reentrancy guards, or from a pane with no ancestor tab. Every one of those
paths must end with a pane that says something rather than a pane that says nothing — which is the
whole point of the issue. This is why protection is not a parameter of the decision: it stays
enforced in exactly one place.

### The close has to target the pane, not the active one

`PaneAction.Close` → `CloseActivePaneAsync` is the wrong vehicle, and the reason is worth writing
down. It reads `_currentPane` for the split case but falls back to `tabs.SelectedItem` when the pane
is alone in its tab, and its zoom-exit path uses `TryGetSelectedTab`. `UpdateActivePane(pane)` does
not select the pane's tab. So a shell dying in a **background** tab — a long-running build, an agent
session, exactly the tabs you are not watching — would close the tab the user is currently looking
at.

So: extract `ClosePaneAsync(TerminalPane pane, bool skipConfirm)` from `CloseActivePaneAsync`, with
every `tabs.SelectedItem` / `TryGetSelectedTab` replaced by `pane.FindAncestorOfType<TabItem>()`,
and redefine `CloseActivePaneAsync(skipConfirm)` as `ClosePaneAsync(_currentPane, skipConfirm)`. The
split-promotion body is already pane-relative and moves unchanged. `ClosePaneAsync` returns
`Task<bool>` so the fallback rule above can see a refusal.

This also fixes the same latent bug on the agent-initiated close path, which can be handed a
background pane today.

### Settings surface

- `TerminalSettings.ShellExitPolicy = "Graceful"`, next to `PaneClosePolicy`.
- **settings.json only, no `SettingsWindow` control** — its sibling `PaneClosePolicy` has none
  either, and matching that is better than growing the settings window for this. A UI control for
  both policies together is reasonable follow-up work.
- `SettingsTools` needs the documented-field row, the sample JSON entry, the enum-like-string list,
  and the writable-field list updated, or `SettingsToolsDriftGuardTests` fails.
- An unrecognised value behaves as `"Graceful"`: this setting decides whether a pane disappears, and
  a typo in a hand-edited settings file must not silently mean "never tell me anything again".

## Data flow

```
PTY EOF
  └─ RustPtySession.OnExit (background thread)
       └─ Dispatcher.UIThread.Post                       [already present]
            └─ TerminalPane.HandleSessionExit
                 ├─ ignores stale sessions (ReferenceEquals guard)   [already present]
                 ├─ LastExitCode = code                              [already present]
                 ├─ SSH: WriteSshDisconnectedBanner                  [already present]
                 └─ ProcessExited                                    [already present]
                      └─ MainWindow.OnPaneProcessExited
                           ├─ tab state + glyph refresh              [already present]
                           └─ SessionExitDecision.For(...)
                                ├─ BannerOnly → pane.WriteLocalExitBanner(code)
                                └─ TryClose   → HandlePaneExitAsync (private async Task)
                                                 └─ await ClosePaneAsync(pane, skipConfirm: true)
                                                      └─ false → pane.WriteLocalExitBanner(code)
```

Recovery after a banner needs no new code: Enter → `TerminalPane.OnKeyDown` →
`ShouldReconnectOnEnter` → `Reconnect()`, which clears `LastExitCode` (so the tab glyph clears too),
writes `[Reconnecting...]`, and calls `InitializeSession`.

## Edge cases

- **Stale exits.** A session that has already been replaced by `Reconnect()` is dropped by the
  existing `ReferenceEquals(Session, session)` guard.
- **No tab.** `OnPaneProcessExited` already returns early when the pane has no ancestor tab; with
  the fallback rule the pane still gets its banner first.
- **Exit during a close.** The reentrancy guards make the close a no-op; the fallback writes the
  banner. Worst case the user sees a banner in a pane that is about to vanish.
- **Zoomed pane.** `ClosePaneAsync` exits zoom for the pane's own tab, not the selected one.
- **Agent registration.** `ProcessExited` already drives `StatusMachine.NotifyExited(exitCode)`
  before any of this, so agent status stays correct whether the pane closes or stays.
- **Broadcast tabs.** A closed pane leaves broadcast membership via the existing `CloseTab` /
  `ClosePaneAsync` cleanup; no new bookkeeping.

## Testing

Unit, no UI:

- `SessionExitDecision` matrix: three policies × {0, non-zero} × {local, SSH}.
- Unrecognised policy string falls back to `Graceful`.

Pane-level, via the existing `HandleSessionExitForTesting` seam (already used by
`TerminalPaneSshDisconnectTests`):

- Local non-zero exit writes `[Shell exited]`, `[Exit code: N]`, `[Press Enter to restart]`.
- Local exit 0 omits the exit-code line.
- The SSH banner is unchanged, asserted byte-for-byte.

Headless `MainWindow` (`AvaloniaTestCase`):

- `Graceful` + clean exit closes the dying pane's tab.
- `Graceful` + clean exit in a **background** tab closes that tab and leaves the selected tab alone.
  This is the regression test for the targeting bug above.
- A protected tab survives a clean exit and shows the banner instead.
- `Never` keeps the pane and shows the banner.

## Out of scope

- **The ambiguous tab glyph.** `LastExitCode` conflating last-command-exit with process-exit
  deserves its own issue: after this change the pane is unambiguous, the tab strip still is not.
- **SSH auto-close.** Deliberately excluded; remote sessions keep their reconnect affordance.
- **The console-host crash itself.** That is #310, already fixed by sideloading the ConPTY host.
