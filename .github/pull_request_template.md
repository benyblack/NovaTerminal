<!--
Thanks for contributing to NovaTerminal.

Answer the questions below. "Not applicable" is a fine answer for a docs or
tooling change — just say so rather than leaving a section blank, so the
reviewer knows you considered it.

If this is your first PR here: welcome. A first PR that needs a couple of
rounds of review is a normal first PR.
-->

## What this changes

<!-- One or two sentences. What was wrong or missing, and what does this do? -->

Fixes #

## Invariant and ownership

- **What invariant does this change affect?**
  <!-- e.g. "lossless reflow", "buffer lock contract", "renderer never
       interprets VT semantics", or "none — docs only" -->

- **Which module owns that invariant?**
  <!-- See docs/MODULE_OWNERSHIP.md. e.g. NovaTerminal.VT -->

## Tests

- **What tests cover this change?**
  <!-- Name the test files/methods. If a change is not covered by tests it
       cannot be merged — see CONTRIBUTING.md. -->

- **Which categories did you run locally?**
  <!-- A bare `scripts/build.sh test` applies no filter and runs everything. But
       CI's gating job EXCLUDES Replay, RenderMetrics, PtySmoke, Stress and
       GoldenSharedPng (each gets a dedicated job), and
       tests/NovaTerminal.App.Tests is NON-BLOCKING there (#81) — so a failure in
       it will not turn the check red. If your change touches either area, run it
       locally and say so here. -->

  - [ ] full unfiltered run (`scripts/build.sh test`)
  - [ ] `Category=Replay`
  - [ ] `Category=RenderMetrics`
  - [ ] `Category=PtySmoke`
  - [ ] `tests/NovaTerminal.App.Tests` (non-blocking in CI — check it yourself)
  - [ ] full local CI rehearsal (`ci/run.sh` / `ci/run.ps1`)

## Impact

- **Does this affect cross-platform behavior?**
  <!-- Windows / Linux / macOS. Which did you test on? -->

- **Does this change renderer metrics?**
  <!-- Frame time, cache hit rates, invalidation counts. Performance PRs
       without metrics will be sent back. -->

- **Does this change VT coverage?**
  <!-- If you added or changed a catalog-owned sequence: update
       src/NovaTerminal.VtContract/vt-capabilities.json and add or update its
       executable case in tests/NovaTerminal.VT.Tests/VtCapabilityContractTests.cs.
       Also update docs/vt_coverage_matrix.md AND regenerate
       src/NovaTerminal.App/Resources/vt-conformance-report.json, or the VT
       Conformance check goes red. See CONTRIBUTING.md. -->
