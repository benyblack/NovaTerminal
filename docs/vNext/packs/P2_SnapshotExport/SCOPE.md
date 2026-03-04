# Pack P2 — Snapshot Export (ANSI/JSON/PNG)

## Objective
Implement deterministic **snapshot export** for bug reports, CI artifacts, and sharing.

## Deliverables
- Export current terminal screen to:
  - `snapshot.ansi`
  - `snapshot.json` (canonical cell grid + metadata)
  - `snapshot.png` (rendered screenshot)
- “Bug report bundle” (zip) containing:
  - snapshots + app/version metadata + (optional) last N seconds replay slice

## In Scope
- Snapshot data contract + versioning (`schemaVersion`)
- CLI or UI command to trigger export (choose smallest integration point)
- Deterministic JSON output (stable ordering, normalized fields)

## Out of Scope
- Replay timeline UI
- VT torture suite runner
- Remote viewer upload
- Command boundaries / folding

## Hard Constraints
- Export must not mutate live terminal state
- Export should finish < 500ms for typical sizes (80x24 .. 200x60) on dev machines
- JSON must be versioned and documented

## Acceptance Criteria (DoD)
- Exports produce expected files
- JSON schema includes at minimum:
  - schemaVersion
  - cols/rows
  - cursor position
  - visible grid cells (codepoints/graphemes + style attrs)
  - palette/theme id if applicable
- PNG matches on-screen render within tolerance
- Tests verify determinism (two exports without state changes must be byte-identical JSON)

## Notes
If PNG export depends on GPU state that is nondeterministic, treat PNG as best-effort and make JSON the correctness source of truth.
