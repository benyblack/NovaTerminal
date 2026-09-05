# Contributing to NovaTerminal

Thank you for your interest in contributing to **NovaTerminal**.

NovaTerminal is a terminal emulator.  
That means **correctness, determinism, and stability** matter more than feature velocity.

This document explains how to contribute **successfully**.

---

## Start Here

New to the project? You do not need to read every design document before your
first PR. Get it building, pick something small, and we will help from there.

### 1. Prerequisites

- **.NET SDK** — the exact version is pinned in `global.json`. Let the SDK
  install it rather than pointing the build at a different band.
- **Rust toolchain** (`cargo`, `rustc`) — the PTY and SSH backends are native
  crates under `src/NovaTerminal.App/native/`.

### 2. Build and test

**Always use the wrapper scripts, never raw `dotnet build`:**

```bash
# Linux / macOS / Git Bash
scripts/build.sh build
scripts/build.sh test
```

```powershell
# Windows / PowerShell
scripts\build.ps1 build
scripts\build.ps1 test
```

The wrappers pass `-nodeReuse:false` and set `DOTNET_CLI_USE_MSBUILD_SERVER=0`.
Without those, MSBuild leaves daemons holding your stdout/stderr handles and the
build appears to hang forever — usually looking stuck in `BuildCliShim`. If that
happens to you, you called `dotnet` directly; run `dotnet build-server shutdown`
and retry through the wrapper.

To rehearse CI locally before pushing: `ci/run.sh` or `ci/run.ps1`. That does a
clean Release build, runs the whole test suite unfiltered, re-runs the replay
category on its own, checks formatting, and does an AOT publish. It does **not**
reproduce the three-OS matrix or the per-category job split, so a green local run
is strong evidence but not a guarantee.

### 3. Formatting

Formatting is a **blocking** CI check. Before pushing:

```bash
dotnet format whitespace          # fixes it
dotnet format whitespace --verify-no-changes   # what CI runs
```

Only the whitespace pass is gated — indentation, alignment, brace placement, line
breaks. The style and analyzer passes are **not** enforced yet (tracked in #108),
so do not run a bare `dotnet format`: it will also rewrite things this project has
not agreed on, and your diff becomes unreviewable.

`.editorconfig` defines the rules and `.gitattributes` pins line endings, so the
check gives the same answer on Windows and Linux. If your editor fights it, trust
`dotnet format` — that is what CI runs.

### 4. Pick something small

Issues labelled [`good first issue`][gfi] are scoped to be landable in an
evening and are deliberately kept away from the load-bearing parts of the VT
core. [`help wanted`][hw] issues are larger but still scoped.

If nothing there appeals, docs fixes, test coverage for an untested class, and
theme/recipe additions are always welcome and always reviewed.

[gfi]: https://github.com/benyblack/NovaTerminal/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22
[hw]: https://github.com/benyblack/NovaTerminal/issues?q=is%3Aissue+is%3Aopen+label%3A%22help+wanted%22

### 5. Ask

If you are unsure whether an approach fits, comment on the issue before writing
much code. That costs you a day of waiting and saves you a week of rework. We
would much rather answer a question early than reject a finished PR.

---

## The Shape of the Codebase

Bytes flow **Pty → VT → Rendering**. The two rules that explain most of the
layering: **VT never learns about the OS**, and **Rendering never interprets VT
semantics**.

| Project | Owns | Depends on |
|---|---|---|
| `NovaTerminal.VT` | The VT/ANSI state machine, screen + alternate buffers, scrollback, reflow, cell/grapheme/width semantics. **The source of truth** — everything else reads it. | *(leaf — BCL only)* |
| `NovaTerminal.Replay` | Replay format v2, recording, playback, the golden-master harness. | VT |
| `NovaTerminal.Pty` | Spawning the child process and delivering raw bytes. Does **not** parse VT. | Replay *(recording only)* |
| `NovaTerminal.Rendering` | Skia draw path, glyph atlas and caches, render metrics. Does **not** interpret VT. | VT |
| `NovaTerminal.Platform` | OS-specific plumbing: input routing, path mapping (WSL ↔ Windows), process abstraction, the whole SSH stack. | Pty |
| `NovaTerminal.CommandAssist` | Command Assist domain, ranking, history/snippet storage, shell integration, view-models. Contains **no** Avalonia — the App owns only the views. | *(leaf)* |
| `NovaTerminal.Backup` | `.novabackup` export/import, automatic snapshots, the category-to-path catalogue. Never touches secret storage. | *(leaf)* |
| `NovaTerminal.VtContract` | The machine-readable VT capability catalogue (`vt-capabilities.json`) and its schema validation. | *(leaf)* |
| `NovaTerminal.AgentHost.Contracts` | Wire contracts shared by the app and the MCP server. | *(leaf)* |
| `NovaTerminal.McpServer` | The opt-in MCP server that agents connect to. | AgentHost.Contracts, Backup, VtContract |
| `NovaTerminal.App` | Avalonia UI — window, tabs, panes, settings, themes, command palette, Command Assist views, Agent Output panel. Wires it all together. | Platform, VT, Rendering, Pty, Replay, CommandAssist, Backup, AgentHost.Contracts |
| `NovaTerminal.Cli` | Thin CLI entry point. | App |
| `NovaTerminal.Conformance` | Validates `docs/vt_coverage_matrix.md` and generates the conformance report. | VtContract |

`CommandAssist`, `Backup`, `VtContract` and `AgentHost.Contracts` are leaves with
zero project references. That is deliberate rather than incidental: it is what
lets `McpServer` share real code with the app while keeping its "does not
reference App/VT/Pty/Rendering" invariant true by construction, and the
architecture tests assert the empty reference list on each of them.

Test projects mirror the source ones. Two things worth knowing before you go
looking:

- The **buffer, reflow and replay suites live in `tests/NovaTerminal.App.Tests/`**,
  not in `NovaTerminal.VT.Tests/` — historical, and the reason VT's measured
  coverage looks lower than it is.
- **`tests/NovaTerminal.App.Tests` failures are blocking in CI**, even though the
  test step itself is `continue-on-error` — a separate allowlist gate reads the
  results and reds the job. See the CI section below before you assume a red
  App.Tests step is somebody else's flake.

The layering rules above are enforced as [NetArchTest] facts in
`tests/NovaTerminal.Architecture.Tests/` — if you cross a boundary, that suite
tells you before a reviewer does.

For the full invariant-by-invariant breakdown, see `docs/MODULE_OWNERSHIP.md`.
Read it when you are changing core behaviour; the table above is enough to find
your way around.

[NetArchTest]: https://github.com/BenMorris/NetArchTest

---

## Core Philosophy

> **Terminal correctness is enforced by automated tests, not discipline.**

If a change cannot be tested, it cannot be merged.

---

## How Much Ceremony Your Change Needs

The bar scales with blast radius. Both tiers require tests; they differ in how
much surrounding evidence a reviewer needs.

### Isolated changes — a unit test is enough

Docs, theme importers, Command Assist recipes, self-contained decoder or parser
helpers, added test coverage, build and tooling scripts.

Read the code around your change and the section of this document that applies.
That is enough.

### Core changes — the full checklist applies

Anything touching the VT parser, `TerminalBuffer`, reflow, the renderer, the PTY
layer, or threading and lifetime.

For these, read first:

- `README.internal.md` – engineering rules and intent
- `docs/ARCHITECTURE.md` – architectural boundaries
- `docs/MODULE_OWNERSHIP.md` – invariant ownership
- `docs/ROADMAP.md` – test-gated roadmap

These areas hold invariants that are not visible from a single file, and a
change that looks locally correct can break replay parity or a performance
contract. A PR here that does not engage with those documents will need another
round of review before it can be merged — so it is faster to read them first.

---

## What We Value

We value contributions that:

- improve VT / ANSI correctness
- fix edge cases in resize, reflow, or alternate screen handling
- improve deterministic replay and test coverage
- reduce flicker or rendering instability
- improve cross-platform parity

We value **correctness over speed**, and **clarity over cleverness**.

---

## What Tends To Get Sent Back

These are the recurring reasons a PR needs another round. None of them are
character judgements — they are just the failure modes a terminal emulator has:

- features without tests
- OS-specific logic in the Terminal Core
- “fixing” rendering by changing semantics
- bypassing replay or parity tests
- “works on my machine” reasoning
- optimizing UI appearance at the expense of correctness

If you hit one of these, we will say which and why. Ask if it is not obvious.

---

## Contribution Types

### 1. Bug Fixes (Highly Welcome)

Bug fixes should include:

- a minimal reproduction
- a replay fixture if applicable
- a test that fails before the fix and passes after

If you cannot reproduce the bug deterministically, explain why.

---

### 2. Correctness Improvements

Examples:
- VT edge cases
- DEC private mode handling
- cursor state bugs
- scrollback isolation issues

These almost always require:
- replay tests
- buffer snapshot assertions

---

### 3. Performance Improvements

Performance work must:
- preserve terminal semantics
- include renderer metrics tests
- demonstrate no regression in correctness

Performance PRs without metrics will be rejected.

---

### 4. Feature Work

Feature work is welcome **only if it does not violate roadmap gates**.

Before starting feature work:
- check `docs/ROADMAP.md`
- ensure Phase −1 and Phase 0 gates are respected

Features that bypass correctness phases will be rejected.

---

## Architectural Rules (Non-Negotiable)

These are enforced by `tests/NovaTerminal.Architecture.Tests/`, so you will get
told before a reviewer has to.

### Terminal Core — `NovaTerminal.VT`
- Must remain OS-agnostic
- Must be deterministic
- Must not depend on UI or rendering

*(“Terminal Core” means the VT engine. There is no `NovaTerminal.Core` project —
it was renamed to `NovaTerminal.Platform` in #76 and holds OS plumbing, not the
engine.)*

### Renderer — `NovaTerminal.Rendering`
- Must not interpret VT semantics
- Must not “fix” buffer issues
- Must use incremental (cell-diff) rendering

### PTY Layer — `NovaTerminal.Pty`
- Must not parse VT
- Must deliver raw bytes
- Must be bounded and non-blocking

---

## Tests Are Mandatory

### Required Test Coverage

Depending on what you touched, add or update:

- **unit tests** — always
- **replay tests** (`Category=Replay`) — VT semantics, buffer state, reflow
- **cross-platform parity tests** — anything with an OS-specific code path
- **renderer metrics tests** (`Category=RenderMetrics`) — draw path, caches,
  invalidation

Not sure which apply? Add the unit test, open the PR, and ask. Working out the
right coverage together is a normal part of review here.

### Test Categories

Suites are tagged with an xUnit `Category` trait so CI can split the slow ones
into their own jobs. **The tags do not filter anything by default** — a bare
`scripts/build.sh test` applies no `--filter` and runs every category, which is
slower locally but means you are not accidentally skipping anything:

```bash
scripts/build.sh test                              # everything, no filter
scripts/build.sh test --filter Category=Replay     # just one category
scripts/build.sh test tests/NovaTerminal.VT.Tests  # just one project — fastest inner loop

# What CI's gating job actually runs (the heavy categories are excluded there
# because they each get a dedicated job):
scripts/build.sh test --filter \
  "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng"
```

| Category | Guards | Add or run one when |
|---|---|---|
| *(untagged)* | The gating lane — the deterministic majority. Must pass. | Always. |
| `Replay` | Byte stream → buffer state is a pure, stable function. | You change VT semantics, buffer state, or reflow. |
| `RenderMetrics` | Frame time, cache hit rates, invalidation counts. | You touch the draw path, glyph/row caches, or invalidation. |
| `GoldenSharedPng` | Pixel output of the shared renderer. | You change glyph rasterisation or layout. |
| `GoldenFontPng` | OS/font-specific rasterisation. Opt-in via `ENABLE_FONT_GOLDENS=1`; skipped otherwise. | Font-stack work. |
| `PtySmoke` | Real shell spawn, byte fidelity, teardown. | You touch PTY spawn, read loops, or process lifetime. |
| `Performance`, `Latency` | Throughput and input-to-frame latency. | Any perf change — **performance PRs without metrics are sent back.** |
| `Stress` | Sustained load, leaks. | Lifetime or threading work. |
| `ShellIntegration` | OSC 133 / OSC 7 shell markers. | Shell-integration changes. |
| `Regression` | Previously-fixed bugs, pinned so they stay fixed. | You fix a bug — pin it here. |

`Lane` is a second, separate trait, and today it has one value: `Lane=PlatformBoot`
marks the App.Tests that boot the Avalonia headless platform themselves (via
`SnapshotService.EnsureAvaloniaInitialized`). CI runs them in their own process,
and that split is a bug fix rather than tidiness — the boot binds a thread-affine
`MediaContext` into the process-global `AvaloniaLocator` root, so sharing a process
makes every later `[AvaloniaFact]` throw "a different thread owns it". If you add
a test that boots the platform, tag it `Lane=PlatformBoot`; if you run App.Tests
locally, run the two filters the way CI does:

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "Lane!=PlatformBoot"
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "Lane=PlatformBoot"
```

Three traps worth knowing:

- **Real-shell PTY tests share one xUnit collection.** Adding a new one outside
  that collection reintroduces a thread-starvation hang. Follow the existing
  pattern.
- **A build that fails reads as zero warnings.** Never take a diagnostic or
  coverage number from a red build.
- **Run App.Tests with `--blame-hang-timeout 5m`**, and never two runs at once
  against the same results directory — concurrent runs corrupt the totals, and
  without the blame timeout an #81 teardown hang looks like an idle terminal.

### Changing VT Coverage

If you add, change, or fix a VT sequence, the coverage matrix and the embedded
report must move with it — otherwise the **VT Conformance** check goes red:

1. Update the row (or add one) in `docs/vt_coverage_matrix.md`.
2. Regenerate the report that ships in the app:

   ```bash
   scripts/build.sh run --project src/NovaTerminal.Conformance -- \
     --validate --report src/NovaTerminal.App/Resources/vt-conformance-report.json
   ```

3. Commit the regenerated JSON alongside your change.

CI verifies both halves: `--validate` fails on matrix errors, and
`--check-report` fails if the committed JSON has drifted from the matrix. Run
the command above from the repo root, or pass `--repo-root`.

### Hard Rule

> If a change is not covered by tests, it will not be merged.

---

## How to Submit a PR

1. Fork the repository
2. Create a focused branch
3. Make small, reviewable commits
4. Add or update tests
5. Run all tests locally
6. Open a PR with a clear description

---

## PR Checklist (Required)

Your PR description should answer:

- What invariant does this change affect?
- Which module owns that invariant?
- What tests cover the change?
- Does this affect cross-platform behavior?
- Does this change renderer metrics?

“Not applicable” is a valid answer for a docs or tooling change — say so rather
than leaving it blank. If information is missing we will ask for it in review;
providing it up front just gets you a faster first response.

---

## CI & Review Process

All PRs are automatically checked by CI:

- formatting (`dotnet format whitespace`, Windows + Linux — blocking). The
  cheapest check to fail and the cheapest to fix: run `dotnet format whitespace`
  and commit the result.
- unit tests (VT, Rendering, Architecture, Platform, McpServer — blocking)
- headless App.Tests, in two lanes (`Lane!=PlatformBoot` and `Lane=PlatformBoot`)
  — **failures are blocking**. Both test steps carry `continue-on-error`, but
  that tolerates the *hang* only. That hang was recorded here for months as an
  upstream Avalonia.Headless deadlock (AvaloniaUI/Avalonia#21467, tracked as
  #81) that no in-repo change could address; four dumps say otherwise. It was
  ours: a Command Assist pass outliving its test read Avalonia's mutable
  `Dispatcher.UIThread` static from a threadpool thread and became the UI
  thread, and the next test's throw inside `EnsureIsolatedApplication()` — which
  Avalonia does not guard — unwound the single dispatcher loop the whole
  assembly shares, so every remaining test blocked forever and the `.trx`
  reported nothing. PR #416 fixed it; #417 removed the leaked panes feeding the
  same contamination. The tolerance stays until run data says the hang is gone,
  because a handful of green runs is not that evidence. The *results* are then
  gated by the following step,
  **Check App.Tests failures against the flake allowlist**, which fails the job
  for any failing test not named in `tests/app-tests-known-flaky.txt`. A missing
  `.trx` is the hang and only warns, so the step also prints how many tests
  executed — a truncated run is visible rather than silently green.
- renderer metric thresholds (`tab_perf_smoke`)
- golden shared PNG tests

That gate exists because "non-blocking" quietly became "unread": the job
reported success for weeks while 16 tests failed on Windows and 14 on Linux. If
you genuinely hit a flake, add it to the allowlist in the same PR with a comment
saying why — do not widen the filter.

Replay and cross-platform parity suites run on every push to `main` and on
the daily scheduled run (see `docs/plans/2026-04-22-ci-rebalance-and-release-publishing.md`);
a parity or replay break detected there must be fixed or reverted before the
next release. Release tags additionally run the gating unit lane on all three
OSes before any bundle is published.

Failing blocking CI blocks merge. The only tolerated failure anywhere is the
#81 hang, and the heavy categories (`Replay`, `RenderMetrics`, `PtySmoke`,
`Stress`, `GoldenSharedPng`) run in their own jobs or on `main` rather than on
every PR — run the ones your change touches locally.

Maintainers may request:
- additional replay fixtures
- stricter assertions
- reduced scope

---

## Coding Style

- Prefer clarity over cleverness
- Avoid premature optimization
- Keep methods small and explicit
- Comment *why*, not *what*

Mechanical formatting is not a matter of taste here — `.editorconfig` decides it
and CI enforces it. Run `dotnet format whitespace` and move on; there is nothing
to argue about and no reviewer will ask you to hand-align anything.

---

## Communication

If you are unsure:
- open an issue first
- describe the problem and approach
- ask before implementing large changes

Questions are welcome at any stage, including “is this issue still open and can I
take it?” and “I got the build working but I cannot find where X lives.”

The standards in this document are about the code, not about you. A first PR
that needs three rounds of review is a normal first PR.

---

## Final Reminder

NovaTerminal is a terminal emulator.

Users may forgive missing features.  
They will not forgive broken behavior.

> **Correctness first. Always.**

Thank you for helping make NovaTerminal better.
