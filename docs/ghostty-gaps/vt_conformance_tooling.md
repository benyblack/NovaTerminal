# VT Conformance Tooling

This PR adds a focused validator/report generator for `docs/vt_coverage_matrix.md`.

## Command

Generate a deterministic JSON report and fail on validation errors:

```powershell
dotnet run --project src/NovaTerminal.Conformance/NovaTerminal.Conformance.csproj -- --validate --report artifacts/vt-conformance/vt-conformance-report.json
```

Defaults:

- repo root: current working directory
- matrix path: `docs/vt_coverage_matrix.md`
- output: stdout unless `--report` is provided

## Report Format

The tool emits a stable JSON document with:

- `schemaVersion`
- `matrixPath`
- `matrixSha256`
- `summary`
- `sections`
- `rows`
- `errors`
- `warnings`

Each row includes:

- section title
- feature name
- status text
- raw evidence text
- normalized evidence kinds
- extracted repo-linked evidence paths
- ownership text
- known deviations text
- source line number

No timestamps are included, so identical matrix content produces identical report bytes.

## Validation Rules

Hard failures:

- feature tables must have consistent markdown columns
- feature rows must use a known status symbol
- `✅ Supported` rows must declare automated evidence
- linked evidence paths must exist in the repo
- `🚫 Won’t support` rows must include a rationale

Warnings:

- `✅ Supported` rows that mention evidence generically but do not yet link a concrete repo path

Warnings are included in the report but do not fail CI.

## CI

`.github/workflows/vt-conformance.yml` runs the validator on:

- `docs/vt_coverage_matrix.md`
- conformance tooling source changes
- workflow/doc updates for this tooling

It uploads the JSON report as an artifact for PR review and push runs.

## Intentional Limitations

- The parser only reads markdown tables that include both `Status` and `Evidence` columns.
- The tool does not infer support from source code; the matrix remains the canonical input.
- Ownership paths are reported verbatim and are not path-validated.
- Generic evidence text such as `Replay` or `Unit/Replay` is accepted for `✅ Supported` rows, but reported as a warning until concrete links are added.
