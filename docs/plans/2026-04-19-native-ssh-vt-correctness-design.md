# Native SSH VT Correctness Hardening Design

**Date:** 2026-04-19

## Goal

Restore VT-correct behavior for native SSH sessions, starting with deterministic session-boundary hardening and then adding end-to-end verification, without changing the terminal parser or renderer unless the evidence proves that is necessary.

## Scope

- Native SSH backend only
- `NativeSshSession` behavior at the `ITerminalSession` contract boundary
- Fullscreen / alternate-screen transitions
- Resize stability and non-fatal resize handling
- Post-command newline / prompt-return correctness
- Deterministic test coverage for Step 1
- Planned native SSH end-to-end verification for Step 2

## Non-Goals

- No VT parser expansion or rendering refactor by default
- No OpenSSH behavior changes in this pass
- No Command Assist behavior changes
- No generic remote-session abstraction across all backends
- No live network-dependent CI lane in Step 1

## Existing Context

- Native SSH does not reuse the PTY session path. It emits terminal output from [NativeSshSession.cs](/d:/projects/nova2/src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs) directly through `OnOutputReceived`.
- OpenSSH-backed SSH sessions reuse the PTY-backed `ITerminalSession` behavior through [OpenSshSession.cs](/d:/projects/nova2/src/NovaTerminal.Core/Ssh/Sessions/OpenSshSession.cs) and [RustPtySession.cs](/d:/projects/nova2/src/NovaTerminal.Pty/RustPtySession.cs).
- The terminal UI consumes session output by calling `Parser.Process(text)` in [TerminalPane.axaml.cs](/d:/projects/nova2/src/NovaTerminal.App/Controls/TerminalPane.axaml.cs).
- Existing VT correctness coverage already defines the desired alternate-screen and resize behavior, including [AlternateScreenTests.cs](/d:/projects/nova2/tests/NovaTerminal.Tests/AlternateScreenTests.cs) and [MidnightCommanderTests.cs](/d:/projects/nova2/tests/NovaTerminal.Tests/Regressions/MidnightCommanderTests.cs).
- Native SSH already has some core tests in [NativeSshSessionTests.cs](/d:/projects/nova2/tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionTests.cs), including incremental UTF-8 decoding and resize forwarding, but it does not yet pin fullscreen chunking, post-command newline behavior, or non-fatal resize failure semantics.

## Problem

Recent native SSH work introduced correctness regressions that do not appear on the OpenSSH/PTy-backed path:

- fullscreen / alternate-screen TUIs can leave the terminal in a bad state
- resize can crash or surface exceptions through the UI path
- command completion can leave newline / prompt-return artifacts

The highest-probability fault line is the native session boundary itself:

- native SSH owns its own poll loop
- native SSH performs its own UTF-8 decoding and output replay behavior
- native SSH forwards resize directly to interop and currently allows interop failure to throw

That makes `NativeSshSession` the correct first target. It is narrow, backend-specific, and consistent with the repo rule to avoid changing VT parsing/rendering unless required.

## Two-Step Approach

### Step 1: Session-Boundary Hardening

Harden native SSH at the managed session boundary so it behaves like the existing PTY/OpenSSH path at `ITerminalSession`.

This step should:

- keep the fix localized to `NativeSshSession`
- preserve current parser / buffer / renderer behavior as the correctness oracle
- add deterministic tests for the reported regressions
- make resize non-fatal even when native interop rejects a resize

### Step 2: End-to-End Native SSH Verification

After Step 1 stabilizes the boundary contract, add an end-to-end verification layer that exercises the real native SSH event path and confirms the Step 1 behavior still holds when the Rust worker, event polling, and UI-driven resize timing are involved together.

This second step should remain scoped to native SSH correctness, not broad SSH feature expansion.

## Recommended Step 1 Architecture

### 1. Keep `NativeSshSession` as the contract adapter

`NativeSshSession` should be the place that normalizes native event delivery into the session contract expected by the rest of the app.

Responsibilities to keep or tighten here:

- incremental UTF-8 decoding across poll events
- output replay for late subscribers
- output emission ordering
- safe resize forwarding
- graceful handling of native errors that should not kill the UI path

### 2. Keep VT behavior as the oracle, not the fix surface

The parser and buffer already define correct alternate-screen and newline behavior through existing tests. Step 1 should feed native SSH output into the same `AnsiParser` / `TerminalBuffer` path and assert the same outcomes.

That means Step 1 proves:

- native SSH emits correct terminal text for the existing VT engine

not:

- rewrite the VT engine to accommodate native SSH regressions

### 3. Prefer deterministic fake-native tests over live SSH

Step 1 should use a fake `INativeSshInterop` to script native events such as:

- split data chunks
- chunked alternate-screen sequences
- resize failures
- exit and closed ordering

This gives reproducible coverage without network timing or environment-specific SSH behavior.

## Step 1 Testing Strategy

Add failing tests first, then implement the minimal production changes.

### Test A: Split UTF-8 across native events

A multibyte glyph split across two native `Data` events must emit a single decoded character with no replacement glyph and no dropped bytes.

This behavior is already partially covered and should remain pinned.

### Test B: Chunked fullscreen / alternate-screen parity

Alternate-screen enter and exit sequences split across awkward event boundaries must still yield the same buffer behavior as the current VT tests:

- no scrollback pollution
- main screen restored correctly
- prompt visible after exit

### Test C: Resize failure is non-fatal

If native interop throws or rejects a resize, `NativeSshSession.Resize(...)` must not throw back into the UI caller. The session should stay usable, log the failure, and continue processing later native output.

### Test D: Post-command newline / prompt-return parity

Typical shell output patterns such as command echo plus `\r\n`, prompt redraws, and carriage-return-driven updates must not leave extra blank lines or a broken prompt state when driven through native SSH output delivery.

## Step 2 Verification Strategy

Use the existing external-suite pattern in [NovaTerminal.ExternalSuites](/d:/projects/nova2/tests/NovaTerminal.ExternalSuites) to add a small native SSH verification harness.

The purpose is to verify the whole path with repeatable scripted scenarios, not to rely on flaky live-network CI.

Target scenarios:

- fullscreen TUI enter / exit
- resize bursts during fullscreen use
- command completion returning to a prompt

Acceptance for Step 2:

- native SSH fullscreen exit returns to a sane prompt state
- resize bursts do not crash and do not strand the buffer in a broken layout
- post-command newline behavior matches the Step 1 buffer-level oracle
- the suite is stable enough to run in CI without a live SSH dependency

## Risks

### False Boundary Assumption

If the new Step 1 tests show that the session boundary is already correct and the bug still reproduces, the next target should be the native Rust event pipeline or the app-level resize timing, not the parser.

### Overfitting To Synthetic Tests

A fake interop can prove contract behavior, but not real worker timing. That is why Step 2 remains necessary.

### Accidental Cross-Backend Change

This work should stay native-backend-specific. Any temptation to “normalize” all remote sessions in this pass should be treated as scope creep unless Step 1 evidence requires it.

## Recommended Outcome

Ship this as a two-step solution:

1. Step 1 hardens native SSH session correctness with deterministic tests and no VT-core changes.
2. Step 2 adds native SSH end-to-end verification using the external-suite pattern.

That sequence gives the fastest path to stopping the current regressions while preserving the terminal core architecture and leaving a clean route to broader native verification later.
