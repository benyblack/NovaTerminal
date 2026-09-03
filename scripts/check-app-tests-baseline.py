#!/usr/bin/env python3
"""Gate the non-blocking headless App.Tests lane against an explicit flake allowlist.

The lane runs with continue-on-error because Avalonia.Headless.XUnit 12.0.4 can deadlock on
testhost teardown (#81 / AvaloniaUI/Avalonia#21467) and a hang must not block PRs. The side
effect was that nobody looked: the job reported success while 16 tests failed on Windows and
14 on ubuntu, for weeks, and a real regression would have merged in silence.

So the hang stays non-blocking and the *results* become blocking:

  * A failing test that is not named in the allowlist fails this step.
  * A run that produced no results at all fails this step too - unless a hang dump is
    present, which is the #81 signature and the one case continue-on-error exists for. A
    crash, a discovery failure, or a zero-result trx is otherwise indistinguishable from
    success, and that is exactly the "green but broken" shape this gate exists to stop.
  * A truncated run (the hang aborts the run partway) is reported loudly, with the count,
    because the tests it never reached cannot be said to have passed.

Names are matched as substrings, so an allowlist entry without theory arguments covers
every case of that theory.

Usage: check-app-tests-baseline.py <trx-path> <allowlist-path>
"""

import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def summary(line: str) -> None:
    """Echo to the job summary as well as the log, so partial runs are visible at a glance."""
    print(line)
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        try:
            with open(path, "a", encoding="utf-8") as handle:
                handle.write(line + "\n")
        except OSError:
            pass


def read_allowlist(path: Path) -> list[str]:
    if not path.is_file():
        return []
    entries = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if line:
            entries.append(line)
    return entries


def read_results(path: Path) -> tuple[list[str], int]:
    """Returns (failed test names, total executed results)."""
    root = ET.parse(path).getroot()
    failures = []
    executed = 0
    for result in root.iter():
        # Tag comparison is namespace-agnostic on purpose: the trx namespace has moved
        # between vstest versions and a silent zero-failure read is the worst outcome here.
        if not result.tag.endswith("UnitTestResult"):
            continue
        executed += 1
        if result.get("outcome") != "Failed":
            continue
        name = result.get("testName")
        if name:
            failures.append(name)
    return sorted(set(failures)), executed


def hang_dumps(trx: Path) -> list[Path]:
    """Hang dumps written by --blame-hang-dump-type, i.e. the #81 signature."""
    results_dir = trx.parent
    if not results_dir.is_dir():
        return []
    return sorted(results_dir.rglob("*hangdump*.dmp"))


def main() -> int:
    if len(sys.argv) != 3:
        print(f"usage: {Path(sys.argv[0]).name} <trx-path> <allowlist-path>", file=sys.stderr)
        return 2

    trx = Path(sys.argv[1])
    allowlist_path = Path(sys.argv[2])
    dumps = hang_dumps(trx)

    if not trx.is_file():
        if dumps:
            summary(
                f"::warning::App.Tests produced no trx, but wrote a hang dump "
                f"({dumps[0].name}). That is the #81 teardown hang, which this lane tolerates "
                f"by design. Nothing to gate on."
            )
            return 0
        summary(
            f"::error::App.Tests produced no results at {trx} and did not hang - no hang dump "
            f"was written. That is a crash or a discovery failure, not the #81 hang, and it "
            f"leaves the whole lane unjudged. Read the step log above."
        )
        return 1

    allowlist = read_allowlist(allowlist_path)
    failures, executed = read_results(trx)

    if executed == 0:
        summary(
            f"::error::App.Tests wrote {trx.name} but it records no executed tests. The lane "
            f"ran nothing, so its result means nothing. Read the step log above."
        )
        return 1

    if dumps:
        summary(
            f"::warning::App.Tests recorded {executed} executed test(s) and then hung "
            f"({dumps[0].name}, the #81 teardown hang), so the run is truncated: tests it never "
            f"reached are neither passed nor failed. The failures below are judged as usual."
        )

    if not failures:
        summary(f"App.Tests: {executed} executed, no failures in {trx.name}.")
        return 0

    unexpected = [f for f in failures if not any(entry in f for entry in allowlist)]
    allowed = [f for f in failures if f not in unexpected]

    summary(f"App.Tests: {executed} executed, {len(failures)} failing in {trx.name}.")
    if allowed:
        print(f"\nAllowed by {allowlist_path} ({len(allowed)}):")
        for name in allowed:
            print(f"  - {name}")

    if not unexpected:
        print("\nAll failures are allowlisted.")
        return 0

    summary(f"NOT allowlisted ({len(unexpected)}):")
    for name in unexpected:
        summary(f"  - {name}")
        print(f"::error::App.Tests regression not in the flake allowlist: {name}")

    print(
        f"\nEither fix these, or - if one is genuinely flaky rather than broken - add it to "
        f"{allowlist_path} with a note saying why and what would make it deterministic. "
        f"Do not widen the list to make a red build green."
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
