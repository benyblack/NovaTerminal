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
  <!-- The default `scripts/build.sh test` run EXCLUDES Replay, RenderMetrics,
       PtySmoke, Stress and GoldenSharedPng — those run in their own CI jobs.
       And tests/NovaTerminal.App.Tests is NON-BLOCKING in CI (#81), so a
       failure there will not turn the check red. If your change touches
       either area, run it locally and say so here. -->

  - [ ] default unit lane (`scripts/build.sh test`)
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
  <!-- If you added or changed a sequence: update docs/vt_coverage_matrix.md
       AND regenerate src/NovaTerminal.App/Resources/vt-conformance-report.json,
       or the VT Conformance check goes red. See CONTRIBUTING.md. -->
