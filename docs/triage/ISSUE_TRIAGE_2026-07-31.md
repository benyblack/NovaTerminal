# Issue Triage — 2026-07-31

**Scope:** the 16 issues open on `benyblack/NovaTerminal` after the 07-29→07-31 fix run
(`main@c083803`).
**Method:** every factual claim in every body re-verified against the current tree. Line counts were
counted, not quoted. Diagnostic counts were produced by running the tool. Verdicts carry `file:line`.
No code was changed.

**Headline:** **1 issue is fully complete and should close now (#107).** Two more have premises that
have partly or wholly evaporated (**#115**'s dedupe half has no target; **#112** names a type that does
not exist). One **cannot be executed as written** (**#113** is blocked by two architecture tests and a
documented invariant). Six bodies carry stale numbers. One got *worse* while unattended (**#216**).

---

## 0. Act on these first — **all done 2026-07-31**

| # | Action | Status |
|---|---|---|
| **#107** | Close as completed — all three items done (1+2 in #214, 3 in #244) | ✅ closed |
| **#115** | Drop the dedupe half; retitle to *"Consolidate SSH transport/services into one assembly; bind ConnectionManager to its ViewModel"* | ✅ body + title rewritten |
| **#113** | Re-scope from "move" to "split"; retitle to *"Split the Skia render core out of…"* | ✅ body + title rewritten |
| **#112** | `GlobalCommandRegistry` → `CommandRegistry`; lead with the by-ref leak; retitle | ✅ body + title rewritten |
| **#216** | Gate first, sweep second; retitle to *"Gate dotnet format in CI, then sweep…"*; added `ci` label | ✅ body + title rewritten |
| **#117** | Retitle to the work that remains — *"Re-gate headless App.Tests (blocked on AvaloniaUI/Avalonia#21467)"* | ✅ body + title rewritten |

Each rewritten body opens with a dated revision note linking to its verification comment, so the
correction is auditable rather than looking like the issue always said that. Verified back through the
API: no U+FFFD, labels and state intact.

**Open count: 16 → 15.** The sections below are the evidence those edits were based on, kept as the
record.

---

## 1. Complete — close it

### #107 — RustPtySession: read error vs EOF, temp file leak, Console logging ✅

All three items verified done.

- **Item 1** (negative `pty_read` indistinguishable from EOF): now a bounded retry —
  `MaxConsecutiveReadErrors` (`RustPtySession.cs:51`), `ReadErrorRetryDelay` (:52), counter at :713,
  check at :759 — followed by real session failure: `readFailed = true` (:768) →
  `TryNotifyExit(ReadFailureExitCode)` (:800), with the native reason surfaced via
  `TryGetNativeLastError()` (:764).
- **Item 2** (fire-and-forget PowerShell injection + `%TEMP%` leak): task tracked in
  `_powerShellInitTask` (:530) rather than discarded; deletion is belt-and-braces — the script
  self-deletes as its own last line (:557), the path is recorded first for recovery (:561), the task's
  `finally` races Dispose (:581-589), and Dispose has a backstop (:1000).
- **Item 3** (`Console.WriteLine` throughout): **zero** `Console.Write*` remain in the file; everything
  goes through `PtyLogger` (:751, :765, :782).

---

## 2. Premise gone or wrong — edit before working

### #115 — Consolidate SSH; dedupe ConnectionManager ⚠️ **half the issue has no target**

The consolidation half is valid. **The dedupe half is not.** The issue asserts `ConnectionManager`
"substantially overlaps `NewSshConnectionView`/`ViewModel` for profile editing — two owners of the same
edit flow." Checked each operation that would constitute that overlap:

| Operation | Reality |
|---|---|
| Validate a host | **Only** `NewSshConnectionViewModel.Validate()` :242. `ConnectionManager` and `SshManagerViewModel` have none. |
| Build a profile | **Only** `NewSshConnectionViewModel` :299-340 + `FromTerminalProfile` :349-363. `SshManagerViewModel` only *reads* (`LoadProfiles` :51-55). |
| Save to the store | **Only** `SshConnectionService.SaveProfile` :80/:112. `ConnectionManager` has no store access — it raises `OnEditProfile` (:29, fired :467) for MainWindow to route (`MainWindow.axaml.cs:5071` → :5087). |

The two components are already cleanly split: **ConnectionManager = browse/filter/launch;
NewSshConnectionView(Model) = edit/validate/save.** Its only write is `ToggleFavorite` (:497), an
in-memory tag flip that delegates persistence upward.

Two further corrections:

- "654 LOC of **pure code-behind**" — the line count is exact, but it *has* a ViewModel:
  `_viewModel = new SshManagerViewModel()` :36, `DataContext = _viewModel` :54, rows are
  `SshProfileRowViewModel`. The real complaint is **~400 lines of imperative `FindControl` view
  manipulation** (`RenderDetail` :372-397, `SetText` :399, `UpdateLaunchPreview` :415-438) that should be
  XAML bindings. Reframe it that way — it is still worth doing, for a different reason.
- "SSH smeared across two assemblies and five App folders" — accurate, but the shape matters: SSH *core*
  already lives in `NovaTerminal.Platform\Ssh\` (43 files, 8 subfolders). So the decision is not
  "consolidate the scatter" but **"create `NovaTerminal.Ssh`, or finish moving into Platform"** — and the
  single clearest misplacement is `App\Shell\SftpService.cs` (**864 lines** of transport code in `Shell\`).

### #112 — DI composition root; remove GlobalCommandRegistry static ⚠️

- **`GlobalCommandRegistry` does not exist.** Not in `src`, not in `tests`. The real type is
  `Shell\CommandRegistry.cs:18`, `public static class CommandRegistry`.
- **It has a worse defect than the one described.** The issue's complaint is cross-contamination between
  MainWindow instances. But `GetCommands()` :36 **returns the backing `List<TerminalCommand>` by
  reference** (:20), so any caller can mutate registry state directly — a sharper and cheaper fix than
  DI, and worth doing independently.
- **"Every service is `new`'d inside MainWindow"** is overstated: 182 `new X(` occurrences, but almost
  all are Avalonia visuals. Actual services: **6** — `CommandPaletteUsageStore` :1942,
  `SshConnectionService` :1944, `SshInteractionService` :1945, `SshLegacyProfileMigrationService` :1946,
  `VaultService` :2392, `GlobalHotkey` :134. The sharpest coupling is :1945, which is handed `() => this`
  **plus a MainWindow private method as a delegate**.
- **A partial hand-rolled root already exists** and the issue does not mention it: `Shell\AppServices.cs`
  (`Build` :7-17, `BuildForDesigner` :19-31) → `AppServiceBundle`, called from `App.axaml.cs:26-32`. It
  composes only `StartupRestoreCoordinator` + `StartupOrchestrator`. The work is *extending* this, not
  introducing DI from nothing. No container: `DependencyInjection` appears in **zero** csproj files.
- Second-instance risk is real: MainWindow has **two public constructors** (:1930 designer, :1934 real),
  and tests already build several (`MainWindowStartupTests.cs:90,103,115,139`).

### #113 — Move TerminalView/TerminalDrawOperation into NovaTerminal.Rendering ⛔ **not executable as written**

The premise is right — the renderer is in the wrong assembly, and `ARCHITECTURE.md:263` says so. But a
literal move is blocked:

- `TerminalDrawOperation : ICustomDrawOperation` (`TerminalDrawOperation.cs:21`) — an **Avalonia
  interface** — with usings on `Avalonia`, `.Media`, `.Platform`, `.Rendering.SceneGraph`, `.Skia`.
- `TerminalView : Control` (`TerminalView.cs:41`) — an **Avalonia Control** — using nine Avalonia
  namespaces.
- `NovaTerminal.Rendering.csproj` references **no Avalonia at all** (VT + SkiaSharp only).
- Two tests would fail: `LayeringTests.cs:34-49` lists `"Avalonia"` in Rendering's
  `NotHaveDependencyOnAny` (:44), and `ProjectFileLayeringTests.cs:63-68` asserts Rendering's project
  references equal exactly `["NovaTerminal.VT"]`.
- And `ARCHITECTURE.md:118-120` states the invariant *"Rendering is a pure function of (buffer snapshot,
  metrics, theme)"* — which is the reason those tests exist.

**Re-scope to a split**, not a move: the Skia-pure drawing core into `Rendering`, the Avalonia binding
shell (`ICustomDrawOperation` impl + `Control`) stays in App. That preserves the invariant instead of
amending it. Amending the tests to permit Avalonia in Rendering would delete the boundary this issue
exists to protect.

Also: **both the issue's and ARCHITECTURE.md's line counts are wrong.** Actual `TerminalView.cs`
**2,218** (issue 2,098, doc 1,912); `TerminalDrawOperation.cs` **2,869** (issue 2,745, doc 2,723).

---

## 3. Partly done — remaining scope corrected

### #117 — coverage reporting; re-gate headless App tests — **item 1 done, item 2 blocked upstream**

- **Item 1 done.** `ci.yml:316-420` job `Coverage (NovaTerminal.VT floor)`; `tests/coverage.runsettings`
  drives the XPlat collector for VT/Rendering/Platform/McpServer; the floor gate is
  `ci.yml:406-412` → `scripts/check-coverage.ps1 -MinimumLinePercent 50 -Label NovaTerminal.VT`, with
  the measured baseline (53.14% line / 46.51% branch) recorded at :400-405. Exactly the assembly the
  issue asked for. Note the *test* step is `continue-on-error` (:364) but the **floor check is not**, so
  the gate is real.
- **Item 2 not actionable here.** Headless App.Tests remain `continue-on-error: true` (`ci.yml:284-285`)
  against upstream `AvaloniaUI/Avalonia#21467`. Nothing in this repo unblocks it.
- Stale: "952 Fact/Theory tests" — actual attribute count is **1,501** (`[Fact]` 1274, `[Theory]` 71,
  `[AvaloniaFact]` 153, `[AvaloniaTheory]` 3), and executed cases are higher still.

**Suggested disposition:** narrow the title to the re-gating, or close item 1 explicitly and leave a
thin issue tracking the upstream flake.

### #127 — rendering benchmarks + thresholds — **cache half done, three claims wrong**

- **Done:** cache-effectiveness assertions now exist — `RenderCacheEffectivenessTests.cs`
  (`SecondFrameOfUnchangedContent_IsServedFromTheRowCache` :72, `MutatingOneRow_InvalidatesOnlyThatRow`
  :107, `GlyphAtlasIsReusedAcrossFrames_WithoutResetting` :147,
  `RenderingWithoutARowCache_ReportsNoHits` :170), tagged `Category=RenderMetrics` and gated by the
  `Render Metrics` job (`ci.yml:649-651`, filter at :696-700).
- **Still open:** no frame-time threshold anywhere. `FrameTimeMs` is captured
  (`RenderPerfMetrics.cs:6`, serialized `RenderPerfWriter.cs:94`) but the only tests touching it are
  JSONL serialization tests. No render benchmark in `tests/NovaTerminal.Benchmarks/` — that project has
  three classes and all are parser/reflow/scrollback. No sustained-output stress case comparing render
  rate to parse rate (`StressTests.DataFlood_Backpressure_StressTest` :24 never renders; it asserts only
  wall-clock <10 s :57 and <100 MB :62).
- **The issue's own premise is wrong on two points.** It cites "an enforced ≤24 KB/frame allocation
  test" as *existing*. The real test is
  `RenderPerf_Allocations_SteadyScroll.RenderPerf_Allocations_SteadyScroll_WithinConservativeCeilings`
  :20, whose ceilings are **32,000 avg and 96,000 p95** (:24-25) — ~31 KB/~94 KB, over 160 measured
  frames, not a per-frame cap. And the offscreen harness renders to an
  **SKBitmap-backed SKCanvas, not an SKSurface** (`RenderPerfSteadyScrollHarness.cs:95-148`).
- **A CI-shape note worth carrying:** those perf ceiling tests are `Category=Performance`, so they run
  in `tab_perf_smoke` (:758), **not** in the job called `Render Metrics`. Third instance this week of a
  job name implying coverage it does not have.

### #121 — Native SSH hardening — **Rust half done, managed half genuinely open**

- **Done:** `zeroize = "1.8"` (`rusty_ssh/Cargo.toml:21`), `Zeroizing<String>` password field
  (`lib.rs:312`, taken at :325), keyboard-interactive returns zeroizing (:3183-3213), with three unit
  tests (:3980, :3993, :4003). `NovaSshSafeHandle : SafeHandleZeroOrMinusOneIsInvalid`
  (`NativeSshSafeHandle.cs:10`), threaded through the whole interop surface and released under
  `Interlocked.Exchange` (`NativeSshSession.cs:604`). A substantial FFI abuse suite exists — null
  handles, double-close, use-after-free, concurrent poll/close, handle-id reuse, buffer-sizing abuse,
  malformed and oversized JSON — gated at `ci.yml:65-66`.
- **Open, and the issue describes it accurately.** Managed code retains SSH secrets in plain `string`,
  which cannot be zeroed. Worst offender: **`ActiveSshSessionRegistry.cs:11`, a process-wide singleton
  `ConcurrentDictionary<Guid, string> _runtimePasswords`** holding live plaintext for the session
  lifetime (written :61, read :66, removed only on `Unregister` :45). Also `TerminalProfile.Password`
  :78, `NativeSshConnectionOptions.Password` :16, `NativeSshInterop.cs:884`,
  `SshInteractionResponse.Secret` :7, `AuthPromptViewModel._value` :30, and the `VaultService` /
  `Win32CredentialManager` / `LinuxSecretStore` parameter chain. None use `SecureString`, pooled
  `char[]`, or pinned buffers.
- **One gap the issue does not list:** the abuse suite has **no bad-UTF-8 input test**. Closest are the
  malformed/oversized JSON cases. Cheap to add and squarely in scope.

#### Outcome (2026-07-31)

- **#258 merged** — the singleton retention above. `_runtimePasswords` is now
  `Dictionary<Guid, byte[]>` under an explicit lock, buffers allocated pinned and zeroed on overwrite
  and on `Unregister`. One test I wrote for it was **deleted**: a concurrency test that passed with the
  lock removed. A test that passes with and without the thing it guards is worse than no test; the
  lock's justification is stated in the code as reasoning instead.
- **#259 merged (`628fd6d`)** — the bad-UTF-8 gap, and more than the "cheap to add" I estimated above.
  Adding the test exposed that `read_c_string` used `to_string_lossy()`, so **every** connect argument
  silently accepted invalid UTF-8 with U+FFFD substituted — including the four `*_cwd_bootstrap` /
  `shell_detection_command` arguments, which are shell commands sent to the remote. Replaced with a
  three-state `CArg` (`Absent` / `InvalidUtf8` / `Value`) across 12 call sites.
- **Lesson worth keeping.** The one-line fix ("return `None` when `to_str()` fails") would have been a
  second bug, and a quieter one: `None` already meant *not supplied*, so each optional argument's
  fallback would have absorbed the encoding error — invalid `identity_file` silently downgrading to
  password auth, invalid `bash_cwd_bootstrap` silently disabling cwd tracking. **Ask what the existing
  sentinel already means before reusing it for a new failure.**
- **Also:** "invalid is rejected" is not a discriminating assertion — it passes for reject, default and
  skip alike. Five mutations were needed to prove five tests, one per wrong behaviour.
- **Still open:** item 2 (transient `string` copies — needs an FFI signature change to take a buffer),
  item 3 (**`TerminalProfile.Password` is serialized to disk — blocked on a product decision, do not
  guess**), item 4. Recommendation recorded on the issue: stop here rather than grind through 2-4 for
  diminishing return.

### #95 — OSC 8 adoption — **gap 1 done, gaps 2-7 open**

- **Gap 1 done** (#227): `TerminalBuffer.AccessAndSnapshot.cs:117-126` now reads
  `_scrollback.GetHyperlinkMap(absRow)` instead of returning `null`.
- **Gap 2 open.** The parser discards everything before the second `;` — `AnsiParser.cs:1584-1592` takes
  only `data.Substring(secondSep + 1)` as the URI, so `id=` is dropped unparsed. The side table is
  `SmallMap<string>` (`TerminalRow.cs:19,50,78`), URI only; no hyperlink-id type exists in
  `NovaTerminal.VT`.
- **Gap 3 open.** `TerminalView.cs:1943` stores `(absRow, col, col, osc8)` — a single cell — while the
  auto-detected path 20 lines below stores a real span (:1967, via `RowTextExtractor.SpanToColumns`
  :1962). Same shortcut in the click path (:2027-2028). So **explicit** author-intent links get *worse*
  hover treatment than regex-guessed ones, which is the wrong way round.
- **Gaps 4-7 open.** Zero `hyperlink` references in `RenderSnapshots.cs`, `ReplayModels.cs`, or anywhere
  in `NovaTerminal.Replay` — replay/snapshot fidelity does not exist.

### #173 — Native SSH output path — **item 1's copy half done, rest open**

Recorded in full on the issue (#239). Remaining: the unbounded queue (needs a **control-event carve-out**
— `queue_event` has 15 call sites, several carrying `Closed`/`Eof`/`Error`, so a naive bound deadlocks
teardown), item 2's port-forward pump task, item 3's poll latency and `EmitOutput` allocations. The CI
step named `rusty_ssh FFI tests (registry, abuse suite, **alloc balance**)` still has no allocation
harness behind it.

### #108 — Re-enable TreatWarningsAsErrors incrementally — **batch 1 done, batch 2 analysed not landed**

Current state: `Directory.Build.props` sets `TreatWarningsAsErrors=false` globally; **6 projects opt in**
(`AgentHost.Contracts`, `Cli`, `Architecture.Tests`, `McpServer.Tests`, `Rendering.Tests`, `VT.Tests`),
**12 inherit OFF** — including every large one (`App`, `VT`, `Platform`, `Pty`, `Rendering`, `Replay`).

The batch-2 analysis is already on the issue and holds: **76 unique** diagnostics across the six small
projects (not the 152 first quoted, which double-counted across target evaluations), with three that need
a judgement rather than a fix — the CA2101×8 in `RustPtySession.cs` must **not** be "fixed" (it would
undo the `LPUTF8Str` marshaling that #152 added), CA1051×3 in `GlyphAtlas.cs` wants a documented
suppression, and CA1806×2 in `VttestCapture.cs` is a real defect worth fixing. Plus the trap: two
`StartsWith` lines beside the flagged ones are astral-plane and have no `char` overload.

### #216 — dotnet format sweep — **got worse; the gate is the real fix**

> **My first pass at this section was wrong and I corrected it on the issue.** I reported "845 total
> diagnostics across 77 files" from a subagent's measurement. Re-running the tool and counting the
> output line by line: **674 format violations (all `WHITESPACE`) across 32 files.** The other 174
> lines are ordinary analyzer *warnings* that `dotnet format` prints because it compiles the projects
> — `xUnit1051`×122, `CA1310`×16, `CA1865`×14, `CA1866`×7, `CS0618`×6, `CS8600`×3 and five singles.
> They are not format violations and #216 should not touch them. **The lesson is the one I keep
> applying to other people's numbers: a delegated measurement is still a claim. Verify it before
> publishing it.** Note the original body's "79 files" is the same error — 80 distinct `.cs` files
> appear somewhere in the output; only 32 have violations.

- Format violations: **674, all `WHITESPACE`, across 32 files** (issue claimed 649 across 79). Still
  **zero** `IDExxxx`.
- The two hot files are **worse**: `TerminalDrawOperation.cs` **263** (was 244; I first published 269),
  `TerminalBuffer.ReflowEngine.cs` **241** (was 234). Together 75% of the total.
- **Why they exist:** `EnforceCodeStyleInBuild=true` *is* set and `.editorconfig` *does* define the
  formatting — but the formatter's `WHITESPACE` rule has no build-time equivalent except `IDE0055`,
  and `.editorconfig` deliberately leaves style severities at `silent`, with its own header explaining
  that promoting them would collide with #108's effort to reduce warnings enough to re-enable
  `TreatWarningsAsErrors`. Verified: a full VT build emits **0** `IDExxxx` and 166 `CA` warnings while
  `ReflowEngine.cs` in that project carries 241 whitespace violations. No git hooks installed either.
  So formatting is enforced by nothing today — not the build, not a hook, not CI.
- Still report-only locally: `ci/run.ps1:55-59`, `ci/run.sh:48-51`, both carrying the stale 649/79
  comment.
- **No format check in GitHub Actions at all** — zero matches for `dotnet format` under `.github/`.

That drift, with nobody working on it, is the argument: the issue's own closing note ("that gate is
arguably the real fix, since the sweep alone will silently re-rot") is now demonstrated rather than
predicted. **Add the CI gate first**, then sweep — otherwise the sweep is a one-off that decays.
Sequencing caveat still stands for `TerminalDrawOperation.cs` (central to #113); `ReflowEngine.cs` is now
free, since #164 closed.

---

## 4. Greenfield features — no groundwork exists

### #91 — Windows installer + auto-update (Velopack) + code signing

**Zero implementation.** No `Velopack` in any csproj/props/ps1/yml/sh/cs — only two design docs
(`docs/superpowers/specs/2026-06-04-windows-packaging-velopack-design.md` and its plan), plus a stale
worktree `.claude/worktrees/feat-velopack/` containing nothing but those same docs. No `SignTool` /
Authenticode / certificate config anywhere. No `CheckForUpdates` / `UpdateManager` / auto-update code.

What does exist, and is **currently inconsistent**: `packaging/winget/` holds hand-written manifests
pinned to **0.3.0** including an `installer.yaml`, while `.github/workflows/release.yml` publishes only
**self-contained AOT zips** (:223 publish, :229-231 zip) — so **no installer artifact is produced and the
winget installer manifest is not fed by the pipeline.** Worth noting on the issue; it is a latent
release-process inconsistency independent of Velopack.

### #96 — SSH key management + public key provisioning wizard

**Zero implementation.** No `ssh-keygen`, `GenerateKeyPair`, `authorized_keys`, `ssh-copy-id` anywhere in
`src` — the only repo hits are test infrastructure standing up a Docker SSH server
(`DockerSshFixture.cs`, `ExternalSuites/NativeSsh/Dockerfile`). No key-management UI; identity handling
is a single path field (`SshProfile.IdentityFilePath`).

**One finding worth adding to the issue:** `SshAuthMode { Default, Agent, IdentityFile }`
(`SshProfile.cs:5-10`) advertises an `Agent` mode, but **the native backend has no ssh-agent support** —
the only `Agent` token in `rusty_ssh/src/lib.rs` is `ChannelMsg::AgentForward` in a match arm (:1356). So
`SshAuthMode.Agent` is meaningful only for the external `OpenSsh` backend. Also beware:
`SshProfile.AllowAgentAccess` (:36-41) is the **AI-agent** act-surface toggle, unrelated to ssh-agent —
an easy and consequential misreading when implementing this.

---

## 5. Refactors — dependency order

The four MainWindow-area refactors are not independent, and the issues do not say so.

```
#112 (CommandRegistry by-ref leak + extend AppServices)
  └─> #110 (TabManager + PaneLayout out of MainWindow)
        └─> #111 (single ThemeService)
```

- **#112 first**, and it is smaller than it looks: fixing `GetCommands()` returning its backing list by
  reference is a contained change with immediate value, separable from any DI decision.
- **#110 next.** Its own claims need correcting: MainWindow is **5,826** lines (issue says 5,297 — it has
  *grown* ~530), and of the guard bools the issue worries about, `_closeTabInProgress` (:3082-3117) and
  `_closePaneInProgress` (:3286-3373) **already have try/finally**. The remaining three
  (`_isDraggingTransferOverlay`, `_tabVisualRefreshScheduled`, `_suppressMruTouchOnSelection`) are
  set and cleared in *different event handlers*, so try/finally is structurally impossible — the issue's
  framing does not apply, and `_isDraggingTransferOverlay` already has a `PointerCaptureLost` clear
  (:4960) as its mitigation. The dispatcher-ordering claim **is** real, via
  `Dispatcher.UIThread.Post` (:2154) → `HydrateDeferredStartupTab` (:2161) → `InitializeRestoredTabs`
  → `_broadcastEnabledTabs.Add` (:2441) and `CleanupTabMru` (:2476).
- **#111 has four cascade sites, not three,** and the line numbers are off by ~394. Live-preview
  :4994-5015, post-save :5019-5050, **plus a Cancel/revert path :5054-5067 the issue omits**. The actual
  appliers are four *different* methods: `MainWindow.ApplyThemeToUI` :4153,
  `MainWindow.ApplyThemeToDialogWindow` :1042, `SettingsWindow.ApplyTheme` :1589-1609,
  `ConnectionManager` :123→:617. Partial dedup already exists — `SettingsWindow` :1613 and
  `ConnectionManager` :617 both delegate to `ThemePaletteResources.Apply`, and
  **`MainWindow.ApplyThemeToUI` is the one that does not**, hand-rolling brushes at :4158-4192. That is
  the concrete starting point. `_settings = sw.Settings` wholesale swap confirmed at :5024.
- **#114 is more feasible than its own body claims.** LOC is exact (4,766 across 60 files). Domain and
  Storage are genuinely Avalonia-free — and so are `Models`, `ShellIntegration`, **and the ViewModels**
  (`CommandAssistBarViewModel.cs:7` etc. are plain `INotifyPropertyChanged`). So "a couple of
  ViewModel-facing interfaces need App-side definitions" is **wrong**; the ViewModels move cleanly. The
  only blockers are **two files**: `CommandAssistKeyRouter.cs:1` needs a key abstraction
  (`Avalonia.Input`) and `CommandAssistAnchorCalculator.cs:2` needs geometry primitives (`Avalonia`
  `Point`/`Rect`/`Size`). The three Views stay in App by design.
  The enum-state-machine half is **weaker** than claimed: `CommandAssistController` is 959 lines (exact),
  but has **7** bools, not ~12 — and only 3-4 are state-machine-ish; `_isAltScreenActive`, `_isRemote`,
  `_isShellIntegrationEnabled` are ambient inputs, not machine states.

---

## 6. Recommended order

1. **#107 — close.** Done.
2. **Body edits** on #115, #113, #112, #216 so nobody starts from a false premise. Cheap; prevents the
   trap this repo has already sprung four times this week.
3. **#216's CI gate** (not the sweep). Small, and it stops the measured drift.
4. **#121's managed-secret retention.** Highest-value remaining substantive work: a process-wide
   singleton holding plaintext passwords for the session lifetime is a real exposure, the Rust half is
   already done, and the scope is a bounded set of named fields. Add the bad-UTF-8 abuse test alongside.
5. **#95 gaps 2+3 together.** The data-model change (`id=`) and full-span hover are the "adoption" body,
   and #3 alone is already an anomaly worth fixing — explicit links currently hover worse than guessed
   ones.
   **Gap 2 done** — #260, merged. Reading the spec rather than trusting recollection changed the design:
   identity is the (URI, `id`) *pair*, and for no-id links the spec recommends VTE's "fresh identity per
   OSC 8 open". Adopting that means every hyperlink cell has an identity, so **gap 3 can group by identity
   instead of walking outward while URIs match** — the adjacent-same-URI merge bug I had planned to
   document as a known limitation simply cannot occur. Gap 3 is now the smaller, self-contained half.
   Contained to `NovaTerminal.VT`: `GetHyperlinkAbsolute` still returns `string?` so both App call sites
   were untouched, nothing in Rendering or Replay reads hyperlinks, and scrollback is never serialised so
   reference identity survives paging (asserted by a test).
   *Process note:* my first mutation set had six mutations, all caught — but `SameUriDifferentIds_
   AreDistinctLinks`, the test pinning the actual fix, was not the **sole** catcher of any of them. Added a
   seventh (group by URI alone, ignoring `id` — the literal pre-change behaviour) which it catches alone.
   A test that never uniquely catches a mutation is not pulling its weight; the gap was in the sweep, not
   the test.
6. **#112's `GetCommands()` by-ref leak**, as a standalone fix ahead of any DI decision.
7. **#127's frame-time gate**, once someone decides whether a wall-clock threshold can be non-flaky on
   shared runners — the issue's own plan (nightly first) is the right answer.

Deliberately last: **#110/#111/#113/#114** (large refactors needing sequencing and a re-scope), **#91/#96**
(greenfield features), **#117 item 2** (blocked upstream), **#108 batch 2** (analysis complete, awaiting a
call on the two suppressions).

---

## Newcomer-readiness pass: #247–#252 (2026-07-31)

These six exist to attract external contributors, so they are **not to be implemented in-house**. This pass
checked only whether a stranger could pick each one up unaided. Every file path, line range, symbol name and
API signature was opened and confirmed against the post-merge tree.

**All six are technically accurate.** Spot-verified: `SixelDecoder.cs:99-103` (the HLS stub and its literal
`SixelColor(200, 200, 200)`), the `type == 2` clamp at `:91-93`, `SeedRecipeProvider` holding exactly seven
recipes at `:12-21` covering the six commands named, `IThemeImporter`'s three members, the `ThemeManager`
importer list at `:14-19`, `SetAnsiColor(int index, bool bright, TermColor color)`, no importer claiming
`.conf`, no test file matching `*Import*` anywhere under `tests/`, no `case 'b'` in the CSI dispatch (only
`case 'B'` for CUD at `:679`), no REP row in the coverage matrix, `WriteChar` at `AnsiParser.cs:141`,
`_lastCharCol`/`_lastCharRow` at `TerminalBuffer.State.cs:18-19` (positions, not characters — as the issue
correctly warns), ICH/DCH at `:700`/`:713`, `HandleOsc` at `:1465`, OSC 0/2 and OSC 7 at `:1496-1514`,
`OSC 52 | Clipboard | Not supported` at `vt_coverage_matrix.md:145`, and `continue-on-error: true` on the
headless lane at `ci.yml:292`. Every `CONTRIBUTING.md` anchor the six issues link to resolves to a real
heading. #247's promised verification comment exists and contains the libsixel and xterm citations it
advertises.

Three defects found and fixed in the bodies:

1. **A stray `@` opened and closed every one of the six bodies.** Real content, not a local display
   artifact — confirmed via `web_fetch`, and it was leading GitHub's `og:description`, so every link preview
   of a newcomer issue began with a bare "@". Stripped.
2. **#251 sent the contributor to check something already solved.** It asked them to verify that a large
   `REP` count cannot hang the write path. It cannot: `MaxCsiParamValue = 65535` (`AnsiParser.cs:37`) caps
   every CSI parameter during digit accumulation at `:619-621`, and *its comment already names
   `Repeat (CSI Ps b)`* as one of the operations it exists to bound. Left as-is, a newcomer would likely add
   a redundant second clamp. Body now says to pin the existing bound with a test.
3. **#250 suggested a test that cannot discriminate anything yet.** "`pwsh` recipes rank above `bash` ones
   for a `pwsh` query" — but `GetRecipesAsync` filters by `CommandToken` *before* ordering, and no command in
   the catalogue has both a `bash` and a `pwsh` recipe, so the assertion has nothing to separate. Body now
   says to add a command with both forms first.

### My own error, recorded because the lesson is reusable

The first edit pass **corrupted all six bodies** and I had to repair them. I fetched each body with
`gh api | Out-String`, changed one line, and PATCHed it back. The write path was correct (UTF-8 no-BOM file
via `--input`) but the **read** path was not: PowerShell's console decoder mangled every em dash, section
sign, multiplication sign and arrow before I ever built the JSON. I had a memory note warning that this box
mangles UTF-8 "both into and out of `gh`" and still only guarded the write.

Recovered by reversing the misdecode — the corruption preserves every byte, so re-encoding to cp437 and
decoding as UTF-8 restores the original exactly (cp850 and cp1252 do not work here). Before writing anything
I re-applied the corruption to the repaired text and confirmed it reproduced the stored text byte-for-byte,
rather than eyeballing it. Verified afterwards through `web_fetch`, which never touches PowerShell.

**Rule going forward: never read a document through a lossy pipe and write it back.** A read-modify-write
inherits every defect of the read. Let `cmd` do the redirection to a file and parse it with an explicit
UTF-8 reader.
