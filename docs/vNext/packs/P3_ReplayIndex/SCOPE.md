# Pack P3 — ReplayIndex + Seek API

## Objective
Implement a **ReplayIndex** that enables fast, deterministic seeking into `.novarec` recordings.

## Deliverables
- Index builder that maps:
  - `t_us -> file offset`
  - marker timestamps (resize, alt-screen, stderr bursts)
- Seek API:
  - `Seek(TimeSpan t)` (or equivalent)
  - `NextMarker(kind)` / `PrevMarker(kind)` (optional but recommended)
- Index caching:
  - write sidecar `.idx` OR cache in memory (choose minimal implementation)
- Minimal UI or CLI hook:
  - optional: `novaterm replay seek <t>` for testing

## In Scope
- Index data structure and builder
- Deterministic seeking behavior
- Tests for seek correctness

## Out of Scope
- Timeline UI (that’s later; this pack focuses on core capability)
- Command boundary indexing
- Any recording schema changes (unless you add optional marker events without breaking)
- Remote replay viewer

## Hard Constraints
- Seeking must reproduce the same terminal state as sequential playback
- Index must be forward-compatible and tolerant of unknown event types (skip by len)
- Large recordings should load with only one scan (then seek is fast)

## Acceptance Criteria (DoD)
- Build index on load OR load existing `.idx`
- Seek to:
  - start
  - mid
  - marker timestamp
  - end
  returns deterministic snapshots
- Tests cover:
  - seek across resize
  - seek across alt-screen
  - seek across heavy scroll

## Notes
If you do not yet have explicit marker events, the index can still record offsets for:
- resize events
- mode change events (alt-screen)
- user bookmarks (if present)
