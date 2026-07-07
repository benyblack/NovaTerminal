# Agent Host A4 (Replay for Agents) Design

Milestone **A4** of `docs/agent-host/DIRECTION.md` — the moat: "debug what your
agent did in the terminal, frame by frame." An agent (or its human) can export
a session's recent activity as a standard NovaTerminal replay file and re-run
it headlessly with pixel/byte determinism. A3 (acting) is intentionally
skipped for now; A4 is still observe-only.

## Goal

- **Flight recorder:** while Agent Access is enabled, each session retains a
  bounded in-memory ring of its recent raw PTY output (+ resizes) — no disk
  writes until asked.
- **`novaterminal.export_replay`** — MCP tool that writes the ring to a
  standard v2 `.rec` file under a dedicated agent-exports folder and returns
  the path.
- **`NovaTerminal.Cli --replay <file>`** — headless: replay the file through
  the deterministic core and print the final screen (`BufferSnapshot`
  formatted text), for CI and agent postmortems.

The change must:

- reuse the existing replay format v2 and `PtyRecorder`/`ReplayRunner`
  machinery — no second format, no second code path for correctness
- preserve original inter-chunk timing (the format's `t` field), which
  requires explicit-timestamp write overloads on `PtyRecorder`
- respect layering: raw bytes exist only in `NovaTerminal.Pty`; Pty may
  reference Replay (it already does) but never VT
- stay observe-only and default-off: the flight ring only exists while the
  observe endpoint is running, and export is additionally gated by its own
  default-off setting per the DIRECTION permission table ("off; observe
  permission + explicit export action")

## Current State (verified)

- Recording taps **raw bytes** in `RustPtySession.ReadLoop`
  (`_recorder?.RecordChunk(buffer, read)`) before UTF-8 decoding; input and
  resize are also recorded. `PtyRecorder` (NovaTerminal.Replay) writes v2
  JSON-Lines but only to a file path and only with enqueue-time timestamps —
  no explicit-timestamp API.
- The App layer only ever sees decoded strings; **no rolling raw-byte history
  exists anywhere** today.
- `ReplayRunner.RunAsync(...)` in Virtual mode + `BufferSnapshot.Capture` +
  `ToFormattedString()` is exactly the headless-replay primitive; golden
  tests already consume it.
- CLI commands are static classes in `NovaTerminal.App/Shell`
  (`IsSupportedCliMode` / `Execute(args, stdout, stderr)`) dispatched from
  `NovaTerminal.Cli/Program.cs`.
- Manual recordings: `AppPaths.RecordingsDirectory`,
  `nova_rec_{yyyyMMdd_HHmmss}_{suffix}.rec`.

## Design

### Privacy stance (explicit)

Agent exports contain **output and resize events only — never input events**.
Manual user-initiated recordings keep recording keystrokes as today, but an
agent-triggered export must not exfiltrate typed secrets (passwords at
prompts). Replay rendering correctness needs only output + resize; input
events are ignored by the render path anyway. Documented in the tool
description and the settings toggle text.

### Flight recorder (NovaTerminal.Replay + Pty integration)

- **`FlightRecordingBuffer`** (new, `NovaTerminal.Replay`): thread-safe
  bounded ring of `(tMs, kind: chunk|resize, payload)` entries, bounded by
  total payload bytes (default 2 MiB per session, constant in contracts).
  Tracks the **geometry at the start of the retained window**: when trimming
  evicts a resize event, the window-start geometry advances — so an export is
  always self-consistent (header geometry = geometry at first retained
  event). Exposes `WriteTo(PtyRecorder recorder)` which rebases timestamps to
  the first retained event.
  - Lives in Replay (not Pty) so it can be unit-tested in isolation and
    reused; Pty already references Replay, and VT types are not needed.
- **`ITerminalFlightRecorder`** (new members on the Pty session surface):
  `EnableFlightRecording(long maxBytes)`, `DisableFlightRecording()`,
  `TryExportFlightRecording(string filePath, out FlightExportInfo info)`.
  Folded into `ITerminalSession` alongside `ITerminalRecorder`.
  `RustPtySession` feeds the ring at the existing byte-level tap points
  (`ReadLoop` chunk, `Resize`) — same places as `_recorder`, one extra
  null-check per chunk when disabled.
- **Lifecycle** (off-is-off, same pattern as the status sweep):
  `AgentHostService` enables flight recording on every registered session at
  `Start` and on `SessionRegistered`; disables at `Stop` and ignores
  unregistered sessions. The registration already publishes the session
  reference (the volatile lifecycle slot from A2 PR2 — widened to carry the
  session so the service can reach the flight-recorder surface without
  touching the pane).

### Protocol + MCP tool

- New method `exportReplay { paneId }` →
  `{ filePath, eventCount, firstEventMs, lastEventMs, truncatedAtStart }`
  (`truncatedAtStart` = ring had already evicted older data). Additive;
  protocol version stays 1.
- Server handler: requires the new setting (below) — otherwise a dedicated
  `exportDisabled` error code with guidance; resolves the registration,
  calls `TryExportFlightRecording` targeting
  `AppPaths.RecordingsDirectory/agent-exports/nova_rec_{stamp}_{suffix}.rec`
  (existing naming convention, dedicated subfolder so agent artifacts are
  visually separate from manual recordings).
- **Setting:** `AgentReplayExportEnabled` (default **false**), settings-window
  sub-toggle under Agent access; validator lists updated. Both toggles must
  be on for export to work — this is the "explicit export action" tier from
  the DIRECTION permission table.
- MCP tool `novaterminal.export_replay(paneId)` — returns the file path, the
  time range, the truncation flag, and a hint to replay it with
  `NovaTerminal.Cli --replay <path>`.

### Headless CLI

- **`ReplayCommand`** (`NovaTerminal.App/Shell`, dispatched from
  `NovaTerminal.Cli/Program.cs` after the existing two commands):
  `--replay <file> [--attributes] [--fast-forward-ms N]`.
  Virtual-mode `ReplayRunner.RunAsync` → UTF-8 decode → `AnsiParser.Process`
  → `buffer.Resize` on resize events → `BufferSnapshot.Capture(buffer,
  includeAttributes).ToFormattedString()` to stdout. Async bridged with
  `GetAwaiter().GetResult()` (established CLI constraint). Exit codes:
  0 success, 1 unreadable/truncated file (partial output still printed with a
  stderr warning, using `RunWithResultAsync`), 2 usage.

## Alternatives Considered

- **Export only when the user manually records** — no memory cost, but the
  moment an agent needs a postmortem is precisely when nobody was recording.
  The flight recorder is the feature.
- **Ring in the App layer from `OnOutputReceived` strings** — bytes are
  already decoded there; re-encoding loses invalid-UTF-8 sequences and splits
  differ from the wire. Rejected: determinism demands the byte-level tap.
- **Synthetic timestamps on export** (avoid touching `PtyRecorder`) — Virtual
  replay ignores timing, but Realtime playback in ReplayWindow and future
  frame-stepping would be garbage. Two small explicit-timestamp overloads are
  cheap and keep the format honest.
- **Including input events with a redaction pass** — redaction of terminal
  input is not reliably possible (no structure). Omit input entirely.

## Testing

- **Ring unit tests** (App.Tests/ReplayTests or AgentHost): byte-budget
  trimming, geometry tracking across evicted resizes, timestamp rebasing,
  `truncatedAtStart` reporting, thread-safety smoke (writer + exporter).
- **Round-trip (the acceptance criterion):** feed a byte script (incl. a
  resize and an emoji) into a `FlightRecordingBuffer`, export via
  `PtyRecorder`, replay the file through `ReplayRunner` into a fresh
  buffer — `BufferSnapshot` must equal the snapshot of a buffer fed the same
  bytes directly. Runs on all three OSes via the normal unit lanes.
- **Recorder overloads:** explicit-timestamp events serialize with the given
  `t` values, monotonicity preserved.
- **Protocol:** export with both toggles on → file exists, valid v2 header,
  no `input` events present (privacy assertion); export with
  `AgentReplayExportEnabled=false` → `exportDisabled`; unknown pane →
  `sessionNotFound`.
- **CLI:** `--replay` on a fixture prints the golden-matching formatted
  snapshot; bad path/usage exit codes; truncated file exits 1 with partial
  output.
- **Off-is-off:** with Agent Access disabled, sessions have no ring
  (enable/disable verified via the session surface).

## Out of Scope

- Frame-stepping / `--at <ms>` rendering and PNG output (extension of
  ReplayCommand later; Virtual fast-forward already gives most of it)
- Exporting manual recordings via MCP (users can already share those files)
- A3 acting surface (unchanged; separate milestone)

## Suggested PR Slicing

1. **Flight recorder core:** `FlightRecordingBuffer`, `PtyRecorder`
   explicit-timestamp overloads, `ITerminalFlightRecorder` +
   `RustPtySession` integration, ring + round-trip + recorder tests. No
   behavior change (nothing enables it yet).
2. **Protocol + tool:** service lifecycle wiring, `exportReplay` method +
   `exportDisabled` error code, `AgentReplayExportEnabled` setting + toggle,
   MCP `novaterminal.export_replay`, protocol/privacy tests.
3. **CLI + docs:** `ReplayCommand` + tests, README (`--replay` usage, module
   notes), DIRECTION A4 checkboxes.
