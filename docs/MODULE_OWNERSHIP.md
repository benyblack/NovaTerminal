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

**Namespace:** `NovaTerminal.VT` (+ `.Export`, `.Storage` sub-namespaces)
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
- No OS, PTY, rendering, or UI logic in this assembly (`Vt_must_be_a_leaf_assembly` arch test)
- All types in `NovaTerminal.VT.*` namespace (`All_VT_types_use_NovaTerminal_VT_namespace`)

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

> **Note:** Today the Avalonia renderer composition (`TerminalView`, `TerminalDrawOperation`) lives in `src/NovaTerminal.App/Core/` — see `docs/ARCHITECTURE.md` § 14 Known Tech Debt.

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

**Namespace:** `NovaTerminal.Platform` (+ `.Input`, `.Paths`, `.Process`, `.Execution`, plus the SSH sub-tree)
**Depends on:** Pty
**Public surface:** `TerminalInputSender`, path mappers, process abstractions, the SSH stack (`Ssh/{Interactions,Launch,Models,Native,OpenSsh,Sessions,Storage,Transport}`)

**Owns**
- Input routing primitives (drop router, shell quoters, input sender)
- Path mapping (notably WSL ↔ Windows)
- Process abstraction (`IProcessRunner`)
- The entire SSH stack: native interop with `rusty_ssh.dll`, OpenSSH bridging, session factories, profile storage, transport
- Future home of `SessionBufferBinder` and other session-orchestration helpers

**Invariants**
- Name is a historical artifact — this is NOT the terminal engine (that's VT). Rename to `NovaTerminal.Platform` is a planned follow-up.
- No Avalonia or Skia in the dependency closure
- SSH transports must satisfy `IRemoteTerminalTransport` so all SSH session implementations are interchangeable

**Test authority**
- Primary: `tests/NovaTerminal.Platform.Tests/`
- Docker-gated E2E: `tests/NovaTerminal.Platform.Tests/Ssh/NativeSshDockerE2eTests.cs` (skipped without Docker)
- App-side integration: `tests/NovaTerminal.App.Tests/Ssh/`, `tests/NovaTerminal.App.Tests/Input/`

---

## NovaTerminal.App (`src/NovaTerminal.App/`)

**Namespace:** `NovaTerminal` (NOT `NovaTerminal.App` — see test-root-namespace note in `NovaTerminal.App.Tests`)
**Depends on:** Core, VT, Rendering, Pty, Replay, Avalonia 12.0.4, SkiaSharp 3.119.4
**Public surface:** `App`, `MainWindow`, `TerminalPane`, settings window, theme manager, command palette, command-assist controller, profile importers, startup orchestrator

**Owns**
- Avalonia UI: windows, controls, view-models
- The currently-in-App renderer composition: `Core/TerminalView.cs`, `Core/TerminalDrawOperation.cs` (slated to move to Rendering)
- Theme management and bundled fonts
- Profile import/export (Alacritty, iTerm2, Windows Terminal)
- Command palette and shortcuts
- CommandAssist (full sub-architecture under `CommandAssist/{Application,Domain,Models,Storage,ShellIntegration,ViewModels,Views}`)
- Startup orchestration (eight `Startup*.cs` files in `Core/`)
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
- `tests/NovaTerminal.App.Tests/Core/CliConsoleBindingsTests.cs`

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

### `tests/NovaTerminal.VT.Tests/` + `tests/NovaTerminal.Rendering.Tests/`

**Own** the fast unit suites for VT and Rendering — designed to run in seconds, no Avalonia in the dependency closure, suitable for tight inner-loop iteration.

### `tests/NovaTerminal.Platform.Tests/` + `tests/NovaTerminal.App.Tests/`

**Own** integration coverage. Core.Tests is the SSH + platform-utilities suite; App.Tests is the full Avalonia-headless integration suite (replay regressions, golden PNGs, command-assist harnesses, shell-integration tests).

### `tests/NovaTerminal.Benchmarks/` + `tests/NovaTerminal.ExternalSuites/`

Standalone Exes — not test libraries — used for performance benchmarking (BenchmarkDotNet) and external-scenario drivers (Vttest, native SSH transcripts). Not discovered by `dotnet test`.

---

## Guiding Rule

> If tests disagree with code, tests are correct.

> If documentation disagrees with code, **add an architecture test that catches the disagreement**. Then fix whichever side was wrong.

> If code disagrees with the architecture-test layer, the change must un-skip a known violation or add a new rule. Silently changing layering is never the right move.
