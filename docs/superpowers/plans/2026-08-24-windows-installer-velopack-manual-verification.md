# Windows Installer + Auto-Update — Manual Verification

**Date:** 2026-08-24
**Applies to:** the Velopack installer/auto-update work on `design/windows-installer-velopack` ([#91](https://github.com/benyblack/NovaTerminal/issues/91))
**Why this file exists:** none of this can be automated. The window wiring has no test coverage by design, and the release path cannot be exercised without publishing real releases. This is Task 7 of
[the implementation plan](2026-08-24-windows-installer-velopack.md), written down so it survives the session that produced it.

Part A can be run now, on a dev build. Part B needs two real published releases and is the only thing that proves the whole chain.

---

## Part A — dev build (no install required)

A dev run is **not** a Velopack install, so the update path must be inert. That is the point: portable-zip, winget and dev users must never see update UI or errors.

1. Launch `src/NovaTerminal.App/bin/Debug/net10.0/NovaTerminal.exe` (run `scripts/build.ps1 build src/NovaTerminal.App` first if stale).
2. The app opens normally and **no** update toast appears bottom-right.
3. Open the command palette: **"Update: Check for updates"** is present under *Application*, immediately — it does not wait for the 10-second mark.
4. Run it. Expect a toast titled **"Updates unavailable"** explaining that this build was not installed by the installer. It should auto-hide after a few seconds. It must **not** be a failure toast, and must **not** be silent.
5. Settings → **"Automatic update checks"** starts **on**. Toggle off, save, close, reopen: still off. Toggle back on and save.
6. Check the debug log (path is printed at startup) for any exception or stack trace mentioning "Update". There should be none.
7. **Quake-mode hide/show cycle** — the regression check for the bug that made a staged update vanish. With quake mode on (default), press Alt+backtick to hide, again to show. Repeat 3–4 times fairly quickly, then:
   - The palette still lists **"Update: Check for updates"** exactly once — not duplicated, not missing.
   - Running it produces exactly **one** toast per invocation, not one per prior hide/show cycle.
   - Leave the app idle 15–20 s: nothing appears spontaneously. (This is what catches accumulated background-check timers.)
8. **Restart preserves the session** — on a dev build this is code inspection only: `ApplyStagedUpdate()` calls `PerformAppTeardown()`, the same method `OnClosing` calls, *before* handing off to the coordinator. Without it, taking an update would skip `SessionManager.SaveSession` and silently discard your tabs.
9. Optional: leave the app open 10+ seconds after launch and confirm no toast ever appears on a dev run.

---

## Part B — two real releases (the actual proof)

**This publishes public artifacts. Do it deliberately.**

### B1. Before tagging

- `scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests` — green. This is the gating project that now carries the update tests, including the backward-compatibility pin that an existing settings file must not silently opt a user out of update checks.
- `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "Category!=ShellIntegration"` — no *new* failures versus baseline. The PTY native-test cluster is known-flaky when `rusty_pty.dll` in the test output is stale; that is unrelated to this work.

### B2. First release

Push a tag (or `workflow_dispatch` with `tag_name`), then check the release carries:

- the three existing zips, **`NovaTerminal-win-x64-<tag>.zip` unchanged in name** — `packaging/winget/` pins that URL and its SHA256;
- `NovaTerminal-Setup-win-x64-<tag>.exe`;
- `NovaTerminalApp-<version>-full.nupkg` (the `App` suffix is deliberate - see the packId note below);
- `releases.win.json`.

**No `*-delta.nupkg` on the first release** — there was no prior Velopack release to diff against. The `Download previous Velopack release` step is expected to log a miss without failing.

Then install it and confirm: no UAC prompt (per-user install), a Start Menu entry **and a Desktop shortcut** (Velopack's default), an Add/Remove Programs entry, and the app launches normally. **SmartScreen will warn** — the installer is unsigned and that stays open on #91.

### B3. Second release — the update itself

Bump `Version` in `Directory.Build.props`, push the next tag, then in the **installed** vN app:

1. Launch and wait ~15 s.
2. The update toast appears: "NovaTerminal `<vN+1>` is downloaded and will be applied when you restart."
3. The palette offers **"Update: Restart to apply `<vN+1>`"**.
4. Open a couple of tabs, then click **Restart now**. The app restarts on vN+1 **and your tabs come back** — that is the session-preservation fix.
5. Turn **Automatic update checks** off, relaunch, and confirm no check fires while the manual palette check still works.

---

## Release-day gotchas (found during review — read before B2)

- **"Was the delta used?" cannot be answered from the releases page.** A full-only release publishes green and looks identical. Confirm from the client's Velopack log at `%LocalAppData%\velopack\velopack_NovaTerminalApp.log` — note this is *not* the app's own debug log, and the app's update-failure toasts point at the latter.
- **`releases.win.json` legitimately lists the previous release's `full.nupkg`**, which is not an asset on the current release. `vpk pack` re-indexes the whole output directory. Nothing on the happy path selects that entry (only a downgrade would, and `AllowVersionDowngrade` is off). It is known and benign — do not chase it.
- **Re-dispatching the workflow for an already-published tag** deletes and rebuilds that version's nupkg, so the re-run republishes `releases.win.json` **without a delta entry** even if a delta was uploaded the first time. Clients fall back to the full package: degraded, not broken. This is inherent to making re-runs work at all.
- **A prerelease tag stays off stable users' update path** only because `create_release` now sets GitHub's `prerelease` flag from the tag's SemVer suffix. `GithubSource` filters on that flag alone — it does not look at the version string — so if that expression is ever removed, a `-beta` tag will update everyone.

---

## What is deliberately not covered

- **Code signing.** Out of scope, still open on #91. Both the installer and the executables are unsigned, so first-run SmartScreen warnings are expected rather than defects.
- **`win-arm64`, macOS and Linux installers.** The release workflow produces only `win-x64` Velopack output.
