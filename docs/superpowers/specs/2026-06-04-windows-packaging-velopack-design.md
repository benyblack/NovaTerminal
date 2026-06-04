# Windows Packaging: Velopack Installer + Auto-Update (signing-ready)

**Date:** 2026-06-04
**Tracking issue:** #91 (Windows installer + auto-update (Velopack) + code signing)
**Scope of this spec:** Windows packaging as a whole — Velopack installer + auto-update, with a code-signing *seam* that is a no-op until a certificate is provided. Code signing's actual certificate, macOS notarization, Linux packaging, and optional extras (`nova` on PATH, file associations) are explicitly **out of scope** and tracked separately under #91.

## Goal

Supplement the portable `NovaTerminal-win-x64-*.zip` with a proper Windows install + update experience: an installer, Start Menu shortcut, Add/Remove Programs entry, and delta auto-update from version N to N+1 — produced by CI on tag. Wire a signing seam now so an Authenticode certificate drops in later with zero CI rework.

## Non-goals

- Obtaining or configuring an actual code-signing certificate (deferred; the seam is a no-op until secrets exist).
- macOS notarization, Linux `.deb`/AppImage.
- `nova` on PATH, file associations.
- Replacing the portable zips. The installer **supplements** them; all existing release assets stay.
- Periodic/background polling for updates (checks happen once per launch).
- A nightly update channel (nightly stays zip-only).

## Key decisions (from brainstorming)

| Decision | Choice |
|---|---|
| Signing | **Defer the cert**; wire a no-op signing seam into CI now. |
| Update UX | **Notify + apply on restart** (background check on startup; command-palette action to restart-and-apply). |
| Feed / hosting | **Public GitHub Releases** of this repo (no new hosting). |
| Channel | **Stable only.** |
| Coexistence | **Supplement** — keep the win-x64 portable zip *and* add the installer. |
| Sequencing | **Spike-first** — prove Velopack + NativeAOT locally before committing CI changes. |

## Architecture

Three layers plus a de-risking spike.

### Step 0 — Spike (gates everything else)

Before touching `release.yml`, validate Velopack against the app's `PublishAot=true` self-contained output **locally**:

1. Add the `Velopack` package + `VelopackApp.Build().Run()` to `Main`.
2. `dotnet publish ... -r win-x64 --self-contained true -p:PublishAot=true` as CI does.
3. `vpk pack` the publish folder; install the produced `Setup.exe`.
4. Confirm the installed app launches normally, and that a second `vpk pack` at a higher version is detected, downloaded, and applied via restart.

**Exit criteria:** install → launch → self-update N→N+1 works on a Windows box.
**If Velopack fights AOT:** decide a fallback at that point — candidates: (a) ship the *installed* build as non-AOT self-contained while keeping the portable zip AOT, or (b) use Velopack's AOT guidance/shim. The fallback choice is made with spike evidence in hand, not pre-committed here.

### Layer 1 — App code (`NovaTerminal.App`)

- Add the `Velopack` NuGet package reference.
- Call `VelopackApp.Build().Run()` as the **first line** of `Program.Main`, before the `VtReportCommand` / `SshAskPassCommand` CLI-mode checks. Velopack's `Setup.exe`/`Update.exe` re-invoke the binary with hook args (`--veloapp-install`, `--veloapp-updated`, etc.); these must be handled and the process must exit fast, before any Avalonia or app initialization.
- Add `UpdateService` wrapping Velopack's `UpdateManager` against a `GithubSource` feed:
  - `Task CheckAsync()` — called fire-and-forget on startup from a background task. Queries the feed, downloads/stages any update, and sets an observable `UpdateReady` flag carrying the target version string.
  - `Task ApplyAndRestartAsync()` — calls `UpdateManager.ApplyUpdatesAndRestart`.
  - Guards: a no-op (returns immediately, `UpdateReady = false`) when the app is **not** running as a Velopack-installed build (`UpdateManager.IsInstalled == false`) or in Debug. This covers the portable-zip and dev cases.
  - All exceptions (offline, rate-limited, feed parse) are logged via `TerminalLogger` and swallowed. Update failure must never block or crash startup.
- UI surface (per "notify + apply on restart"):
  - A subtle indicator bound to `UpdateReady`.
  - A **command-palette action**: "Restart to update to vX.Y.Z" → `ApplyAndRestartAsync()`.

### Layer 2 — Release CI (`release.yml`)

Add the Velopack steps **inside the existing `publish_aot` job's `win-x64` matrix leg**, guarded by `matrix.rid == 'win-x64'`, so they reuse the publish output already on disk. Linux/macOS legs and the win-x64 portable zip are untouched.

After the existing `Publish AOT` + `Archive bundle` steps, on the win-x64 (`windows-latest`) leg:

1. Install the `vpk` tool: `dotnet tool install -g vpk` (pinned version).
2. `vpk download github` — fetch the prior release's Velopack assets so deltas can be computed (tolerate "no prior release").
3. `vpk pack` the `artifacts/publish/win-x64` folder:
   - `--packId NovaTerminal` (stable, must not change across releases or update lineage breaks).
   - `--packVersion <tag-without-leading-v>` (e.g. `v0.3.1` → `0.3.1`).
   - `--mainExe NovaTerminal.exe`, icon from `Assets/nova_icon.ico`.
   - signing args injected via the seam (Layer 3).
4. Upload the produced `Setup.exe`, `RELEASES`, and `.nupkg` (full + delta) to the same GitHub Release via the existing `softprops/action-gh-release` step.

### Layer 3 — Signing seam (no-op today)

- A CI step resolves signing parameters from repository secrets (e.g. `WINDOWS_SIGN_*` / Azure Trusted Signing inputs).
- If the secrets are **present**, `vpk pack` receives the appropriate signing flags (`--signTemplate` for signtool, or `--azureTrustedSign*`), signing both the app payload and `Setup.exe`/`Update.exe`.
- If the secrets are **absent** (the case today), the step resolves to empty args and `vpk pack` packs **unsigned** — no failure. Dropping in a cert later means only populating secrets; no `release.yml` structural change.

## Versioning & feed

- Pack version derives from the release **tag** (strip leading `v`), keeping it consistent with `Directory.Build.props` `<Version>`.
- `--packId` is the fixed identity `NovaTerminal`; never change it.
- Feed: public GitHub Releases of this repository, consumed via Velopack `GithubSource`.
- Channel: stable only.

## Error handling

- Startup update check is fire-and-forget; all failures logged + swallowed.
- `IsInstalled == false` and Debug builds short-circuit before any network call.
- CI `vpk download github` tolerates the no-prior-release case (first Velopack release has no delta).

## Testing

- **Unit:** `UpdateService` state logic against a faked update-manager abstraction — `UpdateReady` toggles correctly when an update is present/absent; exceptions are swallowed and leave `UpdateReady = false`; guarded no-op when not installed / Debug. No network.
- **CI:** assert `vpk pack` produces the expected assets (`Setup.exe`, `RELEASES`, `.nupkg`).
- **Manual (acceptance):** one real install → launch → N→N+1 self-update on a Windows box. This is part of "done" per #91's acceptance criterion. Validated during the spike and re-confirmed on the first tagged release.

## Acceptance criteria

- [ ] Spike proves Velopack + AOT install/launch/self-update locally (or a fallback is chosen with evidence).
- [ ] App links Velopack, handles hook args first in `Main`, and checks for updates on startup without ever blocking/crashing startup.
- [ ] Command-palette action restarts and applies a staged update.
- [ ] On tag, CI produces `Setup.exe` + `RELEASES` + `.nupkg` for win-x64 and uploads them alongside the unchanged portable zips.
- [ ] Signing seam present and a no-op without secrets; documented how to enable.
- [ ] Manual N→N+1 auto-update verified on Windows.
