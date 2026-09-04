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
  * A truncated run is reported loudly, with the count, because the tests it never reached
    cannot be said to have passed - and, when nothing hung, it *fails*. Reporting alone was
    not enough: a run that executed 1,112 of 3,434 tests and lost the rest to xUnit
    collection aborts reported "no failures" and went green, minutes after the same branch
    had run all 3,434. Truncation is only tolerated for the one cause this lane exists to
    tolerate, the #81 teardown hang, which leaves a hang dump behind to prove itself.
  * A catastrophic (runner-level) failure in the step log fails this step. Those aborts kill
    whole collections without ever appearing in the trx, so the results file is silent about
    them: the summary reads "no failures" precisely because the tests were never run.

Names are matched as substrings, so an allowlist entry without theory arguments covers
every case of that theory.

Usage: check-app-tests-baseline.py <trx-path> <allowlist-path> <min-executed> [log-path]

  min-executed  Floor for the executed count in this lane. Lowering it is a decision, the
                same way growing the flake allowlist is: it means the lane legitimately has
                fewer tests, not that a truncated run should be waved through.
  log-path      Optional. The captured `dotnet test` output for this lane, scanned for
                runner-level aborts that never reach the trx.
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


def catastrophic_failures(log: Path) -> list[str]:
    """
    Runner-level aborts from the step log.

    xUnit reports these as "Catastrophic failure: ..." and they take the whole collection with
    them, so the affected tests never produce a UnitTestResult. Nothing about them is visible
    in the trx - which is why a run can lose two thirds of the suite and still be summarised as
    passing. Read from the log because that is the only place they exist.
    """
    if not log.is_file():
        return []
    found = []
    for raw in log.read_text(encoding="utf-8", errors="replace").splitlines():
        marker = "Catastrophic failure"
        index = raw.find(marker)
        if index >= 0:
            found.append(raw[index:].strip())
    # Deduplicated for display, but the caller is told how many times they fired: ten copies of
    # one line is one finding, and also ten dead collections. Both numbers matter.
    return found


def hang_dumps(trx: Path) -> list[Path]:
    """Hang dumps written by --blame-hang-dump-type, i.e. the #81 signature."""
    results_dir = trx.parent
    if not results_dir.is_dir():
        return []
    return sorted(results_dir.rglob("*hangdump*.dmp"))


def main() -> int:
    if len(sys.argv) not in (4, 5):
        print(
            f"usage: {Path(sys.argv[0]).name} <trx-path> <allowlist-path> <min-executed> "
            f"[log-path]",
            file=sys.stderr,
        )
        return 2

    trx = Path(sys.argv[1])
    allowlist_path = Path(sys.argv[2])
    try:
        min_executed = int(sys.argv[3])
    except ValueError:
        print(f"min-executed must be an integer, got {sys.argv[3]!r}", file=sys.stderr)
        return 2
    log_path = Path(sys.argv[4]) if len(sys.argv) == 5 else None
    dumps = hang_dumps(trx)
    aborts = catastrophic_failures(log_path) if log_path else []

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

    # Both checks below are skipped when a hang dump is present, and that is deliberate rather
    # than lenient: the #81 hang truncates runs on roughly one job-run in three, and this lane
    # is non-blocking precisely so that cannot red unrelated PRs. What they close is the other
    # case - a run cut short with nothing hung, which had no signal at all.
    truncated = executed < min_executed
    if aborts and not dumps:
        summary(
            f"::error::App.Tests hit {len(aborts)} runner-level abort(s) "
            f"({len(set(aborts))} distinct) and no hang dump was "
            f"written, so this is not the #81 hang. Each one kills a whole xUnit collection "
            f"without writing a single result, which is why {trx.name} can report no failures "
            f"while the suite lost most of its tests."
        )
        for line in sorted(set(aborts)):
            summary(f"  - {line}")

    if truncated and not dumps:
        summary(
            f"::error::App.Tests executed {executed} test(s), below this lane's floor of "
            f"{min_executed}, and nothing hung. The missing tests did not pass - they never "
            f"ran. If the lane legitimately has fewer tests now, lower the floor in ci.yml "
            f"deliberately; do not let a truncated run report success."
        )

    if (aborts or truncated) and not dumps:
        # Reported before returning so the failure list is still visible: knowing which tests
        # failed in the part that did run is useful even when the run is being rejected.
        if failures:
            summary(f"Failures recorded before the run was cut short ({len(failures)}):")
            for name in failures:
                summary(f"  - {name}")
        return 1

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
