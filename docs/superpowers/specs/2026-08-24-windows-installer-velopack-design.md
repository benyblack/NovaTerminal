# Windows Installer + Auto-Update (Velopack)

**Date:** 2026-08-24
**Status:** Design approved, ready for implementation plan
**Tracking issue:** [#91](https://github.com/benyblack/NovaTerminal/issues/91) — Windows installer + auto-update (Velopack) + code signing (post-0.3)
**Components:** `.github/workflows/release.yml`, `src/NovaTerminal.App` (`Program`, `MainWindow`, `SettingsWindow`, `Shell/TerminalSettings`, new `Update/`), `Directory.Packages.props`

## Summary

Ship a Windows installer as a GitHub release asset, and give installed builds a
background update path: check GitHub on startup, download quietly, apply on the user's
next restart. Velopack does the packaging (`Setup.exe`, full/delta `.nupkg`, update
feed index) and the in-app update mechanics.

The portable story does not change. `NovaTerminal-win-x64-vX.Y.Z.zip` keeps its name
and contents, so `packaging/winget/` continues to work untouched and anyone scripting
that URL is unaffected.

**Not in scope:** Authenticode code signing — the larger half of #91 and the real
SmartScreen fix — stays open. This installer ships unsigned and will still trip
SmartScreen on first run. Also out: macOS/Linux Velopack, `win-arm64`, `nova` on PATH,
file associations.

## Background — what exists today

- **Releases are three zips.** `release.yml`'s `publish_aot` job publishes
  self-contained NativeAOT bundles for `win-x64`, `linux-x64` and `osx-arm64`
  (`-p:PublishAot=true -p:SkipCliShim=true`), then `Compress-Archive`s each into
  `NovaTerminal-<rid>-<tag>.zip` and attaches it with `softprops/action-gh-release`.
  There is no installer of any kind.
- **winget packages the zip as portable.** `packaging/winget/` uses
  `InstallerType: zip` + `NestedInstallerType: portable` precisely so that onboarding
  needs no code-signing certificate. Its `InstallerUrl` points at the release zip and
  its `InstallerSha256` pins that exact asset.
- **The app is NativeAOT.** `NovaTerminal.App.csproj` sets `PublishAot=true`, and
  `ci.yml` has an `aot_publish` job that gates it.
- **`Main` already multiplexes CLI modes.** `Program.Main`
  (`src/NovaTerminal.App/Program.cs`) dispatches `VtReportCommand`,
  `SshAskPassCommand` and `ReplayCommand` before starting Avalonia, because the AOT
  bundle ships no separate CLI.
- **A toast pattern already exists.** `RecordingToast` in `MainWindow.axaml` — a
  bordered panel with title, message, a close button and action buttons, driven by
  `_recordingToastTimer` in `MainWindow.axaml.cs` — is the precedent for transient
  in-window notices.
- **`SetupCommandPalette()` is lazy.** It runs on palette-open and settings-save, not
  at startup (`MainWindow.axaml.cs:2207` comments this explicitly).
- **Version is centralized and static.** `Directory.Build.props` hardcodes
  `0.4.0` / `0.4.0.0`; release versions come from the pushed tag.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Packaging tool | Velopack (`vpk`) | Wraps the existing `dotnet publish` output rather than replacing it; brings installer, delta updates, Start Menu entry and Add/Remove Programs in one step. Matches #91. |
| Update UX | Background check → download → apply on restart | No modal, no surprise restart, nothing on the cold-start path. |
| Asset layout | Keep today's zip, add installer + feed | Zero churn for `packaging/winget/` and for existing download URLs. |
| Update channel | Stable tags only (`prerelease: false`) | Prerelease tags must not push users onto unfinished builds. |
| Install scope | Velopack default (per-user, `%LocalAppData%`) | No elevation, no UAC prompt, no certificate needed. |
| Signing | Deferred | Left on #91; an unsigned installer does not worsen the status quo (the zip's exe is unsigned today). |

## Packaging: changes to `release.yml`

All changes are confined to the `win-x64` leg of `publish_aot`, after the existing
`Archive bundle` step. The other two RIDs are untouched.

1. **Version the build from the tag.** Pass the tag-derived version into the publish
   (`-p:Version=` / `-p:InformationalVersion=`) so the installed executable reports the
   same version the feed advertises. See "Version consistency" below — this is the
   easiest thing in the whole design to get wrong.
2. **Install `vpk`** as a .NET global tool, **version-pinned**. An unpinned tool means
   release output that changes without a commit.
3. **Fetch prior releases for the delta**: `vpk download github` into the Velopack
   output directory, authenticated with the workflow token. Velopack computes a delta
   only when the previous full `.nupkg` is present locally. The first run finds nothing
   and produces full-only output; that is expected and must not fail the job.
4. **Pack**: `vpk pack` over `artifacts/publish/win-x64` with `--packId NovaTerminal`,
   the tag-derived `--packVersion`, `--mainExe NovaTerminal.exe`, and
   `--icon src/NovaTerminal.App/Assets/nova_icon.ico`. Velopack's own portable zip is
   suppressed so the releases page does not carry two near-identical zips (confirm the
   exact flag against `vpk pack --help` during implementation; if there is none, simply
   do not upload that file).
5. **Upload** through the existing `softprops/action-gh-release` step:
   - `Setup.exe` — renamed to `NovaTerminal-Setup-win-x64-<tag>.exe` for a releases page
     that reads clearly. The name is free; nothing resolves the installer by name.
   - `*-full.nupkg`, `*-delta.nupkg`, `releases.win.json` — **names exactly as produced**.
     `GithubSource` locates these by name on the latest release; renaming them breaks
     every client's update check.

This makes a release job read the *previous* tag's assets. If that fetch fails the job
must degrade to full-only output rather than failing the release.

## App integration

A single `Velopack` `PackageReference` in `NovaTerminal.App`, version pinned in
`Directory.Packages.props` (the repo uses central package management). An architecture
test pins the reference to that one project.

### Startup hook

`VelopackApp.Build().Run()` becomes the **first statement in `Main`** — before the
`VtReportCommand` / `SshAskPassCommand` / `ReplayCommand` dispatch. This ordering is
load-bearing, not stylistic: Velopack re-invokes the executable with its own hook
arguments during install, update and uninstall, and our CLI dispatch must never see or
swallow them.

### `UpdateService`

A UI-free seam in a new `src/NovaTerminal.App/Update/`, behind an interface so it is
testable without a window and without network:

- `Task<UpdateCheckResult> CheckAsync(CancellationToken)` — wraps `UpdateManager` over
  `GithubSource(repoUrl, accessToken: null, prerelease: false)`, then
  `CheckForUpdatesAsync` and, when something is found, `DownloadUpdatesAsync`.
- `void ApplyAndRestart()` — `ApplyUpdatesAndRestart`, which hands off to the bundled
  `Update.exe`.
- **Disabled when the app is not a Velopack install** (`UpdateManager.IsInstalled` is
  false). Portable-zip, winget and dev runs must never show update UI or log errors
  about a missing install.

### Wiring and UI

- The check runs on a background task **after** the window is up and startup metrics
  are recorded — 10 seconds after first window activation, once per process launch.
  Nothing about updates may touch the cold-start path; `StartupPerformanceTracker`
  exists because that path is measured.
- On a downloaded update: a **persistent** toast — "NovaTerminal vX.Y.Z is ready" with
  a **Restart now** action and a close button. This is a *new* control modeled on
  `RecordingToast`, not a reuse of that named panel: the two notices can be live at the
  same time and must not contend for one surface. It does not auto-dismiss on a timer
  the way the recording toast does; dismissing it leaves the update staged.
- A command-palette entry for both halves: check on demand, and restart-to-apply once
  staged. `SetupCommandPalette()` is lazy, so the entry's enablement must be computed
  when the palette is built rather than latched at startup.
- New `TerminalSettings.AutomaticUpdateChecks` (default `true`) with a toggle row in
  `SettingsWindow` beside the notification rows. The manual palette check works
  regardless of the toggle.

### Failure handling

Every check and download failure is logged through `TerminalLogger` and swallowed. An
unreachable GitHub, a rate limit or a malformed feed must never produce a dialog or a
degraded startup. The **only** case that reports failure to the user is a check they
asked for explicitly from the palette.

## The NativeAOT risk, and the fallback ladder

Velopack's documentation does not mention NativeAOT, and this app publishes with
`PublishAot=true`. This is the one genuine unknown in the design, so **the first
implementation task is a spike**, before any UI work: add the package reference,
`dotnet publish -r win-x64 -p:PublishAot=true`, inspect for IL2026/IL3050 trim and
reflection warnings, and exercise a real check against a scratch release.

Fallbacks, in order:

1. Confirm the SDK path we use is already reflection-free and the warnings are absent
   or benign.
2. Confine the calls behind a source-generated JSON path.
3. If the in-app SDK is genuinely AOT-incompatible: ship `Setup.exe` from `vpk pack`
   anyway — the packaging step needs no in-app SDK at all — and drive update checks by
   invoking the bundled `Update.exe` CLI instead of the C# API. The installer, the
   Start Menu entry and the update *mechanism* all survive; only the in-process API
   goes away.
4. If none of those hold, stop and report rather than improvising a fourth option.

## Testing

**Unit** (in `App.Tests`, against a fake update source through the `UpdateService`
interface):

| Case | Expectation |
|---|---|
| Not a Velopack install | Service reports disabled; no check attempted |
| Check returns no update | No toast, no palette state change |
| Source throws | Logged, swallowed, no UI |
| Update found | Downloaded, toast state set, restart action available |
| `AutomaticUpdateChecks` off | No automatic check; manual check still runs |

`App.Tests` also runs on ubuntu in CI, so none of these may require Windows or a real
Velopack install — the seam exists partly for that reason.

Worth knowing where these land: `App.Tests` is *not* in the gating unit loop that
`release.yml` and `ci.yml` run (`VT`, `Rendering`, `Architecture`, `Platform`,
`McpServer`), so a regression here will not block a release on its own. `UpdateService`
lives in `NovaTerminal.App` and only `App.Tests` can reach it, so this is the correct
home regardless — but the release safety net for this feature is the manual end-to-end
check below, not CI.

**Architecture:** a test pinning the `Velopack` reference to `NovaTerminal.App`.

No new test project, so `ci.yml`'s build-artifact path list and unit loop are untouched.

**End-to-end is manual and unavoidable:** install `Setup.exe` for vN, publish vN+1,
confirm the toast appears, restart, confirm the running version bumped and the delta
(not the full package) was used.

## Version consistency

`Directory.Build.props` hardcodes `0.4.0` while release versions come from the tag. If
the publish is not given the tag-derived version, an installed build reports `0.4.0`
forever: the first release after any drift looks like an available update to every
client, permanently. The tag must drive both `vpk --packVersion` and the publish's
`Version` properties, and they must agree.

## Open questions / follow-ups

- **Code signing** — remains #91's core. Until it lands, both the zip's exe and the new
  installer trip SmartScreen.
- **`win-arm64`** — add an installer entry when the release workflow starts producing
  that bundle (the winget README already notes the same gap).
- **macOS / Linux** — Velopack covers both; deliberately not attempted here.
