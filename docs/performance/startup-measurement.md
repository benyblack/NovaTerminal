# Startup Measurement

This repo includes a repeatable startup harness for before/after comparisons.

## What It Measures

- `WindowOpened`
- `FirstTerminalReady`
- `SessionRestoreComplete`
- `DeferredWorkComplete`
- `BackgroundRestoreComplete`

## Protocol

1. Build the app in `Release`.
2. Run the baseline harness before behavioral startup changes.
3. Run the candidate harness after the startup changes.
4. Generate a comparison report from the two artifact folders.

The harness launches NovaTerminal with:

- `NOVATERM_STARTUP_METRICS=1`
- `NOVATERM_STARTUP_METRICS_OUT=<artifact jsonl path>`
- `NOVATERM_APPDATA_ROOT=<isolated measurement appdata path>`

The isolated app-data root prevents the measurement run from mutating the normal user profile. The harness also seeds a fixed restored session so restore timing is measured against the same startup workload for baseline and candidate runs.

## Commands

```powershell
dotnet build NovaTerminal.sln -c Release
pwsh -File tests/tools/measure_startup.ps1 -Configuration Release -Label baseline -Iterations 10
pwsh -File tests/tools/summarize_startup_metrics.ps1 -InputPath artifacts-codex/startup/baseline
pwsh -File tests/tools/measure_startup.ps1 -Configuration Release -Label candidate -Iterations 10
pwsh -File tests/tools/summarize_startup_metrics.ps1 -Baseline artifacts-codex/startup/baseline -Candidate artifacts-codex/startup/candidate -Out artifacts-codex/startup-performance-report.md
```

## Artifacts

- Per-launch metrics: `artifacts-codex/startup/<label>/startup_metrics.jsonl`
- Comparison report: `artifacts-codex/startup-performance-report.md`
