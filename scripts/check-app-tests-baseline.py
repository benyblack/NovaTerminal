#!/usr/bin/env python3
"""Gate the non-blocking headless App.Tests lane against an explicit flake allowlist.

The lane itself runs with continue-on-error, because Avalonia.Headless.XUnit 12.0.4 can
deadlock on testhost teardown (#81 / AvaloniaUI/Avalonia#21467) and a hang must not block
PRs. The side effect was that nobody looked: the job reported success while 16 tests
failed on Windows and 14 on ubuntu, for weeks, and a real regression would have merged in
silence.

So the hang stays non-blocking, and the *results* become blocking. Any failing test that
is not named in the allowlist fails this step. A missing trx is treated as the hang and
only warns - there are no results to judge.

Names are matched as substrings, so an allowlist entry without theory arguments covers
every case of that theory.

Usage: check-app-tests-baseline.py <trx-path> <allowlist-path>
"""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def read_allowlist(path: Path) -> list[str]:
    if not path.is_file():
        return []
    entries = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if line:
            entries.append(line)
    return entries


def read_failures(path: Path) -> list[str]:
    root = ET.parse(path).getroot()
    failures = []
    for result in root.iter():
        # Tag comparison is namespace-agnostic on purpose: the trx namespace has moved
        # between vstest versions and a silent zero-failure read is the worst outcome here.
        if not result.tag.endswith("UnitTestResult"):
            continue
        if result.get("outcome") != "Failed":
            continue
        name = result.get("testName")
        if name:
            failures.append(name)
    return sorted(set(failures))


def main() -> int:
    if len(sys.argv) != 3:
        print(f"usage: {Path(sys.argv[0]).name} <trx-path> <allowlist-path>", file=sys.stderr)
        return 2

    trx = Path(sys.argv[1])
    allowlist_path = Path(sys.argv[2])

    if not trx.is_file():
        print(f"::warning::No trx at {trx} - the App.Tests lane produced no results "
              f"(the #81 teardown hang looks like this). Nothing to gate on.")
        return 0

    allowlist = read_allowlist(allowlist_path)
    failures = read_failures(trx)

    if not failures:
        print(f"App.Tests: no failures in {trx.name}.")
        return 0

    unexpected = [f for f in failures if not any(entry in f for entry in allowlist)]
    allowed = [f for f in failures if f not in unexpected]

    print(f"App.Tests: {len(failures)} failing test(s) in {trx.name}.")
    if allowed:
        print(f"\nAllowed by {allowlist_path} ({len(allowed)}):")
        for name in allowed:
            print(f"  - {name}")

    if not unexpected:
        print("\nAll failures are allowlisted.")
        return 0

    print(f"\nNOT allowlisted ({len(unexpected)}):")
    for name in unexpected:
        print(f"  - {name}")
        print(f"::error::App.Tests regression not in the flake allowlist: {name}")

    print(
        f"\nEither fix these, or - if one is genuinely flaky rather than broken - add it to "
        f"{allowlist_path} with a note saying why and what would make it deterministic. "
        f"Do not widen the list to make a red build green."
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
