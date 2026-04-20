# NovaTerminal External Capture Adapters

This project provides automated harnesses that emit NovaTerminal `.rec`
(JSONL) files for deterministic regression and replay testing.

These recordings are used for deterministic regression testing and
golden snapshot generation.

------------------------------------------------------------------------

## Determinism Guarantees

The adapters enforce deterministic recording behavior:

-   `TERM` is forced to `xterm-256color` inside the native PTY layer.
-   `LC_ALL` and `LANG` are set to `C` to ensure stable locale output.
-   Terminal size defaults to 80x24 unless overridden.
-   Output is captured byte-exact from the PTY.
-   Timestamps are monotonic (Stopwatch-based).

The `native-ssh` adapter is transcript-driven rather than PTY-driven, so
it remains deterministic without a live SSH endpoint.

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

Generate a deterministic native SSH transcript:

``` bash
dotnet run --project tests/NovaTerminal.ExternalSuites/NovaTerminal.ExternalSuites.csproj \
  --suite native-ssh \
  --scenario fullscreen-exit \
  --out tests/Replays/NativeSsh/native_ssh_fullscreen_exit.rec
```

Optional parameters:

    --cols <number>
    --rows <number>
    --timeout <milliseconds>

Default terminal size: `80x24`

------------------------------------------------------------------------

## Available Scenarios

### `vttest`

-   `cursor` -- Cursor movement and navigation tests
-   `sgr` -- SGR (color/style) tests
-   `scroll` -- Scroll region tests

These scenarios use deterministic keystroke macros without screen
parsing.

### `native-ssh`

-   `fullscreen-exit` -- Fullscreen / alternate-screen enter and exit returning to a prompt
-   `prompt-return` -- Command completion returning to a stable prompt without extra blank lines
-   `resize-burst` -- Resize events during fullscreen use followed by prompt recovery

These scenarios use scripted transcript output and optional resize
events. They do not require a live SSH server.

------------------------------------------------------------------------

## Live Native SSH Tests

The transcript scenarios above are deterministic VT fixtures. The live
native SSH end-to-end tests are separate xUnit tests that run against a
Dockerized OpenSSH server.

Prerequisites:

-   Docker Desktop or another local Docker engine must be running
-   Set `NOVATERM_ENABLE_DOCKER_E2E=1` to enable the live lane
-   Set `NOVATERM_REBUILD_DOCKER_E2E=1` if you want to force a Docker image rebuild

Run the live Docker native SSH VT tests:

``` powershell
$env:NOVATERM_ENABLE_DOCKER_E2E='1'
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshDockerE2eTests" /nodeReuse:false
```

Force a rebuild of the Docker SSH fixture image before running:

``` powershell
$env:NOVATERM_ENABLE_DOCKER_E2E='1'
$env:NOVATERM_REBUILD_DOCKER_E2E='1'
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshDockerE2eTests" /nodeReuse:false
```

Run the broader native SSH core slice, including the live Docker tests
when enabled:

``` powershell
$env:NOVATERM_ENABLE_DOCKER_E2E='1'
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh" /nodeReuse:false
```

Current live coverage:

-   connect, host-key acceptance, password auth
-   command execution and prompt return
-   alternate-screen exit and prompt recovery
-   resize burst during alternate-screen use with final PTY size verification
-   `vim.tiny` downward scrolling with `scrolloff=5` over native SSH

Deterministic VT parity coverage remains in:

-   `tests/NovaTerminal.Core.Tests/Ssh/NativeSshTerminalParityTests.cs`
-   `tests/NovaTerminal.Tests/ReplayTests/NativeSshReplayParityTests.cs`

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

-   `vttest` is optimized for Linux PTY behavior.
-   Windows behavior depends on `win32::spawn_with_passthrough`.
-   Argument parsing in native layer is whitespace-based (no shell
    quoting support).
-   `vttest` does not currently parse screens; it uses activity-based quiet
    detection.
-   `native-ssh` is a deterministic verification harness, not a live SSH
    interoperability test.

------------------------------------------------------------------------

## Notes

This harness does not modify NovaTerminal core behavior.\
It exists purely as an external integration regression suite.
