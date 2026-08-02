# NovaTerminal – Module Ownership & Invariant Map

Each assembly owns specific invariants. Layering rules are encoded as
[NetArchTest](https://github.com/BenMorris/NetArchTest) facts in
`tests/NovaTerminal.Architecture.Tests/`. Behavioral invariants are
encoded as unit/integration tests in each module's test suite.

Breaking an invariant is a bug, even if the UI appears correct.

The companion doc is `docs/ARCHITECTURE.md`. Update both when an
invariant changes.

---

## NovaTerminal.VT (`src/NovaTerminal.VT/`)

**Namespace:** `NovaTerminal.VT` (+ `.Export`, `.Links`, `.Storage` sub-namespaces)
**Depends on:** *(leaf — only BCL)*
**Public surface:** `AnsiParser`, `TerminalBuffer`, `TerminalRow`, `TerminalCell`, `BufferSnapshot`, `RenderSnapshots.*`, `ReplayModels.*`, `TerminalTheme`, `UnicodeWidth`

**Owns**
- VT/ANSI state machine and parser
- Main + alternate screen buffers
- Scrollback (`Buffer/ScrollbackPages.cs`) and lossless reflow (`TerminalBuffer.ReflowEngine.cs`)
- Cell/row semantics, grapheme cluster handling, Unicode width
- Buffer threading contract (`TerminalBuffer.Lock`, `ReaderWriterLockSlim`)

**Invariants** (enforced by architecture tests and `tests/NovaTerminal.VT.Tests/`)
- Deterministic parsing — same byte stream produces the same semantic ops
- Source of truth — renderers/replay/sessions read this, they don't replicate state
- Lossless reflow — resize never silently drops content
- Alternate-screen isolation — main buffer is preserved across alt entries/exits
- All read access requires holding `TerminalBuffer.Lock`; reads without the lock throw via `AssertLockHeld`
- **Lock re-entrancy contract:** `Lock` is a non-recursive `ReaderWriterLockSlim`. `EnterReadLockIfNeeded()` returns `false` (and acquires nothing) when a read *or* write lock is already held. `EnterWriteLockIfNeeded()` returns `false` only when a *write* lock is already held — calling it while holding a read lock throws `LockRecursionException` (upgrading is not supported), it does **not** return `false`. Both return `true` when they actually acquired the lock. The returned `bool` says *whether this call took the lock* — callers must pass it to the matching `Exit…IfNeeded(..., lockTaken)` and must **not** unlock when it is `false`. Treating a `false` return as "lock acquired" double-unlocks (or unlocks a caller's outer lock).
- **`GetRowAbsolute()` null contract:** returns `null` for any absolute row that has no persistent `TerminalRow` — including **paged-out scrollback rows** (scrollback lives in `ScrollbackPages`, not as row objects), out-of-range rows, and negative indices. To read scrollback content use the cell/grapheme accessors (`GetCellAbsolute`, `GetGraphemeAbsolute`), which page it in; callers that assume a non-null row for scrollback indices will NRE.
- No OS, PTY, rendering, or UI logic in this assembly (`Vt_must_be_a_leaf_assembly` arch test)
- All types in `NovaTerminal.VT.*` namespace (`Leaf_assembly_types_reside_in_its_own_namespace`, `NamespaceAlignmentTests.cs`)

**Test authority**
- Primary: `tests/NovaTerminal.VT.Tests/`
- Replay/regression coverage: `tests/NovaTerminal.App.Tests/ReplayTests/`, `tests/NovaTerminal.App.Tests/AnsiCorpusReplayTests.cs`
- Buffer/reflow coverage: `tests/NovaTerminal.App.Tests/Buffer/`, `tests/NovaTerminal.App.Tests/ReflowScenariosTests.cs`, `tests/NovaTerminal.App.Tests/BufferTests/`

---

## NovaTerminal.Replay (`src/NovaTerminal.Replay/`)

**Namespace:** `NovaTerminal.Replay`
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
- `tests/NovaTerminal.Platform.Tests/Replay/`
- `tests/NovaTerminal.App.Tests/ReplayTests/`
- `tests/NovaTerminal.App.Tests/Regressions/` (Midnight Commander, regression suite)

---

## NovaTerminal.Rendering (`src/NovaTerminal.Rendering/`)

**Namespace:** `NovaTerminal.Rendering`
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
- Primary: `tests/NovaTerminal.Rendering.Tests/` (Skia primitives that don't need a GPU context)
- Renderer metrics: `tests/NovaTerminal.App.Tests/RenderTests/RendererMetricsTests.cs`
- Golden PNG comparisons: `tests/NovaTerminal.App.Tests/RenderTests/GoldenSharedPngTests.cs`, `GoldenFontPngTests.cs`

> **Note:** Today the Avalonia renderer composition (`TerminalView`, `TerminalDrawOperation`) lives in `src/NovaTerminal.App/Shell/` — see `docs/ARCHITECTURE.md` § 14 Known Tech Debt, tracked as #113.

---

## NovaTerminal.Pty (`src/NovaTerminal.Pty/`)

**Namespace:** `NovaTerminal.Pty`
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
- `tests/NovaTerminal.App.Tests/PtySmokeTests.cs` (PtySmoke category — filtered out of default CI lane)
- `tests/NovaTerminal.ExternalSuites/Vttest/` (external scenario driver)
- `tests/NovaTerminal.App.Tests/Ssh/TerminalPaneRecordingTests.cs`

---

## NovaTerminal.Platform (`src/NovaTerminal.Platform/`)

**Namespace:** `NovaTerminal.Platform` (+ `.Input`, `.Paths`, `.Execution`, plus the SSH sub-tree)
**Depends on:** Pty
**Public surface:** `TerminalInputSender`, path mappers, process abstractions, the SSH stack (`Ssh/{Interactions,Launch,Models,Native,OpenSsh,Sessions,Storage,Transport}`)

**Owns**
- Input routing primitives (drop router, shell quoters, input sender)
- Path mapping (notably WSL ↔ Windows)
- Process abstraction (`IProcessRunner`)
- The entire SSH stack: native interop with `rusty_ssh.dll`, OpenSSH bridging, session factories, profile storage, transport
- Future home of `SessionBufferBinder` and other session-orchestration helpers

**Invariants**
- This is NOT the terminal engine (that's VT). Renamed from `NovaTerminal.Core` in #76 to end the three-way "Core" name overload.
- No Avalonia or Skia in the dependency closure
- SSH transports must satisfy `IRemoteTerminalTransport` so all SSH session implementations are interchangeable

**Test authority**
- Primary: `tests/NovaTerminal.Platform.Tests/`
- Docker-gated E2E: `tests/NovaTerminal.Platform.Tests/Ssh/NativeSshDockerE2eTests.cs` (skipped without Docker)
- App-side integration: `tests/NovaTerminal.App.Tests/Ssh/`, `tests/NovaTerminal.App.Tests/Input/`

---

## NovaTerminal.AgentHost.Contracts (`src/NovaTerminal.AgentHost.Contracts/`)

**Namespace:** `NovaTerminal.AgentHost.Contracts`
**Depends on:** *(leaf — only BCL)*
**Public surface:** `AgentHostProtocol`, `AgentHostDiscovery`, `AgentHostJsonContext`, `Frames.*`, and the contract DTO groups (`ActContracts`, `SessionContracts`, `StatusContracts`, `ReplayContracts`)

**Owns**
- The wire protocol between the app's agent host and any external client (today: the MCP server)
- Frame definitions, discovery, and the source-generated JSON serialization context

**Invariants** (enforced by `AgentHostContracts_must_be_a_leaf_assembly` and `AgentHostContracts_csproj_must_have_no_project_references`)
- Leaf assembly — no project references at all, so both sides of the wire can depend on it without pulling in a dependency graph
- Shared by App and McpServer: a breaking change here breaks the agent integration on both sides at once. Version the protocol rather than redefining a frame in place.

**Test authority**
- `tests/NovaTerminal.McpServer.Tests/AgentHostClientTests.cs`
- End-to-end over real stdio: `tests/NovaTerminal.McpServer.Tests/McpServerStdioE2ETests.cs`

---

## NovaTerminal.CommandAssist (`src/NovaTerminal.CommandAssist/`)

**Namespace:** `NovaTerminal.CommandAssist` (+ `.Application`, `.Domain`, `.Models`, `.Storage`, `.ShellIntegration`, `.ViewModels`)
**Depends on:** *(leaf — only BCL)*
**Public surface:** `CommandAssistController` and its three collaborators (`AssistSessionStateMachine` + `AssistSessionState`, `AssistSessionContext`, `CapturePipeline`, `SuggestionOrchestrator` + `SuggestionRefreshOutcome`), `CommandAssistAnchorCalculator` (+ `AssistRect`/`AssistPoint`/`AssistSize`), `AssistKey`/`AssistModifiers`, `CommandAssistModeRouter`, `CommandAssistKeyRouter`, `CommandAssistInsertionPlanner`, `CommandAssistResultBuilder`, `RecognizedCommandParser`, the `I*Store` / `I*Provider` / `ISuggestionEngine` / `ISecretsFilter` domain contracts and their local implementations, `IShellIntegrationProvider` + the four shell providers and bootstrap builders, `ShellIntegrationRegistry`, `ShellLifecycleTracker`, `JsonlHistoryStore`, `JsonSnippetStore`, `CommandAssistJsonContext` / `CommandAssistJsonLinesContext`, and the assist view-models
**Internals exposed to:** `NovaTerminal.App.Tests` only. The App is deliberately not granted
`InternalsVisibleTo`: the two helpers `TerminalPane` needs (`CommandAssistKeyRouter`,
`CommandAssistInsertionPlanner`) are public pure-static functions over public types, so the App
consumes this assembly through its published surface.

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
  `src/NovaTerminal.App/CommandAssist/Views/` under `NovaTerminal.CommandAssist.Views` — the one
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
- All types in `NovaTerminal.CommandAssist.*` (`Leaf_assembly_types_reside_in_its_own_namespace`);
  the App may only use that prefix for Views
  (`App_may_only_use_the_CommandAssist_prefix_for_Views`).

**Test authority**
- `tests/NovaTerminal.App.Tests/CommandAssist/` (kept there for now — the suite exercises the assist
  assembly and the App's `TerminalPane` wiring together)
- Architecture invariants: `tests/NovaTerminal.Architecture.Tests/`

> **Note:** extracted from the App in #114 as Phase 0 of the Command Assist V2 rebuild
> (`docs/plans/2026-08-01-command-assist-v2-plan.md`). Phase 0b then replaced the static
> `CommandAssistInfrastructure` locator with the injected `CommandAssistServices`, unified ranking on
> `CommandAssistSuggestionEngine`, and swapped the history store for JSONL. Phase 0c split
> `CommandAssistController` into `AssistSessionStateMachine`, `CapturePipeline` and
> `SuggestionOrchestrator` (plan task 4), completing Phase 0.

---

## NovaTerminal.McpServer (`src/NovaTerminal.McpServer/`)

**Namespace:** `NovaTerminal.McpServer` (+ `.Tools`)
**Depends on:** AgentHost.Contracts
**Public surface:** `Program` (stdio entry point), `AgentHostClient`, `RepoContext`, and the tool groups under `Tools/` (`SessionTools`, `VtTools`, `ThemeTools`, `SettingsTools`, `ConnectionProfileTools`, `ProjectTools`, `WorkflowTools`)

**Owns**
- The opt-in MCP server that lets external agents observe live terminal sessions, and — behind a separate opt-in — drive them
- Tool schemas exposed over MCP, and their validation of caller input
- Talking to the running app through `AgentHostClient` over the AgentHost protocol

**Invariants**
- **Does not reference App, VT, Pty, or Rendering.** It is a client of the running app over the wire, not an in-process consumer — so it can never reach into terminal state directly.
- Observe and act are separately gated. A tool that mutates session state belongs behind the act opt-in.
- Tool schemas are part of the public contract: `ConnectionProfileDriftGuardTests` exists to catch schema drift against the app's real profile shape.

**Test authority**
- Primary: `tests/NovaTerminal.McpServer.Tests/`
- Schema-drift guard: `ConnectionProfileDriftGuardTests.cs`
- Stdio end-to-end: `McpServerStdioE2ETests.cs`

> **Note:** the dev companion runs the server from `bin/`, so a connected client
> can hold a lock that blocks repo builds — tracked as #211. See
> `docs/mcp-dev-companion.md`.

---

## NovaTerminal.App (`src/NovaTerminal.App/`)

**Namespace:** `NovaTerminal` (NOT `NovaTerminal.App` — see test-root-namespace note in `NovaTerminal.App.Tests`)
**Depends on:** Platform, VT, Rendering, Pty, Replay, AgentHost.Contracts, CommandAssist, Avalonia 12.0.4, SkiaSharp 3.119.4
**Public surface:** `App`, `MainWindow`, `TerminalPane`, settings window, theme manager, command palette, command-assist controller, profile importers, startup orchestrator

**Owns**
- Avalonia UI: windows, controls, view-models
- The currently-in-App renderer composition: `Shell/TerminalView.cs`, `Shell/TerminalDrawOperation.cs` (slated to move to Rendering — #113)
- Theme management and bundled fonts
- Profile import/export (Alacritty, iTerm2, Windows Terminal)
- Command palette and shortcuts
- The Command Assist *presentation* layer only: `CommandAssist/Views/` (Avalonia `UserControl`s),
  the `TerminalPane` wiring, and `Shell/CommandAssistServices.cs` as the composition root.
  Everything else moved to `NovaTerminal.CommandAssist` in #114.
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
- `tests/NovaTerminal.App.Tests/` (the largest suite)
- xunit.v3 + `Avalonia.Headless.XUnit 12.0.4`; **do not downgrade** the Avalonia stack below 12.0.4 — earlier versions leak the headless dispatcher and hang `dotnet test`

---

## NovaTerminal.Cli (`src/NovaTerminal.Cli/`)

**Namespace:** `NovaTerminal.Cli`
**Depends on:** App
**Public surface:** `Program` (Main entry point)

**Owns**
- Headless CLI entry — used for `vt-report` and automation
- Today the CLI shim is built by the App project via the `BuildCliShim` MSBuild target and copied into App's output as a sidecar
- Reaches into App via `InternalsVisibleTo("NovaTerminal.Cli")`

**Invariants**
- This dependency direction is **inverted** — see `docs/ARCHITECTURE.md` § 14 Known Tech Debt. A `NovaTerminal.Bootstrap` library should mediate.

**Test authority**
- `tests/NovaTerminal.App.Tests/VtReportCliTests.cs`
- Console-output rules for CLI vs GUI assemblies: `tests/NovaTerminal.Architecture.Tests/DiagnosticSinkTests.cs`

---

## NovaTerminal.Conformance (`src/NovaTerminal.Conformance/`)

**Namespace:** `NovaTerminal.Conformance`
**Depends on:** *(standalone Exe, no project references)*
**Public surface:** `VtConformanceReportTool`, `VtConformanceCli`

**Owns**
- VT conformance matrix parser (reads `docs/vt_coverage_matrix.md`)
- Report generator (writes `src/NovaTerminal.App/Resources/vt-conformance-report.json`)
- Evidence-link validator (fails CI if a matrix row claims a test file that doesn't exist)

**Invariants**
- Standalone tool — no library dependencies on the rest of the assemblies; consumed via project references from test projects (which run it in-process for validation)
- The shipped `vt-conformance-report.json` artifact's `matrixSha256` must match a fresh re-run on `vt_coverage_matrix.md` — verified by `tests/NovaTerminal.App.Tests/VtReportCliTests.ShippedArtifact_MatchesFreshToolOutput`

**Test authority**
- `tests/NovaTerminal.Platform.Tests/Conformance/VtConformanceToolTests.cs`
- `tests/NovaTerminal.App.Tests/VtReportCliTests.cs`

---

## Tests (First-Class Owners)

### `tests/NovaTerminal.Architecture.Tests/`

**Owns** the layering and namespace-alignment rules. Adding a new architectural invariant means adding a fact here. See `docs/ARCHITECTURE.md` § 12 for the current enforced rule set.

Four files, by concern:
- `LayeringTests.cs` — assembly-level dependency rules (`Vt_must_be_a_leaf_assembly`, `Pty_must_not_depend_on_Vt`, …)
- `ProjectFileLayeringTests.cs` — the same rules asserted against the `.csproj` files, so a stray `ProjectReference` fails even if no code uses it yet
- `NamespaceAlignmentTests.cs` — one assembly, one namespace prefix
- `DiagnosticSinkTests.cs` — GUI and library code must not write diagnostics to the console; CLI tools may

### `tests/NovaTerminal.VT.Tests/` + `tests/NovaTerminal.Rendering.Tests/`

**Own** the fast unit suites for VT and Rendering — designed to run in seconds, no Avalonia in the dependency closure, suitable for tight inner-loop iteration.

### `tests/NovaTerminal.Platform.Tests/` + `tests/NovaTerminal.App.Tests/`

**Own** integration coverage. Platform.Tests is the SSH + platform-utilities suite; App.Tests is the full Avalonia-headless integration suite (replay regressions, golden PNGs, command-assist harnesses, shell-integration tests).

### `tests/NovaTerminal.McpServer.Tests/`

**Owns** the MCP tool surface: tool behaviour, input validation, the connection-profile schema-drift guard, and a stdio end-to-end test that exercises the real protocol rather than a mock.

### `tests/NovaTerminal.Benchmarks/` + `tests/NovaTerminal.ExternalSuites/`

Standalone Exes — not test libraries — used for performance benchmarking (BenchmarkDotNet) and external-scenario drivers (Vttest, native SSH transcripts). Not discovered by `dotnet test`.

---

## Guiding Rule

> If tests disagree with code, tests are correct.

> If documentation disagrees with code, **add an architecture test that catches the disagreement**. Then fix whichever side was wrong.

> If code disagrees with the architecture-test layer, the change must un-skip a known violation or add a new rule. Silently changing layering is never the right move.
