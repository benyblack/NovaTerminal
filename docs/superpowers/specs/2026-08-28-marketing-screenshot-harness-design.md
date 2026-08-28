# Marketing Screenshot & Clip Harness — Design

**Date:** 2026-08-28
**Status:** Approved (brainstorming complete)

## Problem

README, the Astro site under `site/`, and social posts all need images of NovaTerminal.
Today they are hand-taken and pasted as GitHub user-attachment URLs (see the five
`user-attachments` images at the top of `README.md`). That has four costs:

1. **Not reproducible.** Nobody can regenerate the current README images. A UI change
   silently ages them; there is no way to tell which release a shot came from.
2. **Inconsistent.** Each shot was taken from a different machine state — different
   prompt, different cwd, different theme — so the set does not read as one product.
3. **Leaky.** Ad-hoc captures carry the developer's real username, hostname, paths, and
   branch names into public marketing material.
4. **Incomplete.** The differentiating surfaces — agent access, the MCP-driven session,
   the activity journal — are not pictured at all, and motion (palette opening, an agent
   typing) cannot be shown as a still.

## Solution overview

A standalone capture tool, `tools/NovaTerminal.Shots`, that boots the real `MainWindow`
in Avalonia's headless-with-Skia mode, points it at an isolated demo profile, spawns
**live PTY sessions** into a scripted demo world, drives each scenario through real
keyboard input, and rasterizes the window to PNG. A frame-sequence mode feeds ffmpeg for
short clips. A separate `scripts/capture-hero.ps1` grabs the real on-screen window (with
OS shadow and rounded corners) for the two or three hero shots where true native chrome
matters.

Claude is the operator: a `/shots` slash command runs the harness, **looks at every
generated image**, and re-runs individual scenarios whose output is wrong.

### Why headless-with-Skia rather than desktop capture

`tests/NovaTerminal.App.Tests/TestAppBuilder.cs` already runs the app with
`UseHeadlessDrawing = false`, routing drawing through real Skia, and
`CommandAssistOverlayContentRenderTests` already proves a live control rasterizes to real
pixels via `RenderTargetBitmap`. `MainWindow.axaml` sets
`ExtendClientAreaToDecorationsHint="True"`, so the app draws its own title bar and tab
strip, and every overlay worth photographing — `SearchPanel`, `CommandPaletteOverlay`,
`ConnectionOverlay`, `TransferOverlay`, and the pane-level `CommandAssistOverlayHost` —
is an in-window element, not an OS popup. An offscreen render of the window therefore
contains essentially the entire visual identity. What it cannot contain is the OS drop
shadow, rounded corners, and acrylic blur; those are added in post-processing for the
bulk shots and captured for real by the hero script.

### Why a separate project rather than a test lane

Real-shell PTY sessions under the headless xUnit dispatcher are the known trigger for the
#81 deadlock — leaked `RustPtySession` loops on the thread pool, plus a sync-blocking
test runner starving the dispatcher at low pool minimums. A standalone console process
owns its own threading, sets its own `ThreadPool` minimums, and shuts sessions down
deterministically. It does not inherit the constraint that caused #81, and a screenshot
run can never destabilise the gating test lane.

## Non-goals

- Not a golden-image regression test. `SnapshotService` and the golden-PNG lane already
  own pixel regressions; these images are marketing artifacts and are allowed to change.
- Not a replacement for `TerminalSnapshotRenderer`, which renders pane content only (no
  chrome, no tabs, no overlays) and stays the agent-host `capture_screen` path.
- No cross-OS parity requirement. Windows is the primary capture OS — it is the platform
  with an installer and the one most README readers will run. The harness must also run
  on Linux, but a Linux capture is published only when a shot needs it (a protocol or TUI
  that behaves better there), and `shots.json` records which OS produced each asset.
- No CI job that regenerates assets automatically. Regeneration is deliberate and
  reviewed, because the output is public-facing.

## The demo world

Every shot shares one fictional identity, which is what makes sixteen captures read as
one product.

**Identity.** The prompt renders as `nova@demo ~/projects/nova-demo (feat/sixel-decoder) $`.
The user is `nova`, the host is `demo`, the project is `nova-demo`.

**Isolation.** The harness sets `NOVATERM_APPDATA_ROOT` — already honoured by
`AppPaths.RootDirectory` — to a scratch profile directory it seeds itself. This is
load-bearing: `MainWindow`'s constructor calls `TerminalSettings.Load()`, so without the
override the developer's live settings (themes, fonts, profiles, tab orientation) would
leak into published marketing images. That is the same root cause recorded for #357 and
for the post-#356 title-bar failures.

The seeded profile contains:

- `settings.json` — pinned `ThemeName`, `FontFamily`, `FontSize`, tab orientation, the
  agent-access toggles the agent scenarios need, and a `TerminalProfile` named `Demo`
  whose `Command` launches the platform shell with profile loading disabled
  (`bash --noprofile --norc` on Linux, `pwsh -NoProfile -NoLogo` on Windows) so the
  developer's dotfiles cannot alter the prompt or colours.
- `themes/` — the five built-in themes copied in, so `themes-grid` can switch themes
  without touching the real profile.

**Workspace.** A temp directory seeded per run with a small git repo: a handful of source
files worth opening in an editor, a scripted commit history with plausible subjects on
`feat/sixel-decoder`, and a fixed committer identity and dates so `git log --graph`
renders the same story every time. Deleted at the end of the run.

**Environment.** `HOME`/`USERPROFILE`, `CWD`, the prompt variable or function, `TERM`,
and locale are all set explicitly. Nothing is inherited that could appear in a pixel.

## Shot catalogue

Captured at a logical 1280×800 unless noted, with `RenderScaling = 2` — masters are
2560×1600.

### Tier 1 — hero

| Name | Content |
|---|---|
| `hero-split` | Three panes: colorized build/test stream running left, `git log --graph --oneline` top-right, live process monitor bottom-right. Dracula. |
| `hero-single` | One pane: system-info banner, `git status`, a passing test run. Also the source image for social variants. |
| `tabs-vertical` | Vertical tab sidebar, five named tabs, one carrying an agent-activity indicator. |
| `command-palette` | `CommandPaletteOverlay` open, query typed, results filtered with shortcut hints. |
| `themes-grid` | `hero-single` content in all five built-in themes, composited 2×3. Derived from five captures, not itself a capture. |

### Tier 2 — features

`search-overlay` · `sixel-graphics` · `iterm2-inline-image` · `tui-vim` · `tui-htop` ·
`settings-appearance` · `settings-agent-access` · `connection-manager` · `remote-files` ·
`command-assist` · `agent-session`

`settings-agent-access` and `agent-session` are the priority pair: they carry the story no
competing terminal's README tells. `settings-agent-access` shows the observe and act
toggles as separate opt-ins with act off; `agent-session` shows a pane with the agent
indicator lit and the activity journal listing calls.

**How the agent shots get an agent.** No external MCP client is launched. The seeded
`settings.json` enables observe and act, and the harness drives the in-process agent-host
path directly — registering a session and issuing the same `send_input` calls
`NovaTerminal.McpServer` would issue — so the indicator lights and the journal fills
through the production code path rather than through mocked UI state. An image that shows
the journal must be showing real journal entries; anything else would be a staged
screenshot of a security feature, which is precisely the thing not to fake.

### Tier 3 — derived variants

Generated by post-processing from Tier 1 and Tier 2 masters, never re-captured: 1200×630
OG card, 1080×1080 social square, README width, site hero width. Each gets rounded
corners, a drop shadow, and a branded backdrop.

### Tier 4 — clips

| Name | Length | Content |
|---|---|---|
| `clip-agent` | ~5s | An MCP agent types into a live session while the activity journal ticks. The lead clip. |
| `clip-palette` | ~3s | Palette opens, query typed, command executes. |
| `clip-split` | ~4s | Pane splits, then broadcast input reaches both panes. |
| `clip-tui` | ~3s | A full-screen TUI redrawing. |

## Architecture

```
tools/NovaTerminal.Shots/
  Program.cs            CLI: shots [all|<name>...] [--out DIR] [--scale N] [--no-clips]
  ShotHost.cs           Avalonia bootstrap, window lifecycle, threading policy
  DemoWorld.cs          scratch profile + seeded git repo + env; disposal
  Driver.cs             input injection, settle policy, overlay helpers
  Rasterizer.cs         RenderTargetBitmap -> SKBitmap -> PNG
  FrameRecorder.cs      fixed-cadence frame sequence for clips
  PostProcess.cs        shadow, rounded corners, backdrop, variants, themes-grid
  Encoder.cs            ffmpeg invocation (WebM VP9 + GIF via palettegen/paletteuse)
  Scenarios/            one file per shot; each declares name, size, tier, and steps
  Manifest.cs           writes shots.json describing every produced asset
```

**ShotHost** configures `AppBuilder.Configure<App>().UseSkia().UseHeadless(new
AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })` — the same combination
`TestAppBuilder` uses — raises `ThreadPool` minimums before any session starts (the
mitigation recorded for #81), constructs `MainWindow` through
`AppServices.BuildForDesigner()`, shows it, and pumps the dispatcher.

**Driver** prefers real input: `window.KeyPress(Key, RawInputModifiers, PhysicalKey,
text)` from `Avalonia.Headless`, already used in `TerminalViewKeyHandlingTests` and
`TerminalPaneSshDisconnectTests`. Driving through the app's own key bindings means a
screenshot exercises the real user path, so a broken shortcut shows up as a wrong image
rather than a passing capture. Where no binding exists, the driver falls back to the
public `Window.FindControl<T>(name)` surface to set overlay visibility directly —
`CommandPaletteOverlay`, `SearchPanel`, `ConnectionOverlay`, and `TransferOverlay` are
all reachable by name. Only if a scenario needs something neither route reaches do we add
`InternalsVisibleTo("NovaTerminal.Shots")` to `src/NovaTerminal.App/AssemblyInfo.cs` and
the csproj; the goal is zero production changes, and that is the documented escape hatch.

**Settle policy.** Live PTY output is asynchronous, so every step waits on a condition
rather than a sleep: the pane's output stream must be quiet for N consecutive dispatcher
pumps, and where a scenario knows what it is waiting for (a prompt string, a row count, a
non-empty alt screen) it waits on that instead. A step that never settles fails the
scenario loudly with its name — a hung capture must not silently produce a half-drawn
frame.

**Rasterizer** renders the window with `RenderTargetBitmap` at the requested pixel size
and DPI, matching the pattern already proven in
`CommandAssistOverlayContentRenderTests.Rasterize`, and encodes via SkiaSharp.

**FrameRecorder** drives the same scenario steps while rasterizing at a fixed 20 fps into
a PNG sequence, then hands the directory to **Encoder**. ffmpeg 8.0 is present on PATH;
its absence degrades the run to stills-only with a warning rather than failing it.

**Manifest.** Each run writes `shots.json`: every asset's name, tier, pixel size, source
scenario, git commit, OS, and timestamp. This is what makes the set auditable — you can
always answer "which build produced this README image".

## Outputs

- **Masters** — `artifacts/shots/` (already gitignored). Full 2× PNGs, frame sequences,
  `shots.json`. Regenerable, never committed.
- **Published** — `docs/assets/shots/`. Committed, optimized, sized for use: README-width
  PNG plus WebP, site hero, OG card, social square, and the clips as WebM and GIF. README
  and `site/` reference these by relative path instead of GitHub attachment URLs.
- **Hero shots** — `docs/assets/shots/hero/`, captured by the PowerShell script and
  committed alongside a note recording which OS and build produced them.

## Hero capture script

`scripts/capture-hero.ps1` takes a window title or process id, resolves the window's
`DWMWA_EXTENDED_FRAME_BOUNDS`, and captures that rectangle so the real shadow and rounded
corners are included. You position and arrange the app; the script only captures. No
`SendKeys`, no foreground automation, no focus stealing — the parts that have proven
unreliable on this machine are exactly the parts left to a human. The script prints the
manual arrangement steps for each hero shot before capturing.

## Claude automation

A `/shots` slash command in `.claude/commands/shots.md` — the directory does not exist
yet — defines the loop:

1. Build the tool through `scripts/build.ps1`; never raw `dotnet build`.
2. Run the requested scenarios.
3. **Read every produced PNG**, which renders it visually, and judge each against the
   scenario's stated intent: is the pane actually full of content, is the overlay open,
   is text clipped, did the theme apply, is the frame half-drawn.
4. Re-run only the scenarios that failed judgement, adjusting the scenario's settle
   condition or content script.
5. Regenerate variants, refresh `docs/assets/shots/`, and report a summary table of what
   changed.

Visual inspection is the point. A capture harness cannot tell that a shot is boring, and
an assertion cannot tell that a prompt says `C:\Users\behna`. Claude looking at the image
closes both gaps.

## Risks

| Risk | Mitigation |
|---|---|
| Real-shell PTY deadlocks the dispatcher (#81) | Standalone process, raised `ThreadPool` minimums, dedicated PTY threads, explicit session shutdown, per-scenario watchdog that fails loudly. |
| Developer state leaks into public images | `NOVATERM_APPDATA_ROOT` scratch profile, seeded workspace, explicitly set env, shells launched without profile loading. Claude's visual review is the backstop. |
| Font differences change every shot between machines | Pin `FontFamily` and `FontSize` in the seeded settings; record OS and build in `shots.json`; publish each asset from one OS. |
| Committed images bloat the repo | Masters stay in gitignored `artifacts/`; only optimized, use-sized assets are committed. |
| Live PTY makes runs non-reproducible | Accepted deliberately: authenticity was chosen over determinism. The seeded workspace and fixed commit dates remove most of the variance; `shots.json` records the rest. |
| New project breaks solution-wide CI steps | `tools/NovaTerminal.Shots` is added to `NovaTerminal.sln` but is not a test project and must not be added to the unit-test loop or the test-artifact path list in `ci.yml`. Verify the build lane after adding it. |

## Testing

The harness is tooling, so the bar is "it cannot silently produce a bad asset", not full
coverage:

- A smoke scenario that boots the window, captures one frame, and asserts the PNG is
  non-uniform — the same ink-fraction guard `CommandAssistOverlayContentRenderTests` uses
  to catch the blank-raster failure mode.
- `DemoWorld` unit tests: the scratch profile is created under the override root, the
  seeded repo has the expected branch and history, and disposal removes everything.
- A guard asserting no published asset path escapes `docs/assets/shots/`.
- Manual review is the real gate, performed by Claude via `/shots` and by you on the PR.

## Phases

1. **Skeleton** — project, `ShotHost`, `DemoWorld`, `Rasterizer`, one scenario
   (`hero-single`), `scripts/shots.ps1`. Proves the whole path end to end.
2. **Agent story** — `settings-agent-access`, `agent-session`, `clip-agent`, plus
   `FrameRecorder` and `Encoder`. Built second on purpose: it is the differentiator, and
   building it early keeps the rest honest about supporting it.
3. **Tier 1 remainder** — `hero-split`, `tabs-vertical`, `command-palette`,
   `themes-grid`.
4. **Tier 2** — the remaining feature shots.
5. **Post-processing and variants** — shadow, corners, backdrop, OG/social/README sizes.
6. **Publish** — `/shots` command, `scripts/capture-hero.ps1`, README and `site/`
   switched from attachment URLs to committed assets.
