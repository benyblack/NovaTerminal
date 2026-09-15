# Ntilde – Module Ownership & Invariant Map

Each assembly owns specific invariants. Layering rules are encoded as
[NetArchTest](https://github.com/BenMorris/NetArchTest) facts in
`tests/Ntilde.Architecture.Tests/`. Behavioral invariants are
encoded as unit/integration tests in each module's test suite.

Breaking an invariant is a bug, even if the UI appears correct.

The companion doc is `docs/ARCHITECTURE.md`. Update both when an
invariant changes.

---

## Ntilde.VT (`src/Ntilde.VT/`)

**Namespace:** `Ntilde.VT` (+ `.Export`, `.Links`, `.Storage` sub-namespaces)
**Depends on:** *(leaf — only BCL)*
**Public surface:** `AnsiParser`, `TerminalBuffer`, `TerminalRow`, `TerminalCell`, `BufferSnapshot`, `RenderSnapshots.*`, `ReplayModels.*`, `TerminalTheme`, `UnicodeWidth`

**Owns**
- VT/ANSI state machine and parser
- Main + alternate screen buffers
- Scrollback (`Buffer/ScrollbackPages.cs`) and lossless reflow (`TerminalBuffer.ReflowEngine.cs`)
- Cell/row semantics, grapheme cluster handling, Unicode width
- Buffer threading contract (`TerminalBuffer.Lock`, `ReaderWriterLockSlim`)
- Resolution of `OSC 133` marks against the buffer: `GridQueryReader` (mark → live command-line
  text) and `ShellMarkAnchorResolver` (mark → viewport row, for Command Assist's overlay anchor).
  Both live here rather than in `Ntilde.CommandAssist` because they are buffer arithmetic
  over VT types, which the layering tests forbid that assembly from referencing.

**Invariants** (enforced by architecture tests and `tests/Ntilde.VT.Tests/`)
- Deterministic parsing — same byte stream produces the same semantic ops
- Source of truth — renderers/replay/sessions read this, they don't replicate state
- Lossless reflow — resize never silently drops content
- Alternate-screen isolation — main buffer is preserved across alt entries/exits
- All read access requires holding `TerminalBuffer.Lock`; reads without the lock throw via `AssertLockHeld`
- **Lock re-entrancy contract:** `Lock` is a non-recursive `ReaderWriterLockSlim`. `EnterReadLockIfNeeded()` returns `false` (and acquires nothing) when a read *or* write lock is already held. `EnterWriteLockIfNeeded()` returns `false` only when a *write* lock is already held — calling it while holding a read lock throws `LockRecursionException` (upgrading is not supported), it does **not** return `false`. Both return `true` when they actually acquired the lock. The returned `bool` says *whether this call took the lock* — callers must pass it to the matching `Exit…IfNeeded(..., lockTaken)` and must **not** unlock when it is `false`. Treating a `false` return as "lock acquired" double-unlocks (or unlocks a caller's outer lock).
- **`GetRowAbsolute()` null contract:** returns `null` for any absolute row that has no persistent `TerminalRow` — including **paged-out scrollback rows** (scrollback lives in `ScrollbackPages`, not as row objects), out-of-range rows, and negative indices. To read scrollback content use the cell/grapheme accessors (`GetCellAbsolute`, `GetGraphemeAbsolute`), which page it in; callers that assume a non-null row for scrollback indices will NRE.
- No OS, PTY, rendering, or UI logic in this assembly (`Vt_must_be_a_leaf_assembly` arch test)
- All types in `Ntilde.VT.*` namespace (`Leaf_assembly_types_reside_in_its_own_namespace`, `NamespaceAlignmentTests.cs`)

**Test authority**
- Primary: `tests/Ntilde.VT.Tests/`
- Replay/regression coverage: `tests/Ntilde.App.Tests/ReplayTests/`, `tests/Ntilde.App.Tests/AnsiCorpusReplayTests.cs`
- Buffer/reflow coverage: `tests/Ntilde.App.Tests/Buffer/`, `tests/Ntilde.App.Tests/ReflowScenariosTests.cs`, `tests/Ntilde.App.Tests/BufferTests/`

---

## Ntilde.Replay (`src/Ntilde.Replay/`)

**Namespace:** `Ntilde.Replay`
**Depends on:** VT
**Public surface:** `ReplayReader`, `ReplayWriter`, `ReplayRunner`, `ReplayIndex`, `BufferSnapshot`, `GoldenMaster`, `PtyRecorder`

**Owns**
- Replay file format v2 (see `docs/REPLAY_FORMAT_V2.md`)
- Recording-of-bytes pipeline (`ReplayWriter`)
- Playback (`ReplayReader`, `ReplayRunner`) with snapshot virtualization options
- Golden-master harness used by regression suites

**Invariants** (enforced by `Replay_only_depends_on_Vt` and behavioral tests)
- Replay is a pure function of `(byte stream, optional snapshots) → buffer state`
- Cannot reference Pty, Rendering, App, Avalonia, or SkiaSharp
- Snapshot format is forward-compatible within v2

**Test authority**
- `tests/Ntilde.Platform.Tests/Replay/`
- `tests/Ntilde.App.Tests/ReplayTests/`
- `tests/Ntilde.App.Tests/Regressions/` (Midnight Commander, regression suite)

---

## Ntilde.Rendering (`src/Ntilde.Rendering/`)

**Namespace:** `Ntilde.Rendering`
**Depends on:** VT, SkiaSharp 3.119.4
**Public surface:** `PixelGrid`, `GlyphAtlas`, `GlyphCache`, `RowCache`, `ImageRegistry`, `SixelDecoder`, `RenderPerfMetrics`, `RenderPerfWriter`, `RendererStatistics`, `SharedSKFont`, `SharedSKTypeface`

**Owns**
- Skia glyph atlas / cache (dual-atlas system)
- Pixel-grid layout math (`PixelGrid`)
- Sixel image decoding
- Font and typeface wrappers
- Renderer performance metrics

**Invariants** (enforced by `Rendering_only_depends_on_Vt_and_Skia`)
- Rendering is a pure function of `(buffer snapshot, metrics, theme) → pixels`
- No semantic decisions — if the buffer is wrong, the renderer cannot fix it
- No Avalonia in the dependency closure (the Avalonia binding shell is in App)
- Incremental rendering only — no full-redraw fallbacks except on resize/theme change

**Test authority**
- Primary: `tests/Ntilde.Rendering.Tests/` (Skia primitives that don't need a GPU context)
- Renderer metrics: `tests/Ntilde.App.Tests/RenderTests/RendererMetricsTests.cs`
- Golden PNG comparisons: `tests/Ntilde.App.Tests/RenderTests/GoldenSharedPngTests.cs`, `GoldenFontPngTests.cs`

> **Note:** Today the Avalonia renderer composition (`TerminalView`, `TerminalDrawOperation`) lives in `src/Ntilde.App/Shell/` — see `docs/ARCHITECTURE.md` § 14 Known Tech Debt, tracked as #113.

---

## Ntilde.Pty (`src/Ntilde.Pty/`)

**Namespace:** `Ntilde.Pty`
**Depends on:** Replay (for `ReplayWriter` only)
**Public surface:** `ITerminalIO`, `ITerminalLifecycle`, `ITerminalShellMetadata`, `ITerminalRecorder`, `ITerminalSession` (composite), `RustPtySession`, `ShellHelper`, session model DTOs

**Owns**
- The rust-PTY adapter (`RustPtySession` + `ConPtyNative` P/Invoke)
- Session contracts: four narrow interfaces composed into `ITerminalSession`
- Raw byte-stream recording lifecycle (no buffer snapshots — those moved out in Phase 5)

**Invariants** (enforced by `Pty_must_not_depend_on_Vt`)
- **Pty does not reference VT.** The session reports raw bytes/strings; parsing into a `TerminalBuffer` is the consumer's responsibility.
- IO is bounded and non-blocking
- Bytes are delivered verbatim — no transformations at this layer
- `ITerminalSession` is a kitchen-sink composite of four narrower interfaces; new code should depend on the narrowest one that fits

**Test authority**
- `tests/Ntilde.App.Tests/PtySmokeTests.cs` (PtySmoke category — filtered out of default CI lane)
- `tests/Ntilde.ExternalSuites/Vttest/` (external scenario driver)
- `tests/Ntilde.App.Tests/Ssh/TerminalPaneRecordingTests.cs`

---

## Ntilde.Platform (`src/Ntilde.Platform/`)

**Namespace:** `Ntilde.Platform` (+ `.Input`, `.Paths`, `.Execution`, plus the SSH sub-tree)
**Depends on:** Pty
**Public surface:** `TerminalInputSender`, path mappers, process abstractions, the SSH stack (`Ssh/{Interactions,Launch,Models,Native,OpenSsh,Sessions,Storage,Transport}`)

**Owns**
- Input routing primitives (drop router, shell quoters, input sender)
- Path mapping (notably WSL ↔ Windows)
- Process abstraction (`IProcessRunner`)
- The entire SSH stack: native interop with `rusty_ssh.dll`, OpenSSH bridging, session factories, profile storage, transport
- Future home of `SessionBufferBinder` and other session-orchestration helpers

**Invariants**
- This is NOT the terminal engine (that's VT). Renamed from `Ntilde.Core` in #76 to end the three-way "Core" name overload.
- No Avalonia or Skia in the dependency closure
- SSH transports must satisfy `IRemoteTerminalTransport` so all SSH session implementations are interchangeable

**Test authority**
- Primary: `tests/Ntilde.Platform.Tests/`
- Docker-gated E2E: `tests/Ntilde.Platform.Tests/Ssh/NativeSshDockerE2eTests.cs` (skipped without Docker)
- App-side integration: `tests/Ntilde.App.Tests/Ssh/`, `tests/Ntilde.App.Tests/Input/`

---

## Ntilde.AgentHost.Contracts (`src/Ntilde.AgentHost.Contracts/`)

**Namespace:** `Ntilde.AgentHost.Contracts`
**Depends on:** *(leaf — only BCL)*
**Public surface:** `AgentHostProtocol`, `AgentHostDiscovery`, `AgentHostJsonContext`, `Frames.*`, and the contract DTO groups (`ActContracts`, `SessionContracts`, `StatusContracts`, `ReplayContracts`)

**Owns**
- The wire protocol between the app's agent host and any external client (today: the MCP server)
- Frame definitions, discovery, and the source-generated JSON serialization context

**Invariants** (enforced by `AgentHostContracts_must_be_a_leaf_assembly` and `AgentHostContracts_csproj_must_have_no_project_references`)
- Leaf assembly — no project references at all, so both sides of the wire can depend on it without pulling in a dependency graph
- Shared by App and McpServer: a breaking change here breaks the agent integration on both sides at once. Version the protocol rather than redefining a frame in place.

**Test authority**
- `tests/Ntilde.McpServer.Tests/AgentHostClientTests.cs`
- End-to-end over real stdio: `tests/Ntilde.McpServer.Tests/McpServerStdioE2ETests.cs`

---

## Ntilde.VtContract (`src/Ntilde.VtContract/`)

**Namespace:** `Ntilde.VtContract`
**Depends on:** *(leaf — only BCL)*
**Public surface:** `VtCapabilityCatalog`, `VtCapability`, `VtSupport`, `VtCapabilityManifestException`

**Owns**
- The machine-readable catalog of developer-facing VT capability claims
- Strict validation of catalog schema, unique sequence keys, support states, evidence links, and parser contract case names

**Non-responsibilities**
- Parsing terminal input or dispatching escape sequences; `Ntilde.VT` remains the sole owner of runtime VT semantics
- Rendering, application UI, PTY I/O, or MCP transport

**Invariants** (enforced by `VtContract_csproj_has_no_project_references` and the VT capability contract tests)
- Leaf assembly — no project references, so conformance tooling, parser tests, and MCP developer tools can consume the same claims without opening a path into terminal core
- The embedded JSON is the canonical capability source; supported entries require concrete repository evidence and an executable parser contract case

**Test authority**
- Schema and parser contracts: `tests/Ntilde.VT.Tests/VtCapabilityCatalogTests.cs`, `tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs`
- Matrix agreement: `tests/Ntilde.Platform.Tests/Conformance/VtConformanceToolTests.cs`
- Developer-tool agreement: `tests/Ntilde.McpServer.Tests/V2ToolsTests.cs`

---

## Ntilde.Backup (`src/Ntilde.Backup/`)

**Namespace:** `Ntilde.Backup`
**Depends on:** *(leaf — only BCL)*
**Public surface:** `BackupService`, `SnapshotScheduler`, `BackupCatalog`, `BackupCategory`, `BackupManifest`, `BackupOutcome`, `InspectOutcome`, `BundleInspection`, `SnapshotInfo`, `SnapshotReason`, `ImportMode`, `BundleReader`, `BundleWriter`
**Internals exposed to:** `Ntilde.App.Tests` only (`BackupService.ResolveImportStagingRoot`, `SnapshotScheduler.HasPendingChange`/`BeforeSnapshotForTest`/`NotifyFileSystemEvent` — test seams, same shape as `CommandAssist`'s grant below)

**Owns**
- Export/import/snapshot/restore of Ntilde configuration into `.ntildebackup` bundles (a zip with a manifest)
- The category→path catalog (`BackupCatalog`) that decides what a bundle contains
- The debounced snapshot scheduler that watches the backed-up paths and writes an automatic snapshot after changes go quiet

**Non-responsibilities**
- Resolving the app-data root. `BackupService` takes it as a constructor argument rather than
  reading `AppPaths` itself, so both the App and a test can point it at whatever tree they like.
  The one caller that does read `AppPaths` (`BackupCommand`, the `backup` CLI verb) stays in
  `Ntilde.App/Shell/Backup/` rather than moving here — see the invariants below.
- Logging. Nothing here calls a static logger; callers pass an optional `Action<string>? log`
  into `BackupService`'s and `SnapshotScheduler`'s constructors (defaulting to a no-op), mirroring
  the existing `TimeProvider?` injection style.

**Invariants** (enforced by `Backup_csproj_has_no_project_references` and
`McpServer_csproj_only_references_approved_leaf_dependencies`)
- **Leaf assembly — no project references, ever.** This is what makes it safe for
  `Ntilde.McpServer` to reference: a leaf cannot smuggle in App, VT, Pty, or Rendering no
  matter what it depends on, because it depends on nothing. Task 10a's first attempt routed the
  MCP backup tools through `Ntilde.Platform` instead — which itself has no App/VT
  dependency, but does reference `Ntilde.Pty`, so McpServer → Platform → Pty transitively
  broke McpServer's "does not reference Pty" invariant with the reasoning fully intact. Extracting
  this leaf closes that hole by construction, not by remembering to re-check every transitive hop
  Platform might one day pick up.
- No secret material. A bundle carries connection profiles with their `RememberPasswordInVault`
  flag but never password/key material (issue #100) — `BackupService` never reads secret storage.
- `BackupCommand` (the `backup` CLI verb) is the one caller-facing piece that stays behind in
  `Ntilde.App/Shell/Backup/`: it is the only consumer that needs `AppPaths`, and the CLI
  already lives in App (see the Task 7 dispatch guard on `Ntilde.App`, below). Moving it here
  would have bought nothing, since App already references this assembly.

**Test authority**
- `tests/Ntilde.App.Tests/Backup/`
- Palette/scheduler wiring: `tests/Ntilde.App.Tests/Core/MainWindowBackupPaletteTests.cs`
- Settings UI: `tests/Ntilde.App.Tests/Core/SettingsWindowBackupSectionTests.cs`
- Leaf-boundary invariants: `tests/Ntilde.Architecture.Tests/`

> **Note:** extracted from `Ntilde.App/Shell/Backup/` in Task 10a (2026-08-27), then
> re-extracted from a one-round stop at `Ntilde.Platform/Backup/` into its own leaf project
> in Task 10a fix round 1, once review found the Platform routing broke McpServer's
> Pty-independence invariant.

---

## Ntilde.CommandAssist (`src/Ntilde.CommandAssist/`)

**Namespace:** `Ntilde.CommandAssist` (+ `.Application`, `.Domain`, `.Models`, `.Storage`, `.ShellIntegration`, `.ViewModels`)
**Depends on:** *(leaf — only BCL)*
**Public surface:** `CommandAssistController`, `AssistSessionState`, `CommandAssistAnchorCalculator` (+ `AssistRect`/`AssistPoint`/`AssistSize`), `AssistKey`/`AssistModifiers`, `CommandAssistModeRouter`, `CommandAssistKeyRouter`, `CommandAssistInsertionPlanner`, `CommandAssistResultBuilder`, `RecognizedCommandParser`, the `I*Store` / `I*Provider` / `ISuggestionEngine` / `ISecretsFilter` domain contracts and their local implementations, `IShellIntegrationProvider` + the four shell providers and bootstrap builders, `ShellIntegrationRegistry`, `ShellLifecycleTracker`, `JsonlHistoryStore`, `JsonSnippetStore`, `CommandAssistJsonContext` / `CommandAssistJsonLinesContext`, and the assist view-models
**Internals exposed to:** `Ntilde.App.Tests` only. The App is deliberately not granted
`InternalsVisibleTo`: the two helpers `TerminalPane` needs (`CommandAssistKeyRouter`,
`CommandAssistInsertionPlanner`) are public pure-static functions over public types, so the App
consumes this assembly through its published surface. The controller's three collaborators
(`AssistSessionStateMachine`, `AssistSessionContext`, `CapturePipeline`, `SuggestionOrchestrator` +
`SuggestionRefreshOutcome`) and the controller's own `SessionState` accessor are `internal`: only
`CommandAssistController` constructs them, and the only outside readers are the unit tests. The
`AssistSessionState` enum is the one exception that stays public - no public member exposes it, but
the state machine's transition-table tests drive it through `[Theory]` parameters, and xUnit
requires public test classes.

**Owns**
- Assist domain: suggestion ranking, path suggestions, secrets redaction, local docs/recipes, heuristic error insight
- Assist models: suggestions, query/context snapshots, history entries, snippets, failure context
- Assist storage: `history.jsonl` (append-only, in-memory index, periodic compaction, one-time
  migration from the pre-V2 `history.json`) and `snippets.json` (whole-file: low write volume),
  plus their source-generated JSON contexts
- Shell integration: the `OSC 133` contract, the bash/zsh/fish/PowerShell bootstrap builders and providers, provider registry, lifecycle tracking, ordered async event dispatch
- Assist view-models (`INotifyPropertyChanged` only — no toolkit types)
- Application core: controller (a facade over the session state machine, the capture pipeline and
  the suggestion orchestrator, with `AssistSessionContext` carrying the environment they share),
  mode router, insertion planner, result builder, key router, anchor calculator

**Non-responsibilities**
- Rendering the assist surfaces. The Avalonia `UserControl`s stay in the App at
  `src/Ntilde.App/CommandAssist/Views/` under `Ntilde.CommandAssist.Views` — the one
  namespace prefix deliberately shared between two assemblies.
- Resolving application state. Storage paths (`AppPaths`) and settings (`TerminalSettings`) are
  App concerns; they are passed in. `Shell/CommandAssistServices.cs` in the App composes the graph,
  `AppServices.Build` builds the single instance, and `MainWindow` injects it into every pane.

**Invariants**
- **No Avalonia (or Skia) in the dependency closure** — enforced twice: at IL level by
  `CommandAssist_must_not_depend_on_Avalonia_or_the_App` (`LayeringTests.cs`) and at project level by
  `CommandAssist_csproj_must_have_no_project_or_avalonia_references` (`ProjectFileLayeringTests.cs`).
  UI vocabulary crosses the boundary through App-side mappers only: `Avalonia.Input.Key`/
  `KeyModifiers` → `AssistKey`/`AssistModifiers` via `Controls/AssistKeyMapper.cs`, and
  `Avalonia.Rect` → `AssistRect` by construction inside the calculator.
- Leaf assembly — no project references at all, so it stays cheap to reference and cannot drift
  into the UI layer through a transitive edge.
- All types in `Ntilde.CommandAssist.*` (`Leaf_assembly_types_reside_in_its_own_namespace`);
  the App may only use that prefix for Views
  (`App_may_only_use_the_CommandAssist_prefix_for_Views`).

**Test authority**
- `tests/Ntilde.App.Tests/CommandAssist/` (kept there for now — the suite exercises the assist
  assembly and the App's `TerminalPane` wiring together)
- Architecture invariants: `tests/Ntilde.Architecture.Tests/`

> **Note:** extracted from the App in #114 as Phase 0 of the Command Assist V2 rebuild
> (`docs/plans/2026-08-01-command-assist-v2-plan.md`). Phase 0b then replaced the static
> `CommandAssistInfrastructure` locator with the injected `CommandAssistServices`, unified ranking on
> `CommandAssistSuggestionEngine`, and swapped the history store for JSONL. Phase 0c split
> `CommandAssistController` into `AssistSessionStateMachine`, `CapturePipeline` and
> `SuggestionOrchestrator` (plan task 4), completing Phase 0.

---

## Ntilde.McpServer (`src/Ntilde.McpServer/`)

**Namespace:** `Ntilde.McpServer` (+ `.Tools`)
**Depends on:** AgentHost.Contracts, Backup (both zero-reference leaves — see below)
**Public surface:** `Program` (stdio entry point), `AgentHostClient`, `RepoContext`, and the tool groups under `Tools/` (`SessionTools`, `VtTools`, `ThemeTools`, `SettingsTools`, `ConnectionProfileTools`, `ProjectTools`, `WorkflowTools`)

**Owns**
- The opt-in MCP server that lets external agents observe live terminal sessions, and — behind a separate opt-in — drive them
- Tool schemas exposed over MCP, and their validation of caller input
- Talking to the running app through `AgentHostClient` over the AgentHost protocol
- The read-only backup tools (Task 10b) built on `Ntilde.Backup`'s `BackupService`

**Invariants**
- **Does not reference App, VT, Pty, Rendering, or `Ntilde.Platform`.** It is a client of the
  running app over the wire, not an in-process consumer — so it can never reach into terminal state
  directly. `Ntilde.Platform` is named explicitly (not just "the UI layer") because it is the
  one non-obvious way in: Platform itself has no App/VT dependency, but it does reference
  `Ntilde.Pty`, so a McpServer → Platform reference would transitively break "does not
  reference Pty" while looking, at the csproj level, like a small and unrelated addition (Task 10a
  fix round 1 shipped exactly this and caught it in review). `AgentHost.Contracts` and
  `Ntilde.Backup` are the only two references allowed, and both are leaves enforced to have
  zero project references of their own, so neither can ever become a backdoor to the forbidden set.
- Observe and act are separately gated. A tool that mutates session state belongs behind the act opt-in.
- Tool schemas are part of the public contract: `ConnectionProfileDriftGuardTests` exists to catch schema drift against the app's real profile shape.

**Test authority**
- Primary: `tests/Ntilde.McpServer.Tests/`
- Schema-drift guard: `ConnectionProfileDriftGuardTests.cs`
- Stdio end-to-end: `McpServerStdioE2ETests.cs`

> **Note:** the dev companion runs the server from `bin/`, so a connected client
> can hold a lock that blocks repo builds — tracked as #211. See
> `docs/mcp-dev-companion.md`.

---

## Ntilde.App (`src/Ntilde.App/`)

**Namespace:** `Ntilde` (NOT `Ntilde.App` — see test-root-namespace note in `Ntilde.App.Tests`)
**Depends on:** Platform, VT, Rendering, Pty, Replay, AgentHost.Contracts, CommandAssist, Backup, Avalonia 12.0.4, SkiaSharp 3.119.4
**Public surface:** `App`, `MainWindow`, `TerminalPane`, settings window, theme manager, command palette, command-assist controller, profile importers, startup orchestrator

**Owns**
- Avalonia UI: windows, controls, view-models
- The currently-in-App renderer composition: `Shell/TerminalView.cs`, `Shell/TerminalDrawOperation.cs` (slated to move to Rendering — #113)
- Theme management and bundled fonts
- Profile import/export (Alacritty, iTerm2, Windows Terminal)
- Command palette and shortcuts
- The Command Assist *presentation* layer only: `CommandAssist/Views/` (Avalonia `UserControl`s),
  the `TerminalPane` wiring, and `Shell/CommandAssistServices.cs` as the composition root.
  Everything else moved to `Ntilde.CommandAssist` in #114.
- Startup orchestration (seven `Startup*.cs` files in `Shell/`)
- Workspace and session lifecycle
- SSH UI: connection manager, transfer center, remote files sidebar, vault, sftp service, ssh-askpass

**Non-responsibilities**
- VT parsing (delegated to VT)
- Buffer mutation (only via explicit VT APIs)
- Skia primitive logic (delegated to Rendering)

**Invariants**
- App is allowed to depend on all production assemblies; nothing depends on App except Cli and the App.Tests project (and Architecture.Tests, which references everything for inspection)
- Renderer-side bugs ("the pixels look wrong") are diagnosed by chasing back through Rendering → VT, not by patching App

**Test authority**
- `tests/Ntilde.App.Tests/` (the largest suite)
- xunit.v3 + `Avalonia.Headless.XUnit 12.0.4`; **do not downgrade** the Avalonia stack below 12.0.4 — earlier versions leak the headless dispatcher and hang `dotnet test`

---

## Ntilde.Cli (`src/Ntilde.Cli/`)

**Namespace:** `Ntilde.Cli`
**Depends on:** App
**Public surface:** `Program` (Main entry point)

**Owns**
- Headless CLI entry — used for `vt-report` and automation
- Today the CLI shim is built by the App project via the `BuildCliShim` MSBuild target and copied into App's output as a sidecar
- Reaches into App via `InternalsVisibleTo("Ntilde.Cli")`

**Invariants**
- This dependency direction is **inverted** — see `docs/ARCHITECTURE.md` § 14 Known Tech Debt. A `Ntilde.Bootstrap` library should mediate.

**Test authority**
- `tests/Ntilde.App.Tests/VtReportCliTests.cs`
- Console-output rules for CLI vs GUI assemblies: `tests/Ntilde.Architecture.Tests/DiagnosticSinkTests.cs`

---

## Ntilde.Conformance (`src/Ntilde.Conformance/`)

**Namespace:** `Ntilde.Conformance`
**Depends on:** *(standalone Exe, no project references)*
**Public surface:** `VtConformanceReportTool`, `VtConformanceCli`

**Owns**
- VT conformance matrix parser (reads `docs/vt_coverage_matrix.md`)
- Report generator (writes `src/Ntilde.App/Resources/vt-conformance-report.json`)
- Evidence-link validator (fails CI if a matrix row claims a test file that doesn't exist)

**Invariants**
- Standalone tool — no library dependencies on the rest of the assemblies; consumed via project references from test projects (which run it in-process for validation)
- The shipped `vt-conformance-report.json` artifact's `matrixSha256` must match a fresh re-run on `vt_coverage_matrix.md` — verified by `tests/Ntilde.App.Tests/VtReportCliTests.ShippedArtifact_MatchesFreshToolOutput`

**Test authority**
- `tests/Ntilde.Platform.Tests/Conformance/VtConformanceToolTests.cs`
- `tests/Ntilde.App.Tests/VtReportCliTests.cs`

---

## Tests (First-Class Owners)

### `tests/Ntilde.Architecture.Tests/`

**Owns** the layering and namespace-alignment rules. Adding a new architectural invariant means adding a fact here. See `docs/ARCHITECTURE.md` § 12 for the current enforced rule set.

Four files, by concern:
- `LayeringTests.cs` — assembly-level dependency rules (`Vt_must_be_a_leaf_assembly`, `Pty_must_not_depend_on_Vt`, …)
- `ProjectFileLayeringTests.cs` — the same rules asserted against the `.csproj` files, so a stray `ProjectReference` fails even if no code uses it yet
- `NamespaceAlignmentTests.cs` — one assembly, one namespace prefix
- `DiagnosticSinkTests.cs` — GUI and library code must not write diagnostics to the console; CLI tools may

### `tests/Ntilde.VT.Tests/` + `tests/Ntilde.Rendering.Tests/`

**Own** the fast unit suites for VT and Rendering — designed to run in seconds, no Avalonia in the dependency closure, suitable for tight inner-loop iteration.

### `tests/Ntilde.Platform.Tests/` + `tests/Ntilde.App.Tests/`

**Own** integration coverage. Platform.Tests is the SSH + platform-utilities suite; App.Tests is the full Avalonia-headless integration suite (replay regressions, golden PNGs, command-assist harnesses, shell-integration tests).

### `tests/Ntilde.McpServer.Tests/`

**Owns** the MCP tool surface: tool behaviour, input validation, the connection-profile schema-drift guard, and a stdio end-to-end test that exercises the real protocol rather than a mock.

### `tests/Ntilde.Benchmarks/` + `tests/Ntilde.ExternalSuites/`

Standalone Exes — not test libraries — used for performance benchmarking (BenchmarkDotNet) and external-scenario drivers (Vttest, native SSH transcripts). Not discovered by `dotnet test`.

---

## Guiding Rule

> If tests disagree with code, tests are correct.

> If documentation disagrees with code, **add an architecture test that catches the disagreement**. Then fix whichever side was wrong.

> If code disagrees with the architecture-test layer, the change must un-skip a known violation or add a new rule. Silently changing layering is never the right move.
