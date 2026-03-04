# NovaTerminal vNext — Test Pipeline (Golden + CI Strategy)

This document defines the **deterministic test pipeline** for vNext features:
- replay seek + timeline
- unicode/font diagnostics
- VT torture suite
- perf regression guard

Primary objective: **high signal, low flake** CI across OSes and font profiles.

---

## 0. Test Philosophy

### Separate “state correctness” from “pixel correctness”
- **State correctness**: compare canonical `snapshot.json` (cell grid + metadata).
- **Pixel correctness**: compare `snapshot.png` with tolerance, used sparingly.

### Control variables explicitly
Baselines must be keyed by:
- OS: `win|mac|linux`
- `fontProfileId`
- `dpiProfileId`
- (optional) renderer backend id

---

## 1. Test Asset Types

### A) Recordings
- `.novarec` (event stream)
- `.idx` (optional committed; or rebuilt in CI)

### B) Canonical snapshots
- `snapshot.json` (required)
- `snapshot.png` (optional)

### C) Diff artifacts (CI output only)
- `diff.json` summary
- `diff.png` heatmap

---

## 2. Golden Baseline Layout

Recommended directory structure:

```
tests/VTTests/Baselines/
  win/
    bundled-jetbrainsmono/
      dpi-100/
        case-001/
          input.novarec
          expected.snapshot.json
          expected.snapshot.png (optional)
  mac/
  linux/
```

Rules:
- Prefer **bundled fonts** in test assets to avoid system installs.
- Store a `baseline.manifest.json` with:
  - schema versions
  - tool versions used to generate

---

## 3. Baseline Profiles

### Font profiles
- `bundled-jetbrainsmono` (default CI)
- `system-nerdfont` (optional, not required in CI)
- `fallback-default`

### DPI profiles
- `dpi-100`
- `dpi-150` (optional if you support scaling issues)

---

## 4. CI Matrix

Recommended GitHub Actions matrix:
- OS: windows-latest, macos-latest, ubuntu-latest
- Font profile: bundled-jetbrainsmono
- DPI: dpi-100

Optional nightly matrix:
- add dpi-150
- add additional fonts

---

## 5. Deterministic Replay Tests (Must-have)

### 5.1 Seek determinism
For each recording baseline:
1. Load `.novarec`
2. Build or load `.idx`
3. Seek to each marker timestamp
4. Generate `snapshot.json`
5. Compare to expected

Acceptance:
- exact equality for canonical fields
- allow tolerated differences only for explicitly whitelisted fields

### 5.2 Alt-screen correctness
Recordings must include:
- enter alt screen
- draw
- exit alt screen
- ensure scrollback unaffected

### 5.3 Resize storms
Use seeded resize sequence:
- store seed in metadata
- ensure same sequence in CI

---

## 6. Pixel Comparison Strategy (Use Carefully)

### When to use PNG comparisons
- diagnosing renderer regressions
- unicode glyph fallback issues
- known historical bugs (row line artifacts, snapping bugs)

### How to compare
- Prefer a simple pixel diff with a threshold:
  - allow N changed pixels
  - allow small per-channel delta
- Always emit `diff.png` on failure.

### Don’t use PNG as the only gate
PNG differs too easily across GPUs/drivers.

---

## 7. VT Torture Suite Pipeline

### Inputs
- a list of test cases (YAML/JSON):
  - recording generation commands
  - expected snapshot outputs
  - tags (resize/unicode/scroll)

### Runner outputs
- `suite-summary.json`
- per-case artifact folder with:
  - actual snapshot.json/png
  - diff outputs
  - logs

### Score
Compute compatibility score:
- pass = 1
- fail = 0
- weighted by category (resize/unicode higher weight)

---

## 8. Performance Regression Guard Pipeline

### Baseline capture
`novaterm perf record <workload>`
- outputs `perf.jsonl`
- outputs `perf-summary.json`

### Compare
`novaterm perf compare baseline candidate`
- compute per-metric deltas
- apply tolerances:
  - default ±5%
  - configurable per workload/metric

### CI outputs
- JSON summary for PR comments
- JUnit for CI pass/fail
- attach `perf.jsonl` as artifact

---

## 9. Flake Control Checklist

- No reliance on system fonts
- No reliance on current time for determinism
- Avoid GPU timing as correctness input
- Seed all randomized tests
- Prefer state snapshots as primary gate
- For Windows: normalize line endings and locale-sensitive formatting

---

## 10. “Golden Baseline Generation” Command

Add a dedicated CLI command:
- `novaterm testgen baseline --case <id> --os <os> --fontProfileId <id> --dpiProfileId <id>`

It should:
1. run the case
2. produce `.novarec`
3. produce `expected.snapshot.json`
4. optionally produce png
5. update `baseline.manifest.json`

---

## 11. Recommended Minimum Test Set (vNext)

### Determinism suite
- Case 001: plain output + scroll
- Case 002: alt screen enter/exit
- Case 003: resize storms
- Case 004: unicode combining marks
- Case 005: powerline symbols (bundled font)
- Case 006: full-screen TUI trace (htop or similar)

### Performance suite
- Workload A: heavy scroll output
- Workload B: rapid cursor movement + updates
- Workload C: unicode-heavy output

---

## 12. Failure Reporting

On any failure, CI should upload:
- `actual.snapshot.json`
- `diff.json`
- `diff.png` (if png enabled)
- replay excerpt around failure (optional)

This makes debugging fast and avoids “works on my machine” misery 😄
