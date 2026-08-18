# Draft issues for newcomers — 2026-07-31

**Status: all eight resolved — this file is now a record, not a to-do.**

| Draft | Outcome |
|---|---|
| 1. Sixel HLS flat grey | filed as [#247](https://github.com/benyblack/NovaTerminal/issues/247) |
| 2. Theme importers untested | filed as [#248](https://github.com/benyblack/NovaTerminal/issues/248) |
| 3. Kitty theme importer | filed as [#249](https://github.com/benyblack/NovaTerminal/issues/249) |
| 4. Command Assist recipes | filed as [#250](https://github.com/benyblack/NovaTerminal/issues/250) |
| 5. Issue + PR templates | implemented in [#245](https://github.com/benyblack/NovaTerminal/pull/245) |
| 6. CONTRIBUTING on-ramp | implemented in [#245](https://github.com/benyblack/NovaTerminal/pull/245) |
| 7. `REP` (CSI Ps b) | filed as [#251](https://github.com/benyblack/NovaTerminal/issues/251) |
| 8. OSC 52 clipboard | filed as [#252](https://github.com/benyblack/NovaTerminal/issues/252) |

Labels `vt`, `rendering` and `theme` were created (grey `#ededed`, matching the
existing `ssh` / `ux` / `security` area labels).

This file is deliberately untracked, like `docs/ISSUE_TRIAGE_2026-07-27.md`.
Delete it whenever you like — the issue bodies on GitHub are the live copies.

**Why this exists.** Of the 17 open issues, none is newcomer-scoped. The
triage doc's own conclusion (`docs/ISSUE_TRIAGE_2026-07-27.md`, §"Next on the §7
slate") is "nothing quick remains": what is left is the perf trio (`#165`,
`#172`, `#173`), the `#110`–`#115` refactor cluster gated on `#112`,
warnings-as-errors (`#108`), and CI plumbing. The few items still marked effort
`S` (`#107`, `#109`, `#121`) are small only for someone who already knows the
codebase — `#109` needs a logging-abstraction decision and `#121` is native SSH
hardening. A newcomer who filters on `good first issue` today finds an empty
list, so the label cannot do its job.

Every claim below was verified against `main` at `8f2e416` + the working tree.
`file:line` references were read, not inferred.

**Labels already on the repo:** `good first issue`, `help wanted`, `bug`,
`enhancement`, `documentation`, `docs`, `chore`, `ux`, `security`,
`architecture`, `refactor`, `ci`, `hardening`, `threading`, `memory`, `P0`,
`connection-manager`, `ssh`.

**Labels worth adding first:** `vt`, `rendering`, `theme`. Three of the drafts
below want them; without those, area filtering for newcomers is coarse.

---

## Ordering: file 1–4 first

Issues 1–4 are genuinely evening-sized and have a test home already. Issues 5–6
are the onboarding scaffolding that makes 1–4 land smoothly. Issues 7–8 are
`help wanted`, not `good first issue` — they touch the VT core and need the full
review ceremony.

---

## 1. Sixel HLS (`Pu=1`) colours render as flat grey

**Labels:** `good first issue`, `bug`, `rendering`

### What's wrong

`SixelDecoder` handles the RGB colour introducer (`#Pc;2;Pr;Pg;Pb`) but stubs
out the HLS form (`#Pc;1;Ph;Pl;Ps`), assigning every HLS colour the same mid
grey:

```csharp
// src/NovaTerminal.Rendering/SixelDecoder.cs:99-103
else if (type == 1) // HLS (simplified conversion)
{
    // TODO: Full HLS to RGB conversion if needed
    _palette[idx] = new SixelColor(200, 200, 200);
}
```

Any sixel image whose palette is defined in HLS decodes as a flat grey
silhouette. `img2sixel` emits RGB, which is why this has gone unnoticed, but
HLS is part of the DEC sixel definition and other producers use it.

### What to change

Implement HLS → RGB in `SixelDecoder.cs` and use it for `type == 1`.

Two things to get right:

1. **DEC's hue origin is rotated relative to standard HSL.** In DEC sixel,
   0° = blue, 120° = red, 240° = green. So `hslHue = (decHue + 240) % 360`
   before applying a textbook HSL→RGB conversion. Verify against this anchor
   table rather than trusting the formula:

   | `#1;1;H;L;S` | Expected |
   |---|---|
   | `0;50;100` | blue |
   | `120;50;100` | red |
   | `240;50;100` | green |
   | any `H`, `L=0` | black |
   | any `H`, `L=100` | white |
   | any `H`, `S=0` | grey at `L` |

   `libsixel`'s `sixel_helper_hls_to_rgb` is the reference implementation if you
   want to cross-check.

2. **Clamp the inputs.** Sixel is remote-controlled input. The `type == 2`
   branch immediately above clamps `p1..p3` to `0..100` because the byte cast
   would otherwise wrap — the HLS branch needs the same treatment (hue `0..360`,
   lightness and saturation `0..100`). See the comments at
   `SixelDecoder.cs:73-76` and `:90` for the threat model the file already
   documents.

### How to test

Extend `tests/NovaTerminal.Rendering.Tests/SixelDecoderTests.cs`. Note the
existing `SkiaAvailable` guard convention in that file (`Assert.SkipUnless`) —
SkiaSharp is absent on the Linux gating runner. If you factor the conversion out
as a pure static method, you can test it without touching Skia at all, which is
the better shape.

A `[Theory]` over the anchor table above plus one round-trip
(`#1;1;120;50;100` produces the same colour as `#1;2;100;0;0`) is sufficient.

### Acceptance

- [ ] HLS palette entries decode to their spec'd RGB values
- [ ] Out-of-range `H`/`L`/`S` are clamped, not wrapped
- [ ] Anchor table covered by tests
- [ ] `TODO` comment removed

**Files:** `src/NovaTerminal.Rendering/SixelDecoder.cs`,
`tests/NovaTerminal.Rendering.Tests/SixelDecoderTests.cs`

---

## 2. Theme importers have no test coverage

**Labels:** `good first issue`, `theme`, `documentation`

### What's wrong

Three importers ship with zero tests:

- `src/NovaTerminal.App/Shell/ThemeImporters/AlacrittyImporter.cs` (`.toml`)
- `src/NovaTerminal.App/Shell/ThemeImporters/ITerm2Importer.cs` (`.itermcolors`)
- `src/NovaTerminal.App/Shell/ThemeImporters/WindowsTerminalImporter.cs` (`.json`)

There is no test file matching `*Import*` anywhere under `tests/`. Each importer
is a hand-rolled parser over untrusted third-party files, and `Import` swallows
exceptions into a `try`/`catch`, so a parse that silently produces a
default-coloured theme is indistinguishable from success.

This is the cheapest coverage win in the repo and it fits the project's stated
rule that behaviour is enforced by tests rather than discipline.

### What to change

Add `tests/NovaTerminal.App.Tests/Shell/ThemeImporterTests.cs` (namespace
`NovaTerminal.Tests.Shell`, matching the `NovaTerminal.Tests.CommandAssist`
convention in `tests/NovaTerminal.App.Tests/CommandAssist/`).

Cover, per importer:

- a well-formed file maps every slot correctly — `Foreground`, `Background`,
  `CursorColor`, and ANSI 0–15 (see `TerminalTheme` in
  `src/NovaTerminal.VT/TerminalTheme.cs` for the full surface)
- `Name` is derived as expected (e.g. `AlacrittyImporter` appends
  `" (Alacritty)"`)
- comments, blank lines and trailing `# ...` are ignored (Alacritty)
- a malformed colour value is skipped without throwing and without corrupting
  neighbouring slots
- a file that is not the expected format at all yields no theme (or a clearly
  default one) rather than an exception

Write fixtures to a temp directory in the test rather than committing sample
theme files, unless a fixture is large enough to warrant a file.

### Good to know

The headless `App.Tests` lane is **non-blocking** in CI (`continue-on-error`,
see `CONTRIBUTING.md`), so run this suite locally before pushing — a red result
will not turn the check red for you.

### Acceptance

- [ ] All three importers have happy-path and malformed-input tests
- [ ] Tests create their own fixtures; no dependency on developer machine state
- [ ] `scripts/build.ps1 test` (or `.sh`) passes locally

**Files:** `tests/NovaTerminal.App.Tests/Shell/ThemeImporterTests.cs` (new)

---

## 3. Add a Kitty theme importer

**Labels:** `good first issue`, `enhancement`, `theme`

### Context

NovaTerminal imports Windows Terminal, iTerm2 and Alacritty themes. Kitty's
`.conf` format is the other big one — the [kitty-themes] catalogue is where a
lot of people keep their colours, and it is the simplest of the four to parse.

[kitty-themes]: https://github.com/kovidgoyal/kitty-themes

### What to change

1. Add `src/NovaTerminal.App/Shell/ThemeImporters/KittyImporter.cs` implementing
   `IThemeImporter` (three members: `Name`, `Extension`, `Import`) — see
   `IThemeImporter.cs` and copy the shape of `AlacrittyImporter`, which is the
   closest analogue (line-oriented, `key value` pairs).
2. Register it in the importer list at `src/NovaTerminal.App/Shell/ThemeManager.cs:14-19`.

The format is flat `key value` lines, `#`-commented:

```conf
foreground #dddddd
background #000000
cursor #cccccc
selection_foreground #000000
selection_background #fffacd
color0  #000000
color1  #cc0403
...
color15 #ffffff
```

Map `color0`–`color7` to the normal ANSI slots and `color8`–`color15` to the
bright slots via `TerminalTheme.SetAnsiColor(index, bright, color)`. Kitty also
supports `include other.conf` — skipping those lines is fine for a first pass,
but say so in a comment so the limitation is visible.

`Extension` should be `".conf"`; no existing importer claims it.

### How to test

Add cases to the test file from issue #2 (or create it if that one is still
open — coordinate in the issue thread). Include a real theme from the
kitty-themes catalogue as a fixture string.

### Acceptance

- [ ] `KittyImporter` maps foreground/background/cursor and all 16 ANSI slots
- [ ] Registered in `ThemeManager`
- [ ] Comments, blank lines and unknown keys are ignored, not fatal
- [ ] Tests cover a real kitty theme and a malformed one

**Files:** `src/NovaTerminal.App/Shell/ThemeImporters/KittyImporter.cs` (new),
`src/NovaTerminal.App/Shell/ThemeManager.cs`

---

## 4. Grow the Command Assist recipe catalogue

**Labels:** `good first issue`, `enhancement`, `ux`

### Context

Command Assist suggests recipes for the command you are typing. The seed
catalogue currently holds **seven** entries, covering `git` (×2), `docker`,
`ls`, `grep`, `Get-ChildItem` and `Set-Location`:

`src/NovaTerminal.App/CommandAssist/Domain/SeedRecipeProvider.cs:12-22`

Common tools have nothing: `ssh`, `curl`, `tar`, `find`, `kubectl`, `rg`,
`systemctl`, `journalctl`, `dotnet`, `cargo`, and on the PowerShell side
`Get-Process`, `Select-String`, `Get-Content`, `Test-NetConnection`.

### What to change

Add entries to the `Recipes` collection. Each is one line:

```csharp
("ssh", new CommandHelpItem(
    "Forward a local port",
    "ssh -L 8080:localhost:80 user@host",
    "Tunnel a remote port to localhost.",
    "bash",
    ["Recipe"])),
```

Guidelines that keep this reviewable:

- **Correct and safe.** These are one keypress from execution. No destructive
  examples (`rm -rf`, `dd`, force-push), no commands that leak secrets to a
  shell history or a remote.
- **Portable, or tagged.** Set `ShellKind` to `"bash"` or `"pwsh"` honestly;
  `SeedRecipeProvider` sorts shell-matching recipes first. Do not put a
  GNU-only invocation under a generic name if BSD/macOS differs — pick the
  portable form or add both.
- **Teach something.** Prefer the flag combination people look up every time
  over the bare invocation.
- **Two or three per command**, not ten. This is a suggestion list, not a man
  page.

If you want a bigger bite, splitting the catalogue out of the `.cs` file into an
embedded JSON resource would be a welcome follow-up — but do that as a separate
PR, not mixed with content.

### How to test

Extend `tests/NovaTerminal.App.Tests/CommandAssist/SeedRecipeProviderTests.cs`.
Worth adding as invariants over the whole catalogue rather than per-recipe
assertions:

- every recipe has a non-empty `Title`, `Command` and description
- every `ShellKind` is one of the known values
- no two recipes for the same command token share a `Title`
- `pwsh` recipes rank above `bash` ones for a `pwsh` query (already covered by
  the ordering logic — pin it)

### Acceptance

- [ ] Recipes added for at least six commands that currently have none
- [ ] Catalogue-wide invariant tests added
- [ ] No destructive or secret-leaking examples

**Files:** `src/NovaTerminal.App/CommandAssist/Domain/SeedRecipeProvider.cs`,
`tests/NovaTerminal.App.Tests/CommandAssist/SeedRecipeProviderTests.cs`

---

## 5. Add issue and pull-request templates — ✅ DONE 2026-07-31

**Do not file.** Implemented directly:

- `.github/pull_request_template.md` — the `CONTRIBUTING.md` checklist, plus a
  per-category "did you run this locally" list that calls out the two lanes CI
  will not catch for you (the excluded heavy categories, and the non-blocking
  `App.Tests`), plus the VT-coverage regeneration reminder.
- `.github/ISSUE_TEMPLATE/bug_report.yml` — reproducer-first, with the
  does-it-repro-in-another-terminal dropdown.
- `.github/ISSUE_TEMPLATE/feature_request.yml` — problem before proposal, points
  at the roadmap gates, asks whether the reporter wants to implement it.
- `.github/ISSUE_TEMPLATE/config.yml` — blank issues left **enabled**
  deliberately (see the comment in the file); contact links for good-first-issues,
  the contributing guide, and private security advisories.

All three YAML files were validated as parseable. The remaining open question is
whether you want `blank_issues_enabled: false` instead.

<details>
<summary>Original draft (kept for reference)</summary>

### What's wrong

`.github/` contains only `workflows/` and `renovate.json`. There is no
`ISSUE_TEMPLATE/` and no `pull_request_template.md`.

`CONTRIBUTING.md` already defines a five-question PR checklist, but nothing
surfaces it at the moment someone opens a PR, so it gets missed and review
starts with a round-trip asking for it.

### What to change

Add:

1. `.github/pull_request_template.md` — the checklist from `CONTRIBUTING.md`:
   which invariant the change affects, which module owns it, what tests cover
   it, cross-platform impact, renderer-metrics impact. Plus a line for which
   test categories were run locally, since the `App.Tests` lane is
   non-blocking.
2. `.github/ISSUE_TEMPLATE/bug_report.yml` — a form. For a terminal emulator
   the fields that actually matter: OS + version, NovaTerminal version/commit,
   shell, the escape sequence or command that triggers it, expected vs actual,
   and whether it reproduces in another terminal (that one distinction saves a
   lot of triage).
3. `.github/ISSUE_TEMPLATE/feature_request.yml` — problem first, proposal
   second, plus a pointer to `docs/ROADMAP.md` gates.
4. `.github/ISSUE_TEMPLATE/config.yml` — link `CONTRIBUTING.md` and disable
   blank issues, or don't; maintainer's call, ask in the thread.

Keep the forms short. A long form is a form nobody fills in.

### Acceptance

- [ ] PR template mirrors the `CONTRIBUTING.md` checklist
- [ ] Bug form captures OS, version, reproducer, other-terminal comparison
- [ ] Templates render correctly (check the YAML in a draft issue on a fork)

**Files:** `.github/pull_request_template.md`, `.github/ISSUE_TEMPLATE/*.yml`
(all new)

</details>

---

## 6. `CONTRIBUTING.md` has no on-ramp — ✅ DONE 2026-07-31

**Do not file.** Implemented directly in `CONTRIBUTING.md`:

- **"Start Here"** — prerequisites, the wrapper-script build/test commands with
  the `BuildCliShim` hang explained, `good first issue` / `help wanted` filter
  links, and an explicit invitation to ask before writing much code.
- **"How Much Ceremony Your Change Needs"** — isolated changes need a unit test;
  core changes (VT parser, `TerminalBuffer`, reflow, renderer, PTY, threading)
  get the full four-document read. The design docs moved from a gate on everyone
  to a gate on core work.
- **"The Shape of the Codebase"** — a ten-row project table (owns / depends on),
  the two rules that explain the layering (VT never learns about the OS,
  Rendering never interprets VT semantics), where the buffer/reflow/replay suites
  actually live, and a pointer to the architecture tests.
- **"Test Categories"** — all ten `Category` values with what each guards and
  when to add or run one, plus the fact that the default `test` lane excludes
  five of them.
- **"Changing VT Coverage"** — the matrix + embedded-report regeneration
  procedure. The documented command was executed and verified: exit 0,
  `Rows: 55; errors: 0; warnings: 0`, embedded report in sync.
- **Tone**, in the four places it was costing goodwill without buying rigor.

### Adjacent fix, same session

`docs/MODULE_OWNERSHIP.md` documented a module that no longer exists. The
`NovaTerminal.Core` → `NovaTerminal.Platform` rename (#76) had already happened
in the source tree, but the doc still had a `## NovaTerminal.Core` section
claiming the rename was "a planned follow-up", and four dead
`tests/NovaTerminal.Core.Tests/...` paths. `src/NovaTerminal.Core/` and
`tests/NovaTerminal.Core.Tests/` survive on disk only as untracked `bin`/`obj`
leftovers — no `.csproj`, no solution entry.

Fixed, since the new project map points newcomers at that doc. **Still open
there:** `MODULE_OWNERSHIP.md` has no section for `NovaTerminal.Platform`'s
sibling assemblies `NovaTerminal.McpServer` or
`NovaTerminal.AgentHost.Contracts`. Worth its own pass — possibly its own good
first issue.

---

## 7. `REP` (CSI Ps b) is not implemented

**Labels:** `help wanted`, `enhancement`, `vt`

> Not a first issue — this touches the VT write path, which is the most
> invariant-dense part of the codebase. Good second contribution.

### What's wrong

ECMA-48 `REP` — repeat the preceding graphic character `Ps` times — is absent.
`AnsiParser.cs` has no `case 'b'` in its CSI dispatch, and
`docs/vt_coverage_matrix.md` has no row for it at all, so it is not even tracked
as a gap.

Programs that use `REP` to compress runs (some TUI libraries, `tput rep`) draw
missing characters.

### What to change

1. Record the last graphic character written. The printable path is
   `_buffer.WriteChar(c)` at `src/NovaTerminal.VT/AnsiParser.cs:141`.
   Note that `TerminalBuffer` already tracks `_lastCharCol` / `_lastCharRow`
   (`TerminalBuffer.State.cs:18-19`) but those exist for **grapheme
   attachment**, not for `REP` — they record a position, not a character. Do not
   overload them.
2. Add `case 'b'` to the CSI switch. `Math.Max(1, arg0)` repetitions, matching
   the `ICH`/`DCH` convention at `AnsiParser.cs:700-718`.
3. Decide and document the reset rule. In xterm, `REP` applies only to an
   immediately preceding graphic character — any intervening control character
   or escape sequence makes it a no-op. Implement that; a `REP` that repeats a
   stale character is worse than one that does nothing.
4. Respect `DECAWM` wrap and the pending-wrap state — repetition goes through
   the normal write path, it does not bypass it. See
   `tests/NovaTerminal.App.Tests/PendingWrapTests.cs` for the existing
   invariants there.
5. Add a row to `docs/vt_coverage_matrix.md` and regenerate the conformance
   report.

### How to test

Unit tests in `tests/NovaTerminal.VT.Tests/`, at minimum:

- `A` + `CSI 3 b` → `AAAA`
- `CSI b` with no parameter → one repetition
- wide/combining character as the repeated char
- `REP` after a control character → no-op
- `REP` that crosses the right margin under `DECAWM` on and off
- `REP` with a large count is bounded (this is remote-controlled input — a
  parameter of 2^31 must not hang the write path)

That last one matters: check what the existing parameter clamping does before
assuming it is handled.

### Acceptance

- [ ] `REP` implemented with the xterm reset rule
- [ ] Wrap and wide-character interaction tested
- [ ] Large-count input bounded
- [ ] Coverage matrix row added, conformance report regenerated

**Files:** `src/NovaTerminal.VT/AnsiParser.cs`,
`src/NovaTerminal.VT/TerminalBuffer.WritePath.cs`, `docs/vt_coverage_matrix.md`,
`tests/NovaTerminal.VT.Tests/`

---

## 8. OSC 52 clipboard write

**Labels:** `help wanted`, `enhancement`, `security`, `vt`

> Not a first issue — the security posture is the hard part, not the parsing.

### Context

`docs/vt_coverage_matrix.md:145` marks OSC 52 as `❌ Not supported`. It is what
lets a program running over SSH copy to your local clipboard — the single most
requested remote-workflow feature in terminals, and `tmux`/`nvim` both drive it.

### What to change

`OSC 52 ; Pc ; Pd ST`, dispatched from `HandleOsc` at
`src/NovaTerminal.VT/AnsiParser.cs:1465` (follow the `OSC 0/2` and `OSC 7`
shape at `:1496-1514`).

**Architecture constraint:** the VT core must not depend on UI or OS. So the
parser raises an event — `OnClipboardWriteRequested` alongside the existing
`OnTitleChanged` / `OnWorkingDirectoryChanged` — and the App layer performs the
clipboard write. A PR that reaches for an Avalonia clipboard API from inside
`NovaTerminal.VT` will be sent back.

**Security posture, which is the substance of this issue:**

- **Reads (`Pd` = `?`) must be refused.** A remote process being able to read
  your clipboard is an exfiltration primitive — passwords, tokens. xterm ships
  this disabled for good reason. Refuse it outright; do not add a setting for it
  in this PR.
- **Cap the payload.** Base64 from a hostile source is unbounded. Pick a limit
  (xterm's is in the low hundreds of KB), enforce it before decoding, and drop
  oversized requests silently.
- **Validate the base64 strictly** and drop malformed payloads rather than
  writing partial data.
- **Handle `Pc`** — `c`, `p`, `s`, `0`-`7`, or empty. Mapping everything to the
  system clipboard is acceptable; mapping *nothing* is not.
- Consider whether writes should be gated by a setting (default on) and whether
  a toast should confirm — a silent clipboard replacement from a background pane
  is surprising. Worth discussing in the thread before implementing.

### How to test

`tests/NovaTerminal.VT.Tests/` for the parse and policy layer — the event fires
with decoded content for a valid write; does not fire for a read request,
oversized payload, or malformed base64. Then update
`docs/vt_coverage_matrix.md` and regenerate the conformance report.

### Acceptance

- [ ] Write path works end to end, event-based, no UI dependency in VT
- [ ] Read requests refused, with a test pinning that
- [ ] Size cap and base64 validation, with tests
- [ ] Coverage matrix updated from `❌` to its real status

**Files:** `src/NovaTerminal.VT/AnsiParser.cs`, App-layer clipboard wiring,
`docs/vt_coverage_matrix.md`, `tests/NovaTerminal.VT.Tests/`

---

## Filing notes

- Issues 2 and 3 overlap in one file. File 2 first, and if someone takes 3
  before 2 lands, say in the thread which one owns creating the test file.
- Each draft deliberately names the file *and* the test file. The most common
  reason a first-time contributor stalls on this repo is not knowing where a
  test is allowed to live.
- Consider pinning a "Where to start" issue that links these, and adding a
  `good first issue` line to the README — the label only works if people know
  to filter on it.
