"""
Behaviour matrix for the SDK-resolution guard in scripts/build.sh and scripts/build.ps1.

The guard exists because a wrapper that reports success for a build which produced nothing is
worse than no wrapper: CLAUDE.md tells every contributor and agent to use these scripts, so
this is the exit code the project's tooling is built on. When global.json pins an SDK that is
not installed, the host prints "A compatible .NET SDK was not found" and has been seen exiting
0 (#347), and that zero used to travel all the way out.

Two things here are easy to get wrong later, so they are cases rather than comments:

  * The guard must not key on the exit code. The same input exits 0 on the Linux host that
    reported #347 and 155 on the Windows machine that fixed it, so both are in the table.
  * It must not key on the English error text, because the host localizes it. The "localized"
    case carries no English at all; a guard grepping for "A compatible .NET SDK was not found"
    passes every other case here and fails only that one.

Each case also asserts whether the underlying command RAN, not only what the wrapper exited
with. A guard that returned 1 while still letting the build proceed would satisfy an
exit-code-only table, and so would one that blocked everything - including the host queries
you need in order to diagnose a broken resolver.

Plain python with no test framework, matching the rest of scripts/. Run it directly:

    python scripts/tests/build_wrapper_sdk_guard_tests.py

Exits non-zero if any case behaves differently from the table at the bottom.
"""
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BUILD_SH = REPO / "scripts" / "build.sh"
BUILD_PS1 = REPO / "scripts" / "build.ps1"

# Printed by the stub for any verb that is not --version. Its presence in the output is how a
# case tells "the guard stopped this" apart from "the guard let it through".
RAN = "STUB-DOTNET-RAN-THE-COMMAND"

RESOLVER_FAILURE = """The command could not be loaded, possibly because:
  * You intended to execute a .NET application:
      The application 'build' does not exist or is not a managed .dll or .exe.
  * You intended to execute a .NET SDK command:
      A compatible .NET SDK was not found.

Requested SDK version: 10.0.300-preview.0.26177.108
global.json file: /repo/global.json

Installed SDKs:

8.0.424 [/usr/share/dotnet/sdk]
10.0.400 [/usr/share/dotnet/sdk]"""

# The same failure with the English translated away, which is what a host with a non-en UI
# culture prints. The installed-SDK list stays as it is - those are versions and paths, not
# prose - which makes this also the case proving that the " [path]" suffix is what stops the
# list from reading as a successful bare-version line.
RESOLVER_FAILURE_LOCALIZED = """Der Befehl konnte nicht geladen werden, moeglicherweise weil:
  * Sie eine .NET-Anwendung ausfuehren wollten:
      Die Anwendung "build" ist nicht vorhanden.
  * Sie einen .NET SDK-Befehl ausfuehren wollten:
      Es wurde kein kompatibles .NET SDK gefunden.

Angeforderte SDK-Version: 10.0.300-preview.0.26177.108

Installierte SDKs:

8.0.424 [/usr/share/dotnet/sdk]
10.0.400 [/usr/share/dotnet/sdk]"""

HEALTHY = "10.0.400"


def write_stub(directory: Path, version_output: str, version_status: int):
    """
    A fake `dotnet` that answers --version as configured and announces itself otherwise.

    Both stubs print the canned output with `cat`/`type` from a sibling file rather than
    building it out of echo lines. That is not tidiness: cmd's echo would need every one of
    ( ) & < > | ^ escaped, and would turn the blank lines into stray dots, so the fixture
    would quietly stop resembling what a real host prints.
    """
    directory.mkdir(parents=True, exist_ok=True)
    # Default newline translation, deliberately: on Windows this writes CRLF, so the healthy
    # cases there really do feed the guard a Windows host's actual output. That is the one
    # input that could make the anchored version match reject a good SDK and stop every build
    # on the machine, and it holds - Git Bash's command substitution drops the CR before the
    # match sees it. Passing newline="\n" here would look tidier and silently drop the only
    # coverage of that path.
    (directory / "version.txt").write_text(version_output + "\n", encoding="utf-8")

    sh = directory / "dotnet"
    sh.write_text(
        '#!/usr/bin/env bash\n'
        'if [ "$1" = "--version" ]; then\n'
        '  cat "$(dirname "$0")/version.txt"\n'
        '  exit ' + str(version_status) + '\n'
        'fi\n'
        'echo "' + RAN + '"\n'
        'exit 0\n',
        encoding="utf-8",
        newline="\n",
    )
    sh.chmod(0o755)

    if os.name == "nt":
        # PowerShell will not run the extensionless script above; it needs something on
        # PATHEXT, and .cmd is the one it resolves from PATH the way it resolves dotnet.exe.
        (directory / "dotnet.cmd").write_text(
            "@echo off\r\n"
            'if "%1"=="--version" (\r\n'
            '  type "%~dp0version.txt"\r\n'
            "  exit /b " + str(version_status) + "\r\n"
            ")\r\n"
            "echo " + RAN + "\r\n"
            "exit /b 0\r\n",
            encoding="utf-8",
        )


def run_wrapper(shell: str, exe: str, stub_dir: Path, args):
    env = dict(os.environ)
    env["PATH"] = str(stub_dir) + os.pathsep + env["PATH"]
    # The wrappers sweep leftover test hosts before compiling; irrelevant here, and it shells
    # out to PowerShell on every case. Opt out.
    env["NOVA_KEEP_STALE_HOSTS"] = "1"

    if shell == "sh":
        # as_posix(), because bash receives "D:\path\build.sh" with the backslashes eaten as
        # escapes and reports the script as missing. Forward slashes work on both platforms.
        cmd = [exe, BUILD_SH.as_posix()] + args
    else:
        cmd = [exe, "-NoProfile", "-File", str(BUILD_PS1)] + args

    p = subprocess.run(cmd, capture_output=True, text=True, env=env, cwd=str(REPO))
    return p.returncode, p.stdout + p.stderr


def interpreter(shell: str):
    """
    The full path to the interpreter, never the bare name.

    On Windows, handing subprocess a bare "bash" gets WSL's C:\\Windows\\System32\\bash.exe
    rather than Git Bash: CreateProcess searches System32 before it searches PATH, so the
    lookup disagrees with both shutil.which and the shell the developer is typing in. That
    bash cannot see D:\\ at all, so every case failed with "No such file or directory" while
    the script sat right where the path said it was.
    """
    return shutil.which("bash" if shell == "sh" else "pwsh")


cases = [
    # name,                                 version output,             status, args,               exit, ran
    ("resolver fails, host exits 0 (#347)", RESOLVER_FAILURE,           0,      ["build", "x.sln"], 1,    False),
    ("resolver fails, host exits 155",      RESOLVER_FAILURE,           155,    ["build", "x.sln"], 1,    False),
    ("resolver fails, localized message",   RESOLVER_FAILURE_LOCALIZED, 0,      ["build", "x.sln"], 1,    False),
    ("version printed but status nonzero",  HEALTHY,                    1,      ["build", "x.sln"], 1,    False),
    ("nothing printed at all, status 0",    "",                         0,      ["build", "x.sln"], 1,    False),
    # A usable SDK: the guard has to get out of the way, for every verb it covers.
    ("healthy sdk, build proceeds",         HEALTHY,                    0,      ["build", "x.sln"], 0,    True),
    ("healthy sdk, publish proceeds",       HEALTHY,                    0,      ["publish"],        0,    True),
    ("healthy sdk, restore proceeds",       HEALTHY,                    0,      ["restore"],        0,    True),
    # Verbs the guard does not name individually. The first draft listed the verbs it covered
    # and let every one of these through (local codex review); they are here so that a later
    # return to an allowlist fails instead of silently reopening the hole.
    ("broken resolver, vstest blocked",     RESOLVER_FAILURE,           0,      ["vstest", "x.dll"], 1,   False),
    ("broken resolver, watch blocked",      RESOLVER_FAILURE,           0,      ["watch", "run"],   1,    False),
    ("broken resolver, tool blocked",       RESOLVER_FAILURE,           0,      ["tool", "restore"], 1,   False),
    ("broken resolver, format blocked",     RESOLVER_FAILURE,           0,      ["format"],         1,    False),
    ("broken resolver, no args blocked",    RESOLVER_FAILURE,           0,      [],                 1,    False),
    # SDK-global options come BEFORE the verb, and a predicate that judged only the leading
    # token read these as SDK-free because they start with a dash (local codex review).
    ("broken resolver, -d build blocked",   RESOLVER_FAILURE,           0,      ["-d", "build"],    1,    False),
    ("broken resolver, --diagnostics build", RESOLVER_FAILURE,          0,      ["--diagnostics", "build"], 1, False),
    # The exempt forms need no SDK, so they must still work when none resolves - and the host
    # queries among them are what someone runs to find out why none does. A guard that blocked
    # these would take the diagnosis away along with the failure.
    ("broken resolver, --list-sdks passes", RESOLVER_FAILURE,           0,      ["--list-sdks"],    0,    True),
    ("broken resolver, --info passes",      RESOLVER_FAILURE,           0,      ["--info"],         0,    True),
    ("broken resolver, --version passes",   RESOLVER_FAILURE,           0,      ["--version"],      0,    False),
    ("broken resolver, exec passes",        RESOLVER_FAILURE,           0,      ["exec", "a.dll"],  0,    True),
    ("broken resolver, a.dll passes",       RESOLVER_FAILURE,           0,      ["a.dll"],          0,    True),
    ("broken resolver, -d exec passes",     RESOLVER_FAILURE,           0,      ["-d", "exec", "a.dll"], 0, True),
    ("broken resolver, upper A.DLL passes", RESOLVER_FAILURE,           0,      ["A.DLL"],          0,    True),
    # Documented limit, pinned so it is a known trade rather than a surprise: a host option
    # that takes a value puts a non-option token in front of the .dll, so the predicate
    # guards a runtime-only run. Telling them apart needs a table of which host options
    # consume a value - the allowlist shape that caused both defects this guard was already
    # corrected for, and one that fails silent-green when incomplete. Erring loud is the
    # trade. Change this case only alongside an explicit exemption, never by teaching the
    # predicate the host's option grammar.
    ("--roll-forward + dll guarded (known)", RESOLVER_FAILURE,         0,      ["--roll-forward", "LatestMajor", "a.dll"], 1, False),
]

failures = 0
ran_any = False

for shell, label in (("sh", "scripts/build.sh"), ("ps1", "scripts/build.ps1")):
    exe = interpreter(shell)
    if exe is None:
        print(f"-- {label}: interpreter not present, skipped")
        continue

    print(f"-- {label} (via {exe})")
    for name, out, status, args, expect_code, expect_ran in cases:
        root = Path(tempfile.mkdtemp())
        try:
            write_stub(root / "bin", out, status)
            code, output = run_wrapper(shell, exe, root / "bin", args)
        finally:
            shutil.rmtree(root, ignore_errors=True)

        ran = RAN in output
        ok = code == expect_code and ran == expect_ran
        failures += 0 if ok else 1
        ran_any = True
        print(
            f"   {'PASS' if ok else 'FAIL'}  {name:<38} "
            f"exit={code} (want {expect_code})  ran={ran} (want {expect_ran})"
        )
        if not ok:
            print("         " + output.strip().replace("\n", "\n         ")[:600])

# A table that silently ran nothing is the exact failure mode this file is about.
if not ran_any:
    print("\nneither bash nor pwsh was available - no case actually ran")
    sys.exit(1)

print()
print("all guard cases behaved" if failures == 0 else f"{failures} case(s) misbehaved")
sys.exit(1 if failures else 0)
