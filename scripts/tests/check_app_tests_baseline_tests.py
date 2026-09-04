"""
Behaviour matrix for scripts/check-app-tests-baseline.py.

The gate decides whether the headless App.Tests lane is allowed to report success, and it now
has real branching: unlisted failures, an empty or missing trx, a truncated run, runner-level
aborts, and the one truncation it must keep tolerating (the #81 teardown hang, evidenced by a
hang dump). Getting any of those backwards either reds every PR or hides another
green-but-broken run - the exact failure this gate was extended to stop, where a job executed
1,112 of 3,434 tests and reported "no failures".

Plain python with no test framework, matching the rest of scripts/. Run it directly:

    python scripts/tests/check_app_tests_baseline_tests.py

Exits non-zero if any case behaves differently from the table at the bottom.
"""
import io, os, shutil, subprocess, sys, tempfile
from pathlib import Path

GATE = Path("scripts/check-app-tests-baseline.py").resolve()
ALLOW = Path("tests/app-tests-known-flaky.txt").resolve()


def make_trx(path: Path, passed: int, failed_names=()):
    results = "".join(
        f'<UnitTestResult testName="Passing{i}" outcome="Passed" />' for i in range(passed)
    ) + "".join(
        f'<UnitTestResult testName="{n}" outcome="Failed" />' for n in failed_names
    )
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        '<?xml version="1.0" encoding="UTF-8"?>'
        f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}</Results></TestRun>',
        encoding="utf-8",
    )


def run(case, trx, floor, log=None, dump=False):
    root = Path(tempfile.mkdtemp())
    trx_path = root / "lane" / "unit_app.trx"
    make_trx(trx_path, trx["passed"], trx.get("failed", ()))
    if dump:
        (trx_path.parent / "testhost_1_hangdump.dmp").write_bytes(b"\x00")
    args = [sys.executable, str(GATE), str(trx_path), str(ALLOW), str(floor)]
    if log is not None:
        log_path = trx_path.parent / "console.log"
        log_path.write_text(log, encoding="utf-8")
        args.append(str(log_path))
    p = subprocess.run(args, capture_output=True, text=True)
    shutil.rmtree(root, ignore_errors=True)
    return p.returncode, (p.stdout + p.stderr)


ABORT_LOG = (
    "[xUnit.net 00:00:07.46]     [FATAL ERROR] System.InvalidOperationException\n"
    "[xUnit.net 00:00:07.46] Catastrophic failure: System.InvalidOperationException : "
    "The calling thread cannot access this object because a different thread owns it.\n"
    "[xUnit.net 00:00:53.56] Catastrophic failure: System.InvalidOperationException : "
    "The calling thread cannot access this object because a different thread owns it.\n"
)

cases = [
    # name,                                   trx,                       floor, log,       dump,  expect
    ("healthy full run",                      {"passed": 3434},          3300,  "",        False, 0),
    ("truncated, nothing hung",               {"passed": 1112},          3300,  "",        False, 1),
    ("truncated but hung (#81, tolerated)",   {"passed": 1112},          3300,  "",        True,  0),
    # Aborts warn rather than block: they fire on complete, clean runs too (21208fd ran all
    # 3,434 with 10 of them), so blocking would red every PR and train everyone to ignore
    # this lane. The floor is what guards coverage until the underlying cause is fixed.
    ("aborts in log, full run, warns only",   {"passed": 3434},          3300,  ABORT_LOG, False, 0),
    ("aborts in log but hung (tolerated)",    {"passed": 3434},          3300,  ABORT_LOG, True,  0),
    ("truncated and aborted, floor blocks",   {"passed": 1112},          3300,  ABORT_LOG, False, 1),
    ("unlisted failure, full run",            {"passed": 3400, "failed": ["Totally.New.Test"]}, 3300, "", False, 1),
    ("no log argument, healthy",              {"passed": 3434},          3300,  None,      False, 0),
    ("no log argument, truncated",            {"passed": 10},            3300,  None,      False, 1),
    ("platformboot lane, healthy",            {"passed": 46},            40,    "",        False, 0),
]

failures = 0
for name, trx, floor, log, dump, expect in cases:
    code, out = run(name, trx, floor, log, dump)
    ok = code == expect
    failures += 0 if ok else 1
    print(f"{'PASS' if ok else 'FAIL'}  {name:<38} exit={code} (want {expect})")
    if not ok:
        print("      " + out.strip().replace("\n", "\n      ")[:600])

print()
print("all gate cases behaved" if failures == 0 else f"{failures} case(s) misbehaved")
sys.exit(1 if failures else 0)
