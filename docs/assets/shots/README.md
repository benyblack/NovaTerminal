# Marketing screenshots & clips — generated assets

Everything in this directory (except `hero/`, see below) is **generated**, not hand-captured.
`tools/NovaTerminal.Shots` boots NovaTerminal's real `MainWindow` headless-with-Skia against an
isolated fake workspace (`DemoWorld`), drives each scenario, and rasterizes the result. Nothing
here should be edited by hand — regenerate it instead:

```bash
scripts/shots.ps1 all --scale 2 --publish
```

or, inside Claude Code, run the `/shots` command, which builds, reviews every image against its
scenario's stated `Intent` before publishing, and reports what changed.

## Where things live

- **This directory** — the *published* variants: PNGs (and, for 16 of the 28 variants, a
  smaller lossless WebP sibling — only emitted where it actually beats the PNG) sized for their
  destination:
  - `<name>-readme.png[.webp]` — 1280px wide, for `README.md`.
  - `<name>-site.png[.webp]` — 2400px wide, for the marketing site (`site/public/shots/`).
  - `og-card.png` (1200×630) and `social-square.png` (1080×1080) — social-preview cards, derived
    from `hero-single` only.
  - `clip-*.gif` / `clip-*.webm` — short looping clips.
- **`artifacts/shots/`** (gitignored, not in this repo) — the working directory: Tier 1/2
  masters at full capture resolution, individual clip frames, and `shots.json`, the manifest a
  run writes there. Each entry records the scenario name, file, dimensions, the **git commit**
  and **OS** the capture ran on, and a UTC timestamp — so for any published image you can trace
  back to exactly which build and platform produced it. The masters are not committed; only the
  publish step's derived, checked variants above are.
- **`hero/`** — the two exceptions. `hero-real-single` and `hero-real-split` are captured from
  the **real on-screen window** (drop shadow, rounded corners, acrylic blur — a headless
  `RenderTargetBitmap` can't produce those), by hand, with `scripts/capture-hero.ps1`. See
  `hero/README.md` for the capture procedure and leak-check discipline. Never put a headless
  render in that folder, and never put a hand-captured window screenshot anywhere else in this
  directory.

## Scenarios you won't find here

The harness's `ScenarioCatalog` deliberately does not register four scenarios, so they produce no
assets — if you're looking for the sixel or iTerm2 inline-image screenshot, or a connection
manager / remote files shot, this is why there isn't one:

- **`sixel-graphics`**, **`iterm2-inline-image`** — implemented and testable, but no production
  code under `src/` wires an actual image decoder onto `AnsiParser` yet (it recognizes both
  protocols but decodes neither). Screenshotting them would demonstrate a capability the shipped
  app doesn't have.
- **`connection-manager`**, **`remote-files`** — the SSH profile store bypasses this harness's
  config sandbox and reads/writes the developer's real, unsandboxed profile file; `remote-files`
  additionally needs a live, connected SSH session the offline harness has no way to provide.

Each scenario's own source file under `tools/NovaTerminal.Shots/Scenarios/` documents the
evidence trail in detail, and `ScenarioCatalog.cs` explains what re-enabling one would require.

## If you're adding or changing a scenario

Look at an existing `IScenario` implementation and its scenario file's header comment for the
pattern (a stated `Intent`, the settle/wait discipline, and — where relevant — the leak checks
`DemoWorld` performs). Never commit an image you have not looked at against its `Intent`, and
never let a real username, hostname, path, or branch name appear in a published asset.
