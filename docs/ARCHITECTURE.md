# NovaTerminal – Architecture & Design Rationale

This document describes the **internal architecture**, **invariants**, and **design trade-offs**
of NovaTerminal.

It is authoritative for contributors and automated agents.

---

## 1. Architectural Goals

NovaTerminal is designed to satisfy four non-negotiable goals:

1. **Deterministic terminal semantics**
2. **Cross-platform behavioral parity**
3. **Incremental, flicker-free rendering**
4. **Test-enforced correctness**

All architectural decisions follow from these goals.

---

## 2. Assembly Graph

NovaTerminal is structured as eight focused .NET assemblies plus standalone tools. The dependency graph is acyclic. Each assembly's namespace matches its assembly name (enforced by `tests/NovaTerminal.Architecture.Tests/NamespaceAlignmentTests`).

```
Cli ──► App ──► { Platform, VT, Rendering, Pty, Replay }
                  │          │     │         │     │
                  └──► Pty   │     │         │     │
                             └─VT◄──┘        │     │
                                            └─VT◄──┘
                                                  Replay◄─Pty

Conformance  (standalone Exe – vt-report tool)
```

Concretely, from the `.csproj` graph:

| Assembly | Depends on | Owns |
|---|---|---|
| `NovaTerminal.VT` | (leaf) | VT/ANSI parser, terminal buffer state, scrollback, reflow |
| `NovaTerminal.Replay` | VT | Session recording/playback, snapshots, replay format v2 |
| `NovaTerminal.Rendering` | VT, SkiaSharp | Skia glyph atlas/cache, pixel grid, sixel decoder |
| `NovaTerminal.Pty` | Replay | PTY transport: rust-PTY adapter, session contracts. **Does not depend on VT** — see arch test `Pty_must_not_depend_on_Vt` |
| `NovaTerminal.Platform` | Pty | Platform-utilities: input routing, path mapping, SSH transport/sessions, credential vault |
| `NovaTerminal.App` | Platform, VT, Rendering, Pty, Replay | Avalonia UI shell: windows, controls, command palette, settings, themes, command-assist |
| `NovaTerminal.Cli` | App | Headless CLI shim (`vt-report` etc.) |
| `NovaTerminal.Conformance` | (standalone Exe) | VT conformance matrix tool used by tests and CI |

### Hard Rule

> **No OS-specific logic in `NovaTerminal.VT`.** VT is the leaf — no Avalonia, no Skia, no native interop, no I/O. Enforced by `LayeringTests.Vt_must_be_a_leaf_assembly`.

---

## 3. Terminal Engine — `NovaTerminal.VT`

The terminal engine is the **single source of truth** for terminal semantics. The Avalonia UI is downstream — if the rendered pixels look wrong, the bug is in the VT engine, not the renderer.

### 3.1 Responsibilities
- Parse VT / ANSI escape sequences (`src/NovaTerminal.VT/AnsiParser.cs`)
- Maintain deterministic screen state in `TerminalBuffer` (`src/NovaTerminal.VT/TerminalBuffer.cs` + partial files)
- Handle alternate-screen transitions
- Manage scrollback (`src/NovaTerminal.VT/Buffer/ScrollbackPages.cs`)
- Perform lossless reflow on resize (`src/NovaTerminal.VT/TerminalBuffer.ReflowEngine.cs`)
- Provide read-only snapshots for rendering and replay

### 3.2 Components

#### `AnsiParser`

State machine for ESC, CSI, OSC, DEC-private modes. Emits **semantic operations** against `TerminalBuffer`, not rendering actions.

**Key invariant:** Same byte stream → same sequence of semantic operations.

#### `TerminalBuffer`

Applies semantic operations to terminal state. Maintains cursor, modes, tab stops, margins. Owns main + alternate screen buffers and scrollback.

**Key invariants**
- Alternate screen is fully isolated from main screen
- Scrollback is immutable once flushed
- Buffer state is renderer-agnostic — no Skia, no Avalonia in the type surface
- All read access requires holding `TerminalBuffer.Lock` (`ReaderWriterLockSlim`); reads without the lock throw `AssertLockHeld`

#### `TerminalRow` / `TerminalCell`

Row/cell semantics. Cell equality is defined only on render-affecting state — no renderer fields allowed.

### 3.3 Determinism

The VT engine is **purely deterministic**:

- No timers
- No threading assumptions beyond the documented lock
- No OS calls
- No UI interactions

This enables deterministic replay, cross-platform parity testing, and safe refactoring.

---

## 4. Recording / Playback — `NovaTerminal.Replay`

A focused module that owns the record/replay file format and the snapshot/byte-stream coupling. References VT for snapshot types (`BufferSnapshot`), but is independent of Pty, Rendering, and App.

`PtyRecorder`, `ReplayWriter`, `ReplayReader`, `ReplayRunner` live here. Goldens and regression suites consume `ReplayRunner`.

---

## 5. Renderer Primitives — `NovaTerminal.Rendering`

Skia-only helpers: glyph atlas (`GlyphAtlas`), glyph cache (`GlyphCache`), pixel grid math (`PixelGrid`), font wrappers (`SharedSKFont`, `SharedSKTypeface`), image registry, sixel decoder, render performance metrics.

This module is intentionally **Avalonia-agnostic**. The actual Avalonia `DrawOperation` and viewport (`TerminalDrawOperation`, `TerminalView`) live in `src/NovaTerminal.App/Shell/` today — see Known Tech Debt below for the planned extraction.

**Invariants** (enforced by `LayeringTests.Rendering_only_depends_on_Vt_and_Skia`)
- Rendering is a pure function of (buffer snapshot, metrics, theme).
- No semantic decisions — if the buffer is wrong, the renderer cannot fix it.
- No Avalonia in the dependency closure.

---

## 6. PTY Transport — `NovaTerminal.Pty`

Native OS integration via the Rust PTY library (`rusty_pty.dll`/`.so`/`.dylib`). Defines the session contracts and `RustPtySession` as the canonical implementation.

`ITerminalSession` is a composite of four narrow interfaces:

| Interface | Concern |
|---|---|
| `ITerminalIO` | `SendInput(string)`, `OnOutputReceived` event |
| `ITerminalLifecycle` | `Id`, `Resize`, process state, `OnExit`, `IDisposable` |
| `ITerminalShellMetadata` | `ShellCommand`, `ShellArguments` |
| `ITerminalRecorder` | `StartRecording(filePath)`, `StopRecording`, `IsRecording` |

**Invariants** (enforced by `LayeringTests.Pty_must_not_depend_on_Vt`)
- Pty does not reference VT. The session reports raw byte/string output; parsing it into a `TerminalBuffer` is the consumer's job.
- IO is bounded and non-blocking.
- Recording in this layer captures the raw byte stream only — buffer-snapshot recording is an orchestration-layer concern and is not currently wired up.

---

## 7. Platform / SSH — `NovaTerminal.Platform`

This is **not** the terminal engine. It's a platform-utilities library: input routing (`Input/`), path mapping (`Paths/WslPathMapper`), the SSH stack (`Ssh/{Native,OpenSsh,Sessions,Storage,Transport}`), and the credential vault. It was renamed from `NovaTerminal.Core` to `NovaTerminal.Platform` (issue #76) to end the three-way "Core" name overload; the original "Core" terminal engine had earlier been renamed to `NovaTerminal.VT` during the namespace-alignment work.

This is also where session-orchestration helpers (such as a future `SessionBufferBinder`) belong.

---

## 8. UI Shell — `NovaTerminal.App`

Avalonia 12.0.4 application. Hosts windows, controls (`TerminalPane`, `MainWindow`, `SettingsWindow`), view-models, themes, and command-palette. Composes everything below.

Shell composition glue (startup orchestration, app paths/logging/services, session & workspace managers, theme manager, command registry, profiles, the Avalonia view host) lives in `src/NovaTerminal.App/Shell/`, namespace `NovaTerminal.Shell` — renamed from `App/Core/` + `NovaTerminal.Core` (issue #76).

### Responsibilities
- Window and pane management
- Input routing (keyboard, mouse, drag-and-drop)
- Selection handling
- Settings UI, theming, profiles
- Command palette + command-assist (in-process shell-integration helpers)
- Pane resizing (pixel → row/col calculation)

### Non-Responsibilities
- VT parsing (delegated to VT)
- Buffer mutation (except via explicit APIs)
- Rendering logic (Skia primitives in Rendering; Avalonia binding shell here is intentionally thin — though see Known Tech Debt)

---

## 9. CLI Shim — `NovaTerminal.Cli`

Headless tooling entry point used for `vt-report` and other automation use cases. Currently references `NovaTerminal.App`; a `NovaTerminal.Bootstrap` library should mediate so neither side reaches into the other (see Known Tech Debt).

---

## 10. Deterministic Replay (Architectural Feature)

Replay is a **core architectural feature**, not a debug tool.

```
PTY byte stream
   ↓
[Recorder]                  -- ReplayWriter (NovaTerminal.Replay)
   ↓
Replay file
   ↓
AnsiParser                  -- NovaTerminal.VT
   ↓
TerminalBuffer              -- NovaTerminal.VT
   ↓
Buffer snapshot             -- compared in CI
```

Snapshots capture visible screen content, attributes, cursor state, alt/main flag, and minimal scrollback.

---

## 11. Cross-Platform Parity

NovaTerminal enforces **behavioral parity** across OSes:

| Aspect | Must match across OSes |
|---|---|
| VT parsing | Yes |
| Buffer state | Yes |
| Wrapping & reflow | Yes |
| Search semantics | Yes |
| Rendering semantics | Yes |

Allowed differences: window chrome, hotkeys, blur/transparency, credential storage backend.

---

## 12. Architecture-Tests Safety Net

`tests/NovaTerminal.Architecture.Tests/` uses [NetArchTest.Rules](https://github.com/BenMorris/NetArchTest) to encode layering and namespace rules as xUnit facts. Today's enforced rules:

**`LayeringTests`**
- `Vt_must_be_a_leaf_assembly`
- `Replay_only_depends_on_Vt`
- `Rendering_only_depends_on_Vt_and_Skia`
- `Pty_must_not_depend_on_Vt`
- `No_production_assembly_references_test_assemblies`

**`NamespaceAlignmentTests`**
- `Leaf_assembly_types_reside_in_its_own_namespace` (Theory: VT, Replay, Rendering, Pty, Platform)
- `No_two_assemblies_share_a_namespace_prefix`

**`ProjectFileLayeringTests`**
- `Pty_csproj_must_not_reference_Vt`
- `Replay_csproj_only_references_Vt`
- `Rendering_csproj_only_references_Vt`
- `Vt_csproj_must_have_no_project_references`

Adding a new layering invariant means adding a new fact. Reverting one of these accidentally fails CI.

---

## 13. Test Layout

| Project | What it tests |
|---|---|
| `NovaTerminal.VT.Tests` | Fast unit suite for parser/buffer in isolation (no Avalonia, no Skia) |
| `NovaTerminal.Rendering.Tests` | Skia primitives that don't need a GPU context |
| `NovaTerminal.Platform.Tests` | Platform utilities + SSH; includes Docker-gated E2E (skipped without Docker) |
| `NovaTerminal.App.Tests` | App-level integration — Avalonia-headless tests, replay regressions, golden PNG comparisons, command-assist |
| `NovaTerminal.Architecture.Tests` | Layering and namespace rules (Section 12) |
| `NovaTerminal.Benchmarks` | BenchmarkDotNet perf benchmarks (Exe, not auto-discovered by `dotnet test`) |
| `NovaTerminal.ExternalSuites` | Vttest and Native-SSH external scenario drivers (Exe) |

App-side tests use xunit.v3 + `Avalonia.Headless.XUnit 12.0.4` (which carries the dispatcher-cleanup fix; **do not downgrade** below 12.0.4 — earlier versions leak headless dispatcher threads and hang `dotnet test` under captured-stdout runners).

---

## 14. Known Tech Debt

These are tracked in follow-up plans under `docs/plans/`:

- **Renderer composition still lives in App.** `src/NovaTerminal.App/Shell/TerminalView.cs` (1,912 LOC) and `TerminalDrawOperation.cs` (2,723 LOC) implement the Skia-backed Avalonia renderer. They belong in `NovaTerminal.Rendering` behind a thin Avalonia binding shell. Planned extraction: `2026-MM-DD-renderer-composition-extraction-plan.md`.
- **SSH is fragmented across Platform and App.** `Platform/Ssh/` holds the transport, while `App/Services/Ssh/`, `App/ViewModels/Ssh/`, `App/Views/Ssh/`, and `App/Shell/{SftpService,VaultService,SshAskPassCommand}.cs` hold the user-facing surface. A `NovaTerminal.Ssh` (or `.Remote`) assembly would consolidate the non-UI portion.
- **CommandAssist** has its own Application/Domain/Storage layering inside `App/CommandAssist/`. Candidate for `NovaTerminal.CommandAssist` extraction.
- **CLI ↔ App reference direction.** `Cli` currently references `App` and is built via a nested MSBuild target (`BuildCliShim` in `App.csproj`). A `NovaTerminal.Bootstrap` library should mediate so `Cli → Bootstrap ← App` replaces `Cli → App`.
- **Byte vs string at the PTY boundary.** `ITerminalIO.SendInput(string)` and `OnOutputReceived(Action<string>)` lose information at the byte-vs-codepoint boundary (UTF-8 split across reads, embedded NULs, lone surrogates). Migrating to `ReadOnlySpan<byte>` / `Action<ReadOnlyMemory<byte>>` is the planned follow-up to Phase 5.
- **Buffer-snapshot recording.** Phase 5 removed `ITerminalSession.AttachBuffer` / `TakeSnapshot`. The byte-stream is still recorded; buffer snapshots at recording start/stop are gone. Re-introducing them as an orchestration helper (likely in `Replay` or `Platform`) is a small follow-up.
- **`MainWindow.axaml.cs` is 5,259 LOC, `TerminalPane.axaml.cs` 2,572 LOC, `SettingsWindow.axaml.cs` 1,672 LOC.** These code-behinds contain business logic that should live in services and view-models.
- **`TerminalBuffer` is split across 10 partial files (~5K LOC).** Several of the partials (`WritePath`, `ReflowEngine`, `ThreadingAndInvalidation`, `TabStops`) want to be collaborators rather than partials.

The 2026-05-28 architecture review (`docs/plans/2026-05-28-architecture-module-boundaries-review.md`) catalogs all of the above and ranks them by leverage.

---

## 15. Failure Modes Designed Against

- "Looks fine on my machine"
- Resize-induced corruption
- Alternate-screen leakage
- Renderer-driven semantic drift
- Platform-specific behavior divergence
- Silent assembly-boundary refactors (caught by Section 12 arch tests)

---

## 16. Why This Architecture

This architecture is intentionally **boring**. That is a feature.

- Ghostty and WezTerm succeed because they are predictable.
- Users forgive missing features. They do not forgive broken terminals.
- NovaTerminal optimizes for **trust first**, features second.

---

## 17. Summary

NovaTerminal is:
- deterministic by design
- cross-platform by construction
- test-gated by policy (arch tests + replay regression suite)
- incremental by default

> UI attracts users. Correctness keeps them.

Read this before making architectural changes. Add an arch test before reaching for documentation as the enforcement mechanism — docs rot, tests don't.
