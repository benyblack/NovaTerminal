# VT Capability Contract Design

## Problem

NovaTerminal describes VT support in several independent places: the hot-path
`AnsiParser` switch, the Markdown coverage matrix, the generated application
report, and the MCP escape-sequence explainer. Those descriptions can drift.
After CNL (`CSI E`) and CPL (`CSI F`) were implemented, the MCP explainer and
its tests still described them as unsupported, while the grouped coverage row
remained partial. Existing CI validated each artifact internally but did not
prove that their support claims agreed with real parser behavior.

## Design

Add a small machine-readable capability manifest owned by the conformance
tooling. Each entry identifies one CSI sequence by mnemonic and final byte,
records its support state, names a deterministic evidence case, and provides
the explanation used by developer tooling. Keep `AnsiParser` unchanged: its
switch remains the performance-sensitive implementation, while tests verify
manifest claims by sending representative byte streams through the public
parser API.

The conformance tool will load and validate the manifest. Validation will fail
when entries are duplicated, supported entries lack evidence, or evidence does
not cover the declared sequence. The MCP explainer will read its CSI descriptions
from the same embedded manifest data instead of maintaining separate support
sentences. The Markdown coverage matrix remains the human-readable summary, but
CNL, CPL, and CHA will have separate rows so partial support cannot hide which
sequence lacks evidence. The generated application report will be regenerated
from that corrected matrix.

This is deliberately additive. It does not generate parser dispatch code,
reflect into parser internals, or add work to terminal input processing.

## Data Flow

1. The capability manifest declares the supported CSI feature and its evidence
   identifier.
2. Conformance tests load the manifest and run every supported cursor movement
   case through `AnsiParser`.
3. The conformance CLI validates the manifest alongside the Markdown matrix.
4. The MCP explainer consumes the same descriptions, preventing a second support
   table from drifting.
5. CI runs the conformance tests and report validation whenever the parser,
   manifest, matrix, or tooling changes.

## Validation and Failure Handling

Manifest parsing is strict and deterministic. Invalid JSON, duplicate sequence
keys, unknown support states, missing descriptions, or supported entries without
evidence are validation errors. Tooling reports the offending entry and exits
non-zero. Runtime terminal behavior is unaffected because the parser does not
load the manifest.

## Testing

- First add failing MCP tests proving CNL and CPL must be reported as supported.
- Add failing manifest-validation tests for duplicates and missing evidence.
- Add a contract test that executes each supported cursor capability through
  `AnsiParser`, including omitted/zero parameters, bounds, private/intermediate
  rejection, and split-input equivalence where applicable.
- Update the matrix and regenerate the embedded report.
- Run focused VT, conformance, and MCP tests, then the repository's full wrapped
  test and build verification.

## Deferred Work

Byte-accurate fixtures for additional producers, live nightly PTY probes, and
semantic fuzz invariants remain valuable follow-ups. They are intentionally not
part of this PR so the first enforcement layer stays reviewable and low risk.
