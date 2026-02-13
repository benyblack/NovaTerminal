# NovaTerminal VTTEST Capture Adapter

This project provides an automated harness to drive `vttest` and capture
its raw PTY output into NovaTerminal `.rec` (JSONL) files.

These recordings are used for deterministic regression testing and
golden snapshot generation.

------------------------------------------------------------------------

## Determinism Guarantees

The capture adapter enforces a controlled execution environment:

-   `TERM` is forced to `xterm-256color` inside the native PTY layer.
-   `LC_ALL` and `LANG` are set to `C` to ensure stable locale output.
-   Terminal size defaults to 80x24 unless overridden.
-   Output is captured byte-exact from the PTY.
-   Timestamps are monotonic (Stopwatch-based).

This ensures reproducible `.rec` files suitable for snapshot-based
testing.

------------------------------------------------------------------------

## Prerequisites (Ubuntu/Linux)

To run the capture adapter, install `vttest`:

``` bash
sudo apt-get update
sudo apt-get install vttest
```

If building from source:

``` bash
wget https://invisible-island.net/datafiles/release/vttest.tar.gz
tar xvf vttest.tar.gz
cd vttest-*
./configure
make
sudo make install
```

No manual `TERM` or locale exports are required.

------------------------------------------------------------------------

## Setup

1.  Build NovaTerminal (ensures native `rusty_pty` is available):

``` bash
dotnet build NovaTerminal/NovaTerminal.csproj
```

2.  Build the external suite:

``` bash
dotnet build tests/NovaTerminal.ExternalSuites/NovaTerminal.ExternalSuites.csproj
```

------------------------------------------------------------------------

## Usage

Generate a `.rec` file:

``` bash
dotnet run --project tests/NovaTerminal.ExternalSuites/NovaTerminal.ExternalSuites.csproj \
  --suite vttest \
  --scenario cursor \
  --out tests/Replays/Vttest/vttest_cursor.rec
```

Optional parameters:

    --cols <number>
    --rows <number>
    --timeout <milliseconds>

Default terminal size: `80x24`

------------------------------------------------------------------------

## Available Scenarios

-   `cursor` -- Cursor movement and navigation tests
-   `sgr` -- SGR (color/style) tests
-   `scroll` -- Scroll region tests

These scenarios use deterministic keystroke macros without screen
parsing.

------------------------------------------------------------------------

## Snapshot Generation

After generating a `.rec`:

1.  Move the file to:

        NovaTerminal.Tests/Fixtures/Replay/

2.  Run replay tests.

3.  Replay assertions compare against checked-in golden `.snap` files
    in `NovaTerminal.Tests/Fixtures/Replay/`.

4.  For CI parity runs, set `PARITY_ARTIFACT_DIR` so each replay test
    also emits a runtime-generated `.snap` artifact.

Golden `.snap` files must be reviewed before committing when they are
intentionally updated.

------------------------------------------------------------------------

## CI Integration (Recommended)

In Linux CI (Ubuntu runner):

``` yaml
env:
  LC_ALL: C
  LANG: C
```

Even though the PTY layer sets these internally, setting them at the
workflow level improves traceability.

For replay parity jobs, also set:

``` yaml
env:
  PARITY_ARTIFACT_DIR: ${{ github.workspace }}/artifacts/parity
```

Recommended flow:

1.  Run replay tests with `PARITY_ARTIFACT_DIR` set.
2.  Upload `artifacts/parity/**/*.snap` from each OS job.
3.  Compare downloaded artifact sets using
    `tests/tools/compare_snapshots.py`.
4.  Fail on missing artifacts or snapshot diffs.

Nightly stress lanes should target real categories (`Stress`,
`Performance`, `Latency`) instead of `ReplayStress`.

------------------------------------------------------------------------

## Limitations

-   Optimized for Linux PTY behavior.
-   Windows behavior depends on `win32::spawn_with_passthrough`.
-   Argument parsing in native layer is whitespace-based (no shell
    quoting support).
-   Does not currently parse VTTEST screens; uses activity-based quiet
    detection.

------------------------------------------------------------------------

## Notes

This harness does not modify NovaTerminal core behavior.\
It exists purely as an external integration regression suite.
