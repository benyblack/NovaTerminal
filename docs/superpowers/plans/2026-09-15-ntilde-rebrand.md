# Ntilde Rebrand Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the product and every identifier from NovaTerminal to Ntilde in one PR, with a one-time settings migration for existing installs.

**Architecture:** A throwaway Python sweep script (kept in the session scratchpad, never committed) performs the mechanical rename in two commits: path renames first, then ordered token replacement. Hand-written commits follow for the code that needs judgment: data-folder migration, dual-extension readers, packaging identity, docs. All work happens on branch `rebrand/ntilde` in the worktree `D:\projects\nova2\.worktrees\ntilde-rebrand`, cut from main at `c024293`.

**Tech Stack:** .NET 10 / C# (xunit tests), Avalonia, MSBuild, Python 3.14 for the sweep, bash and PowerShell packaging scripts, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-15-ntilde-rebrand-design.md`

## Global Constraints

- Casing: `Ntilde` in C# identifiers and display strings; `ntilde` for CLI command, package names, data folder, MCP tool prefix, GitHub slug, release asset prefix; `NTILDE_*` for env vars.
- Velopack `--packId` is `NtildeApp`. It must never equal the data folder name `ntilde` (Windows paths are case-insensitive; Velopack uninstall deletes `%LocalAppData%\<packId>`).
- Never renamed: `NovaSsh*`, `nova_ssh_*`, `NOVA_SSH_RESULT_*`, `NovaSftpTransferProgressCallback*`, `NovaClientHandler`, crate names `rusty_pty` / `rusty_ssh`, the `.nova-rust-inputs.sha256` stamp, the local checkout folder `nova2`, `renovate`, and the Docker SSH e2e fixture world (`nova` test user, `nova$` prompt, `kbdnova`, `nova-pass`, `nova-key-pass`, image tag `novaterm-native-ssh-e2e`, `/novaterm-keys`, `novaterm-entrypoint.sh`, `novaterm.conf`).
- `.rec`, `.snap`, `.png`, `.ico`, `.ttf`, `.otf`, `.gitkeep` are never text-edited; only their paths move.
- Always build and test through `scripts/build.ps1` (never raw `dotnet`). App.Tests runs need `--blame-hang-timeout 5m` and must never run concurrently with another App.Tests run.
- No fabricated release data: the winget manifest is a template with `__VERSION__` / `__SHA256__` placeholders, rendered at first release.
- The sweep only touches `git ls-files` output; it runs only on a clean tree (`git status --porcelain` empty).
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

---

## Spec amendments discovered during planning

These refine the spec; Task 1 writes them into the spec file.

1. **Docker SSH e2e fixture is excluded.** `tests/NovaTerminal.ExternalSuites/NativeSsh/Dockerfile` creates user `nova` with prompt `nova$ `; 25 byte-exact `.rec` fixtures and `NativeSshReplayParityTests` assert that literal prompt. Renaming it breaks recorded parity for no user-visible gain.
2. **`packaging/arch` (AUR) exists on main** and was not in the spec. Package becomes `ntilde-bin` with `provides=('ntilde')`, `conflicts=('ntilde' 'novaterminal' 'novaterminal-bin')`, `replaces=('novaterminal-bin')`.
3. **winget ships a template, not a versioned manifest set.** A manifest for an unpublished release cannot carry a real SHA256. The `0.3.0` set is deleted; `packaging/winget/template/` holds placeholder manifests; the README recipe renders them at first release.
4. **`novarec` is a header field, not an extension.** `.rec` files carry `{"type":"novarec",...}`. New recordings write `ntilderec`; the reader accepts both.
5. **Release asset prefix is lowercase `ntilde-`** via a dedicated sweep rule (`NovaTerminal-win-x64-...` becomes `ntilde-win-x64-...`). Velopack nupkgs derive from the pack ID and are `NtildeApp-*`.
6. **Homebrew PR #453 is still open.** The cask lane is not on main. Task 12 merges main after it lands and re-runs the sweep.
7. **Mechanical commit is split in two** (paths, then text) so git rename detection and `git log --follow` survive.

---

## File structure

**New files**
- `src/Ntilde.App/Shell/WorkspaceBundleNaming.cs`: extension constants and the two naming helpers for workspace bundles (currently inline in MainWindow, four copies).
- `tests/Ntilde.App.Tests/Core/WorkspaceBundleNamingTests.cs`
- `packaging/winget/template/benyblack.ntilde.yaml`, `benyblack.ntilde.installer.yaml`, `benyblack.ntilde.locale.en-US.yaml`
- `docs/announcements/2026-09-15-ntilde-rebrand.md`
- Scratchpad only (not committed): `<scratchpad>/sweep.py`

**Modified beyond the mechanical sweep**
- `src/Ntilde.App/Shell/AppPaths.cs`: `AppName = "ntilde"`, `LegacyRootDirectory`, `MigrateLegacyRoot`, marker file.
- `src/Ntilde.AgentHost.Contracts/AgentHostDiscovery.cs`, `src/Ntilde.McpServer/Tools/BackupTools.cs`: folder literal `"ntilde"`.
- `src/Ntilde.Backup/BackupService.cs`: `LegacyBundleExtension`, dual listing.
- `src/Ntilde.VT/ReplayModels.cs`, `src/Ntilde.Replay/Replay/PtyRecorder.cs`, `src/Ntilde.Replay/Replay/ReplayRunner.cs`, `tests/Ntilde.ExternalSuites/Vttest/RecWriter.cs`: header token constants.
- `src/Ntilde.App/MainWindow.axaml.cs`: four picker sites use `WorkspaceBundleNaming`.
- `packaging/linux/build-deb.sh`, `packaging/linux/test-build-deb.sh`, `packaging/arch/build-arch.sh`, `packaging/arch/test-build-arch.sh`
- `packaging/winget/README.md`, `packaging/winget/submit-first-time.ps1`; `packaging/winget/0.3.0/` deleted
- `.github/workflows/release.yml`: Velopack pack-ID comments
- `README.md`, `packaging/linux/ntilde.1`, `assets/command-knowledge/command-catalogue.json`, `scripts/generate-command-catalogue.ps1`, `src/Ntilde.CommandAssist/Models/CommandKnowledgeCatalogue.cs`

---

### Task 1: Record the spec amendments

**Files:**
- Modify: `docs/superpowers/specs/2026-09-15-ntilde-rebrand-design.md`

- [ ] **Step 1: Append the amendments section**

Add before the `## Risks` heading:

```markdown
## Amendments (2026-09-15, during planning)

1. The Docker SSH e2e fixture (`tests/.../NativeSsh/Dockerfile`, user `nova`, prompt `nova$`,
   image `novaterm-native-ssh-e2e`) is excluded from the rename. 25 byte-exact `.rec` fixtures
   and the parity tests assert the recorded prompt literally.
2. `packaging/arch` (AUR, `novaterminal-bin`) is on main and joins the rename: `ntilde-bin`,
   `provides=('ntilde')`, `conflicts=('ntilde' 'novaterminal' 'novaterminal-bin')`,
   `replaces=('novaterminal-bin')`.
3. winget: the `0.3.0` manifests are deleted and replaced by `packaging/winget/template/`
   with `__VERSION__` / `__SHA256__` placeholders, rendered at the first Ntilde release. A
   manifest for an unpublished asset cannot carry a real hash.
4. `novarec` is the replay header `type` field, not a file extension. New recordings write
   `ntilderec`; readers accept both tokens.
5. Release asset prefix is lowercase `ntilde-` (`ntilde-win-x64-v1.2.3.zip`); Velopack nupkgs
   follow the pack ID: `NtildeApp-1.2.3-full.nupkg`.
6. Homebrew PR #453 has not merged. The rebrand branch merges main after it lands and re-runs
   the sweep over the new files.
7. The mechanical work lands as two commits (path renames, then text) so rename detection and
   `git log --follow` keep working.
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-09-15-ntilde-rebrand-design.md
git commit -q -F - <<'EOF'
docs(spec): record rebrand amendments found while planning

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 2: Write the sweep script and dry-run it

**Files:**
- Create (scratchpad, NOT committed): `C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\6baad9ce-5765-4899-be0e-32951a426793\scratchpad\sweep.py`

**Interfaces:**
- Produces: `python sweep.py paths` (git-mv every tracked path containing a token), `python sweep.py text` (rewrite tracked text files), `python sweep.py audit` (print every remaining `nova` hit outside the allowlist; exit 1 if any). Idempotent: re-running on renamed content is a no-op.

- [ ] **Step 1: Write the script**

```python
#!/usr/bin/env python3
"""Throwaway NovaTerminal -> Ntilde sweep. Run from the worktree root on a clean tree.

  python sweep.py paths   # git mv every tracked path whose components contain a token
  python sweep.py text    # rewrite tracked text files in place
  python sweep.py audit   # list remaining 'nova' hits outside the allowlist; exit 1 if any
"""
import os
import re
import subprocess
import sys

TEXT_EXT = {
    ".cs", ".md", ".json", ".csproj", ".axaml", ".txt", ".sh", ".astro", ".yml", ".yaml",
    ".ps1", ".rs", ".py", ".toml", ".mjs", ".ts", ".svg", ".props", ".lock", ".sln",
    ".runsettings", ".manifest", ".fish", ".desktop", ".css", ".1", ".rb", ".xml",
}
TEXT_BASENAMES = {"LICENSE", ".editorconfig", ".gitignore", ".gitattributes"}
SKIP_FILE_SUFFIXES = ("/NativeSsh/Dockerfile",)   # docker e2e fixture stays as-is
NATIVE_MARKER = "/native/"                        # Rust crates: product-name rules only

# Swapped for placeholders before the rules run, restored after.
PROTECTED = [
    r"NovaSsh\w*", r"nova_ssh_\w*", r"NOVA_SSH_RESULT_\w*",
    r"NovaSftpTransferProgressCallback\w*", r"NovaClientHandler",
    r"\.nova-rust-inputs", r"nova2", r"[Rr]enovate\w*",
    # Docker SSH e2e fixture world - byte-exact recordings depend on these literals.
    r"nova\$", r'"nova"', r"'nova'", r"/home/nova\b", r"nova-pass", r"nova-key-pass",
    r"kbdnova", r"nova:nova", r"novaterm-native-ssh-e2e", r"novaterm-keys",
    r"novaterm-entrypoint", r"novaterm\.conf",
]

# Applied everywhere, Rust crates included. Order matters: longest / most specific first.
PRODUCT_RULES = [
    (r"benyblack/NovaTerminal", "benyblack/ntilde"),
    (r"benyblack\.NovaTerminal", "benyblack.ntilde"),
    (r"github\.io/NovaTerminal", "github.io/ntilde"),
    (r"'/NovaTerminal'", "'/ntilde'"),
    # Release asset prefix is lowercase: NovaTerminal-win-x64-v1.zip -> ntilde-win-x64-v1.zip
    (r"NovaTerminal-(?=Setup|win-|linux-|osx-|\$|\{|<|sidecar)", "ntilde-"),
    (r"Nova Terminal", "Ntilde"),
    (r"nova terminal", "ntilde"),
    (r"NovaTerminal", "Ntilde"),
    (r"NOVATERMINAL", "NTILDE"),
    (r"novaterminal", "ntilde"),
]
# Applied outside the Rust crates only.
SHORT_RULES = [
    (r"NOVATERM_", "NTILDE_"),
    (r"NovaTerm", "Ntilde"),
    (r"novaterm", "ntilde"),
    (r"NOVA", "NTILDE"),
    (r"Nova", "Ntilde"),
    (r"(?<![A-Za-z])nova", "ntilde"),
]

AUDIT_ALLOW = re.compile(
    r"NovaSsh|nova_ssh_|NOVA_SSH_RESULT_|NovaSftpTransferProgressCallback|NovaClientHandler"
    r"|\.nova-rust-inputs|nova2|[Rr]enovate|nova\$|\"nova\"|'nova'|/home/nova|nova-pass"
    r"|nova-key-pass|kbdnova|nova:nova|novaterm-native-ssh-e2e|novaterm-keys"
    r"|novaterm-entrypoint|novaterm\.conf|__NOVA_(BASHRC|ZSHRC|ZPROFILE|FISHRC)__|novapart"
    r"|/NativeSsh/Dockerfile:|\.rec:|\.snap:|useradd .*nova|Match User kbdnova"
)


def tracked():
    out = subprocess.run(["git", "ls-files", "-z"], capture_output=True, check=True).stdout
    return [p.decode("utf-8") for p in out.split(b"\0") if p]


def transform(text, product_only):
    holes = []

    def stash(m):
        holes.append(m.group(0))
        return f"\x00{len(holes) - 1}\x00"

    for pat in PROTECTED:
        text = re.sub(pat, stash, text)
    for pat, rep in PRODUCT_RULES:
        text = re.sub(pat, rep, text)
    if not product_only:
        for pat, rep in SHORT_RULES:
            text = re.sub(pat, rep, text)
    return re.sub(r"\x00(\d+)\x00", lambda m: holes[int(m.group(1))], text)


def rename_paths():
    total = 0
    while True:
        moves = {}
        for p in tracked():
            parts = p.split("/")
            for i, part in enumerate(parts):
                new = transform(part, product_only=False)
                if new != part:
                    moves["/".join(parts[: i + 1])] = "/".join(parts[:i] + [new])
                    break
        if not moves:
            break
        for old, new in sorted(moves.items()):
            os.makedirs(os.path.dirname(new) or ".", exist_ok=True)
            subprocess.run(["git", "mv", old, new], check=True)
            total += 1
    # git mv leaves empty source directories behind on disk
    for root, dirs, files in os.walk(".", topdown=False):
        if ".git" in root:
            continue
        for d in dirs:
            full = os.path.join(root, d)
            if not os.listdir(full):
                os.rmdir(full)
    print(f"renamed {total} paths")


def is_text(path):
    base = os.path.basename(path)
    return base in TEXT_BASENAMES or os.path.splitext(base)[1] in TEXT_EXT


def rewrite_text():
    changed = skipped = 0
    for p in tracked():
        if not is_text(p) or p.endswith(SKIP_FILE_SUFFIXES):
            continue
        with open(p, "rb") as f:
            data = f.read()
        bom = data.startswith(b"\xef\xbb\xbf")
        try:
            text = (data[3:] if bom else data).decode("utf-8")
        except UnicodeDecodeError:
            print(f"SKIP (not utf-8): {p}")
            skipped += 1
            continue
        new = transform(text, product_only=NATIVE_MARKER in p)
        if new != text:
            with open(p, "wb") as f:
                f.write((b"\xef\xbb\xbf" if bom else b"") + new.encode("utf-8"))
            changed += 1
    print(f"rewrote {changed} files, skipped {skipped}")


def audit():
    out = subprocess.run(["git", "grep", "-n", "-i", "nova"], capture_output=True).stdout
    bad = [l for l in out.decode("utf-8", "replace").splitlines() if not AUDIT_ALLOW.search(l)]
    for l in bad:
        print(l)
    print(f"{len(bad)} unexpected hits")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    if subprocess.run(["git", "status", "--porcelain"], capture_output=True).stdout.strip():
        sys.exit("tree is not clean; commit or discard first")
    {"paths": rename_paths, "text": rewrite_text, "audit": audit}[sys.argv[1]]()
```

- [ ] **Step 2: Dry-run the transform on a few known lines**

Run from the worktree root:

```bash
python - <<'EOF'
import importlib.util, sys
spec = importlib.util.spec_from_file_location("sweep", r"C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\6baad9ce-5765-4899-be0e-32951a426793\scratchpad\sweep.py")
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
cases = {
 "NovaTerminalApp": "NtildeApp",
 "NOVATERMINAL_REPO_ROOT": "NTILDE_REPO_ROOT",
 "NOVATERM_APPDATA_ROOT": "NTILDE_APPDATA_ROOT",
 "benyblack/NovaTerminal/releases": "benyblack/ntilde/releases",
 "NovaTerminal-win-x64-$tag.zip": "ntilde-win-x64-$tag.zip",
 "NovaTerminal-${{ matrix.rid }}": "ntilde-${{ matrix.rid }}",
 "NovaTerminal-sidecar": "ntilde-sidecar",
 "NovaTerminal-specific": "Ntilde-specific",
 "com.novaterminal.Vault": "com.ntilde.Vault",
 "__nova_precmd": "__ntilde_precmd",
 "nova-shell-integration.sh": "ntilde-shell-integration.sh",
 ".novabackup": ".ntildebackup",
 "\"novarec\"": "\"ntilderec\"",
 "NovaSshSafeHandle": "NovaSshSafeHandle",
 "nova_ssh_connect": "nova_ssh_connect",
 "NOVA_SSH_RESULT_OK": "NOVA_SSH_RESULT_OK",
 "renovatebot.com": "renovatebot.com",
 "D:/projects/nova2/src": "D:/projects/nova2/src",
 "PS1='nova$ '": "PS1='nova$ '",
 "SshUser = \"nova\"": "SshUser = \"nova\"",
 "novaterm-native-ssh-e2e:v4": "novaterm-native-ssh-e2e:v4",
 "kbdnova": "kbdnova",
 "/usr/bin/nova": "/usr/bin/ntilde",
 "man nova": "man ntilde",
 "Nova Terminal remote": "Ntilde remote",
 "cd src/NovaTerm": "cd src/Ntilde",
 "novaterm_test_root_": "ntilde_test_root_",
 "MirrorWriteNovaPwd": "MirrorWriteNtildePwd",
 "cask \"novaterminal\"": "cask \"ntilde\"",
 "const DEFAULT_BASE = '/NovaTerminal';": "const DEFAULT_BASE = '/ntilde';",
}
bad = [(k, m.transform(k, False), v) for k, v in cases.items() if m.transform(k, False) != v]
print("native-only:", m.transform("Closed by NovaTerminal / __NOVA_ZSHRC__ / .novapart", True))
print("FAIL" if bad else "all cases pass", bad)
EOF
```

Expected: `all cases pass []` and the native-only line reads `Closed by Ntilde / __NOVA_ZSHRC__ / .novapart`.

- [ ] **Step 3: Verify the tree is clean and no `Ntilde` identifier already exists**

```bash
git status --porcelain
git grep -il ntilde -- . ':!docs/superpowers'
```

Expected: both print nothing.

---

### Task 3: Mechanical commit A: path renames

**Files:**
- Every tracked path containing `NovaTerminal`, `novaterminal`, `nova_icon`, `nova-`, `nova.` (about 1000 files under `src/`, `tests/`, `packaging/`, `assets/`, `docs/`, plus `NovaTerminal.sln`).

- [ ] **Step 1: Run the path pass**

```bash
python "C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\6baad9ce-5765-4899-be0e-32951a426793\scratchpad\sweep.py" paths
```

Expected: `renamed N paths` with N roughly 40 to 80 (directories move as units).

- [ ] **Step 2: Check no tracked path still carries a token**

```bash
git ls-files | grep -iE 'nova' | grep -vE 'NativeSsh/Dockerfile|nova2'
ls src tests
```

Expected: the grep prints nothing. `ls` shows `Ntilde.App`, `Ntilde.Core`, ..., `Ntilde.App.Tests`, etc.

- [ ] **Step 3: Confirm git sees renames, not delete+add**

```bash
git status --porcelain | awk '{print $1}' | sort | uniq -c
```

Expected: only `R` entries (renamed), no `D` or `A`.

- [ ] **Step 4: Commit**

```bash
git commit -q -F - <<'EOF'
rebrand: rename every NovaTerminal path to Ntilde

Mechanical: git mv only, contents untouched. Produced by the sweep
script described in docs/superpowers/plans/2026-09-15-ntilde-rebrand.md
(Task 2); re-run `sweep.py paths` on the parent commit to reproduce.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 4: Mechanical commit B: text replacement and audit

**Files:**
- Every tracked text file (about 1150 files).

- [ ] **Step 1: Run the text pass**

```bash
python "C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\6baad9ce-5765-4899-be0e-32951a426793\scratchpad\sweep.py" text
```

Expected: `rewrote ~1150 files, skipped 0`. If any `SKIP (not utf-8)` line appears, open that file, confirm it is text, and hand-edit it.

- [ ] **Step 2: Run the audit**

```bash
python "C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\6baad9ce-5765-4899-be0e-32951a426793\scratchpad\sweep.py" audit
```

Expected: `0 unexpected hits`. If hits remain, they are either (a) a legitimately protected token missing from the allowlist, so extend `AUDIT_ALLOW`, or (b) a real miss, so add a rule and re-run. The script refuses a dirty tree, so: `git commit -q -m WIP`, fix the rule, run `text` again (idempotent on already-renamed content), then fold with `git reset --soft HEAD~1` before the real commit in Step 5. Do not hand-edit misses; the script is the record.

- [ ] **Step 3: Spot-check the protected zones**

```bash
git grep -c 'NovaSshSafeHandle' -- src/Ntilde.Platform/Ssh/Native/NativeSshSafeHandle.cs
git grep -n 'ImageTag = ' -- tests/Ntilde.Platform.Tests/Ssh/DockerSshFixture.cs
git grep -n 'Closed by' -- src/Ntilde.App/native/rusty_ssh/src/lib.rs
git grep -n 'renovate' -- .github/renovate.json | head -1
git diff --stat -- '*.rec' '*.snap' '*.png' '*.ico' | tail -1
```

Expected: the safe-handle count is unchanged from before, the image tag still reads `novaterm-native-ssh-e2e:v4`, the Rust file says `Closed by Ntilde`, renovate is intact, and the binary diffstat is empty.

- [ ] **Step 4: Build the solution**

```powershell
scripts/build.ps1 build Ntilde.sln -c Debug 2>&1 | Tee-Object -FilePath "$env:TEMP\ntilde-build.log" | Select-String -Pattern 'error|Warning CS|Build succeeded|Build FAILED' | Select-Object -First 40
```

Expected: `Build succeeded`. Typical fallout if it fails: a hard-coded resource URI `avares://NovaTerminal/...` inside a binary-ish file the sweep skipped, or a generated file. Fix by extending the sweep, not by hand.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -q -F - <<'EOF'
rebrand: replace NovaTerminal tokens with Ntilde across the tree

Mechanical text pass from the sweep script (plan Task 2): ordered
token rules, SSH FFI names and the docker e2e fixture protected,
Rust crates get product-name rules only. Audit is clean.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 5: Prove the swept tree passes the guard test projects

**Files:**
- No edits expected. Fix-ups, if any, go in this task's commit.

- [ ] **Step 1: Architecture and MCP drift guards**

```powershell
scripts/build.ps1 test tests/Ntilde.Architecture.Tests -c Debug --no-build 2>&1 | Select-String -Pattern 'Passed!|Failed!|Failed ' | Select-Object -First 20
scripts/build.ps1 test tests/Ntilde.McpServer.Tests -c Debug --no-build 2>&1 | Select-String -Pattern 'Passed!|Failed!|Failed ' | Select-Object -First 20
scripts/build.ps1 test tests/Ntilde.Core.Tests -c Debug --no-build 2>&1 | Select-String -Pattern 'Passed!|Failed!|Failed ' | Select-Object -First 20
```

Expected: three `Passed!` lines.

- [ ] **Step 2: Python guard tests for the build wrappers**

```bash
python scripts/tests/build_wrapper_sdk_guard_tests.py 2>&1 | tail -3
```

Expected: `OK`.

- [ ] **Step 3: If anything failed, fix and commit**

Fix only what the failure names. Then:

```bash
git add -A
git commit -q -F - <<'EOF'
rebrand: fix guard-test fallout from the sweep

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 6: Data-folder migration

**Files:**
- Modify: `src/Ntilde.App/Shell/AppPaths.cs`
- Modify: `src/Ntilde.AgentHost.Contracts/AgentHostDiscovery.cs:16`
- Modify: `src/Ntilde.McpServer/Tools/BackupTools.cs:100-101`
- Test: `tests/Ntilde.App.Tests/Core/AppPathsTests.cs`

**Interfaces:**
- Produces: `public static string AppPaths.LegacyRootDirectory`, `public static bool AppPaths.MigrateLegacyRoot(string legacyRoot, string newRoot)`, `public const string AppPaths.MigrationMarkerFileName = ".migrated-from-novaterminal"`.

- [ ] **Step 1: Write the failing tests**

Append inside `AppPathsTests` (namespace is now `Ntilde.Tests.Core`):

```csharp
    [Fact]
    public void MigrateLegacyRoot_CopiesEverythingExceptLogs_AndWritesMarker()
    {
        string temp = CreateTempDirectory();
        try
        {
            string legacy = Path.Combine(temp, "NovaTerminal");
            string fresh = Path.Combine(temp, "ntilde");
            Directory.CreateDirectory(Path.Combine(legacy, "themes"));
            Directory.CreateDirectory(Path.Combine(legacy, "logs"));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"FontSize\":13}");
            File.WriteAllText(Path.Combine(legacy, "themes", "dark.json"), "{}");
            File.WriteAllText(Path.Combine(legacy, "logs", "debug.log"), "noise");

            bool migrated = AppPaths.MigrateLegacyRoot(legacy, fresh);

            Assert.True(migrated);
            Assert.Equal("{\"FontSize\":13}", File.ReadAllText(Path.Combine(fresh, "settings.json")));
            Assert.True(File.Exists(Path.Combine(fresh, "themes", "dark.json")));
            Assert.False(Directory.Exists(Path.Combine(fresh, "logs")));
            Assert.True(File.Exists(Path.Combine(fresh, AppPaths.MigrationMarkerFileName)));
            Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyRoot_RunsOnce_MarkerBlocksSecondCopy()
    {
        string temp = CreateTempDirectory();
        try
        {
            string legacy = Path.Combine(temp, "NovaTerminal");
            string fresh = Path.Combine(temp, "ntilde");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "first");
            Assert.True(AppPaths.MigrateLegacyRoot(legacy, fresh));

            File.WriteAllText(Path.Combine(legacy, "settings.json"), "second");
            File.SetLastWriteTimeUtc(Path.Combine(legacy, "settings.json"), DateTime.UtcNow.AddMinutes(5));

            bool migratedAgain = AppPaths.MigrateLegacyRoot(legacy, fresh);

            Assert.False(migratedAgain);
            Assert.Equal("first", File.ReadAllText(Path.Combine(fresh, "settings.json")));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyRoot_NoLegacyFolder_DoesNothing()
    {
        string temp = CreateTempDirectory();
        try
        {
            string fresh = Path.Combine(temp, "ntilde");

            bool migrated = AppPaths.MigrateLegacyRoot(Path.Combine(temp, "NovaTerminal"), fresh);

            Assert.False(migrated);
            Assert.False(Directory.Exists(fresh));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void RootDirectory_FolderNameIsLowercaseNtilde()
    {
        string? previous = Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", null);
            Assert.Equal("ntilde", Path.GetFileName(AppPaths.RootDirectory));
            Assert.Equal("NovaTerminal", Path.GetFileName(AppPaths.LegacyRootDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", previous);
        }
    }
```

- [ ] **Step 2: Run to verify they fail**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests -c Debug --filter "FullyQualifiedName~AppPathsTests" --blame-hang-timeout 5m 2>&1 | Select-String -Pattern 'error CS|Passed!|Failed!' | Select-Object -First 10
```

Expected: compile errors naming `MigrateLegacyRoot`, `MigrationMarkerFileName`, `LegacyRootDirectory`.

- [ ] **Step 3: Implement in AppPaths**

Replace the constants block at the top of `AppPaths`:

```csharp
        private const string AppName = "ntilde";
        /// <summary>Data folder name before the Ntilde rebrand. Read once for migration; never written.</summary>
        private const string LegacyAppName = "NovaTerminal";
        /// <summary>Written into the new root after the one-time copy so it never runs twice.</summary>
        public const string MigrationMarkerFileName = ".migrated-from-novaterminal";
        private const string RootOverrideEnvVar = "NTILDE_APPDATA_ROOT";
```

Add after `RootDirectory`:

```csharp
        /// <summary>Where a pre-rebrand install kept its data. Only consulted by <see cref="MigrateLegacyRoot"/>.</summary>
        public static string LegacyRootDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyAppName);
```

In `EnsureInitialized`, insert as the first statement inside the `try`, before `Directory.CreateDirectory(RootDirectory)`:

```csharp
                    // Skipped under the env override: tests and portable installs point at a
                    // scratch root, and copying a developer's real NovaTerminal folder into it
                    // would be a surprise. Real installs have no override set.
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootOverrideEnvVar)))
                    {
                        MigrateLegacyRoot(LegacyRootDirectory, RootDirectory);
                    }
```

Add the method next to `MigrateDirectoryIfNeeded`:

```csharp
        /// <summary>
        /// One-time copy of a pre-rebrand data folder into the new root. Copies every top-level
        /// file and every subdirectory except <c>logs</c>, newer-file-wins per file, never deletes
        /// the source, and writes <see cref="MigrationMarkerFileName"/> so it runs once.
        /// </summary>
        /// <returns><c>true</c> when a copy ran; <c>false</c> when there was nothing to do.</returns>
        public static bool MigrateLegacyRoot(string legacyRoot, string newRoot)
        {
            try
            {
                if (!Directory.Exists(legacyRoot)) return false;

                string legacyFull = Path.GetFullPath(legacyRoot);
                string newFull = Path.GetFullPath(newRoot);
                if (PathsEqual(legacyFull, newFull)) return false;

                string marker = Path.Combine(newFull, MigrationMarkerFileName);
                if (File.Exists(marker)) return false;

                Directory.CreateDirectory(newFull);

                foreach (string file in Directory.GetFiles(legacyFull))
                {
                    MigrateFileIfNeeded(file, Path.Combine(newFull, Path.GetFileName(file)));
                }

                foreach (string directory in Directory.GetDirectories(legacyFull))
                {
                    string name = Path.GetFileName(directory);
                    if (string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase)) continue;
                    MigrateDirectoryIfNeeded(directory, Path.Combine(newFull, name));
                }

                File.WriteAllText(
                    marker,
                    $"Settings were copied from {legacyFull} on {DateTime.UtcNow:O}. The old folder was left in place and can be deleted.{Environment.NewLine}");
                return true;
            }
            catch
            {
                // Best-effort migration only; a failed copy must never block startup.
                return false;
            }
        }
```

- [ ] **Step 4: Point the other two folder consumers at the lowercase name**

`AgentHostDiscovery.cs:16`: change `private const string AppName = "Ntilde";` to `"ntilde"`.
`BackupTools.cs:101`: change the `"Ntilde"` literal passed to `Path.Combine` to `"ntilde"`.

Confirm no other consumer builds the folder path by hand:

```bash
git grep -nE 'LocalApplicationData\)' -- 'src/**/*.cs' | grep -vE 'AppPaths.cs|AgentHostDiscovery.cs|BackupTools.cs|ProfileImporter.cs'
```

Expected: nothing (ProfileImporter reads other apps' folders, not ours).

- [ ] **Step 5: Run the tests**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests -c Debug --filter "FullyQualifiedName~AppPathsTests" --blame-hang-timeout 5m 2>&1 | Select-String -Pattern 'Passed!|Failed!|Failed ' | Select-Object -First 10
```

Expected: `Passed!` with the four new tests included.

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.App/Shell/AppPaths.cs src/Ntilde.AgentHost.Contracts/AgentHostDiscovery.cs src/Ntilde.McpServer/Tools/BackupTools.cs tests/Ntilde.App.Tests/Core/AppPathsTests.cs
git commit -q -F - <<'EOF'
rebrand: copy settings from the NovaTerminal data folder on first launch

Data root is now LocalAppData/ntilde. One-time copy from the old
folder (newer-file-wins, logs skipped, source untouched), guarded by a
marker file so it never runs twice, and skipped under
NTILDE_APPDATA_ROOT so tests never touch a developer's real folder.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 7: Backup bundles accept the legacy extension

**Files:**
- Modify: `src/Ntilde.Backup/BackupService.cs:24` and `ListSnapshots` (around line 1051)
- Test: `tests/Ntilde.App.Tests/Backup/SnapshotTests.cs`

**Interfaces:**
- Produces: `public const string BackupService.LegacyBundleExtension = ".novabackup"`. `BundleExtension` is already `".ntildebackup"` after the sweep.

- [ ] **Step 1: Write the failing test**

Append inside `SnapshotTests`:

```csharp
    /// <summary>
    /// The data-folder migration copies a NovaTerminal install's backups/ verbatim, so the
    /// snapshot list must still see .novabackup files or a migrated user loses every restore
    /// point the moment they upgrade.
    /// </summary>
    [Fact]
    public void ListSnapshots_IncludesLegacyNovabackupFiles()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        var info = service.Snapshot(SnapshotReason.Auto)!;
        string legacyPath = Path.ChangeExtension(info.FilePath, BackupService.LegacyBundleExtension);
        File.Move(info.FilePath, legacyPath);

        var listed = service.ListSnapshots();

        var only = Assert.Single(listed);
        Assert.Equal(Path.GetFullPath(legacyPath), Path.GetFullPath(only.FilePath));
        Assert.Equal(info.Id, only.Id);
    }
```

- [ ] **Step 2: Run to verify it fails**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests -c Debug --filter "FullyQualifiedName~SnapshotTests" --blame-hang-timeout 5m 2>&1 | Select-String -Pattern 'error CS|Passed!|Failed!' | Select-Object -First 10
```

Expected: compile error on `LegacyBundleExtension`.

- [ ] **Step 3: Implement**

In `BackupService`:

```csharp
    public const string BundleExtension = ".ntildebackup";

    /// <summary>
    /// Extension written before the Ntilde rebrand. Listed and importable forever, never
    /// written: a migrated backups/ folder is full of these.
    /// </summary>
    public const string LegacyBundleExtension = ".novabackup";
```

In `ListSnapshots`, replace the `foreach` line:

```csharp
        foreach (string path in Directory.EnumerateFiles(BackupsDirectory))
        {
            if (!path.EndsWith(BundleExtension, StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(LegacyBundleExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryParseSnapshot(path, out var info)) results.Add(info!);
        }
```

Import already takes any path (`BackupCommand.Import`, `BackupService.Import`), so nothing else changes.

- [ ] **Step 4: Run the Backup test folder**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests -c Debug --filter "FullyQualifiedName~Ntilde.Tests.Backup" --blame-hang-timeout 5m 2>&1 | Select-String -Pattern 'Passed!|Failed!|Failed ' | Select-Object -First 10
```

Expected: `Passed!`.

- [ ] **Step 5: Commit**

```bash
git add src/Ntilde.Backup/BackupService.cs tests/Ntilde.App.Tests/Backup/SnapshotTests.cs
git commit -q -F - <<'EOF'
rebrand: keep listing .novabackup snapshots after the extension change

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 8: Replay header accepts both type tokens

**Files:**
- Modify: `src/Ntilde.VT/ReplayModels.cs` (class `ReplayHeader`)
- Modify: `src/Ntilde.Replay/Replay/PtyRecorder.cs:30`
- Modify: `src/Ntilde.Replay/Replay/ReplayRunner.cs:102`
- Modify: `tests/Ntilde.ExternalSuites/Vttest/RecWriter.cs` (writes the header literal)
- Modify: `docs/REPLAY_FORMAT_V2.md`
- Test: `tests/Ntilde.App.Tests/ReplayTests/ReplayV2Tests.cs`

**Interfaces:**
- Produces: `ReplayHeader.TypeToken = "ntilderec"`, `ReplayHeader.LegacyTypeToken = "novarec"`, `static bool ReplayHeader.IsKnownType(string? type)`.

- [ ] **Step 1: Write the failing test**

Append inside `ReplayV2Tests`:

```csharp
        /// <summary>
        /// Every recording made before the rebrand (and all 25 checked-in fixtures) carries
        /// type "novarec". The runner must still detect them as v2, or they silently fall back
        /// to the raw v1 path and replay the header line as terminal output.
        /// </summary>
        [Fact]
        public async Task ReplayRunner_AcceptsLegacyNovarecHeaderAsV2()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                string header = "{\"type\":\"novarec\",\"v\":2,\"cols\":40,\"rows\":5,\"date\":\"2026-01-01T00:00:00.0000000Z\",\"shell\":\"pwsh.exe\"}";
                File.WriteAllText(tempFile, header + "\n");

                int cols = 0, rows = 0;
                var gathered = new StringBuilder();
                var runner = new ReplayRunner(tempFile);
                await runner.RunWithResultAsync(
                    onDataCallback: data => { gathered.Append(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                    onResizeCallback: (c, r) => { cols = c; rows = r; return Task.CompletedTask; },
                    options: new ReplayRunOptions { PlaybackMode = ReplayPlaybackMode.Virtual });

                Assert.Equal(40, cols);
                Assert.Equal(5, rows);
                Assert.DoesNotContain("novarec", gathered.ToString());
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void ReplayHeader_KnowsBothTypeTokens()
        {
            Assert.Equal("ntilderec", ReplayHeader.TypeToken);
            Assert.Equal("novarec", ReplayHeader.LegacyTypeToken);
            Assert.True(ReplayHeader.IsKnownType("ntilderec"));
            Assert.True(ReplayHeader.IsKnownType("novarec"));
            Assert.False(ReplayHeader.IsKnownType("asciicast"));
            Assert.False(ReplayHeader.IsKnownType(null));
        }
```

- [ ] **Step 2: Run to verify they fail**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests -c Debug --filter "FullyQualifiedName~ReplayV2Tests" --blame-hang-timeout 5m 2>&1 | Select-String -Pattern 'error CS|Passed!|Failed!' | Select-Object -First 10
```

Expected: compile errors on `TypeToken`, `LegacyTypeToken`, `IsKnownType`.

- [ ] **Step 3: Implement**

In `ReplayHeader` add above the `Type` property:

```csharp
        /// <summary>Header type written by current recorders.</summary>
        public const string TypeToken = "ntilderec";

        /// <summary>Header type written before the Ntilde rebrand. Accepted on read forever.</summary>
        public const string LegacyTypeToken = "novarec";

        public static bool IsKnownType(string? type)
            => type == TypeToken || type == LegacyTypeToken;
```

`PtyRecorder.cs:30`: `Type = ReplayHeader.TypeToken,`

`ReplayRunner.cs:102`: `if (header != null && ReplayHeader.IsKnownType(header.Type) && header.Version == 2)`

Find every other literal and route it through the constants:

```bash
git grep -n '"ntilderec"' -- src tests
```

Expected sites: `RecWriter.cs` (use `ReplayHeader.TypeToken`), `AgentHostReplayProtocolTests.cs:261` and `ReplayIndexTests.cs` (assert against `ReplayHeader.TypeToken`). Any reader comparing `== "ntilderec"` must use `IsKnownType`.

`docs/REPLAY_FORMAT_V2.md` line 22: change to `` - `type` (`string`): must be `"ntilderec"`; readers also accept the pre-rebrand `"novarec"` `` and update the example on line 17 to `ntilderec`.

- [ ] **Step 4: Run the replay tests**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests -c Debug --filter "FullyQualifiedName~ReplayTests|FullyQualifiedName~AgentHostReplayProtocolTests" --blame-hang-timeout 5m 2>&1 | Select-String -Pattern 'Passed!|Failed!|Failed ' | Select-Object -First 10
scripts/build.ps1 test tests/Ntilde.Platform.Tests -c Debug --filter "FullyQualifiedName~ReplayIndexTests" 2>&1 | Select-String -Pattern 'Passed!|Failed!|Failed ' | Select-Object -First 5
```

Expected: `Passed!` for both. The fixture-driven `NativeSshReplayParityTests` and the vttest replays exercise the legacy token for real.

- [ ] **Step 5: Commit**

```bash
git add -A src/Ntilde.VT/ReplayModels.cs src/Ntilde.Replay tests docs/REPLAY_FORMAT_V2.md
git commit -q -F - <<'EOF'
rebrand: write ntilderec replay headers, keep reading novarec

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 9: Workspace bundle naming helper with dual extensions

**Files:**
- Create: `src/Ntilde.App/Shell/WorkspaceBundleNaming.cs`
- Modify: `src/Ntilde.App/MainWindow.axaml.cs` lines 3024, 3031, 3062, 3069, 3100, 3108-3111, 3146 (numbers from before the sweep; content is now `.ntildews.json`)
- Test: `tests/Ntilde.App.Tests/Core/WorkspaceBundleNamingTests.cs`

**Interfaces:**
- Produces: `static class Ntilde.Shell.WorkspaceBundleNaming` with `Extension`, `LegacyExtension`, `PickerPatterns`, `SuggestedFileName(string)`, `SuggestedWorkspaceName(string)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

public sealed class WorkspaceBundleNamingTests
{
    [Theory]
    [InlineData(@"C:\x\dev.ntildews.json", "dev")]
    [InlineData(@"C:\x\dev.novaws.json", "dev")]
    [InlineData("/home/u/Team Setup.NOVAWS.JSON", "Team Setup")]
    [InlineData(@"C:\x\plain.json", "plain")]
    [InlineData(@"C:\x\odd.name.json", "odd.name")]
    public void SuggestedWorkspaceName_StripsEitherBundleExtension(string path, string expected)
    {
        Assert.Equal(expected, WorkspaceBundleNaming.SuggestedWorkspaceName(path));
    }

    [Fact]
    public void SuggestedFileName_UsesNewExtensionAndTrims()
    {
        Assert.Equal("dev.ntildews.json", WorkspaceBundleNaming.SuggestedFileName("  dev "));
    }

    [Fact]
    public void PickerPatterns_ListNewThenLegacyThenJson()
    {
        Assert.Equal(new[] { "*.ntildews.json", "*.novaws.json", "*.json" }, WorkspaceBundleNaming.PickerPatterns);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests -c Debug --filter "FullyQualifiedName~WorkspaceBundleNamingTests" --blame-hang-timeout 5m 2>&1 | Select-String -Pattern 'error CS|Passed!|Failed!' | Select-Object -First 10
```

Expected: compile error, type `WorkspaceBundleNaming` not found.

- [ ] **Step 3: Implement the helper**

```csharp
using System;
using System.IO;

namespace Ntilde.Shell
{
    /// <summary>
    /// File naming for exported workspace bundles. Bundles are JSON, so the "extension" is a
    /// two-part suffix; the pre-rebrand suffix is still recognised on import so existing
    /// exports keep opening with a sensible suggested name.
    /// </summary>
    public static class WorkspaceBundleNaming
    {
        public const string Extension = ".ntildews.json";
        public const string LegacyExtension = ".novaws.json";

        /// <summary>File-picker patterns, new suffix first so it is the default filter.</summary>
        public static readonly string[] PickerPatterns = { "*" + Extension, "*" + LegacyExtension, "*.json" };

        public static string SuggestedFileName(string workspaceName) => $"{workspaceName.Trim()}{Extension}";

        /// <summary>Workspace name to propose for a bundle picked from disk.</summary>
        public static string SuggestedWorkspaceName(string bundlePath)
        {
            string name = Path.GetFileNameWithoutExtension(bundlePath); // drops ".json"
            foreach (string marker in new[] { ".ntildews", ".novaws" })
            {
                if (name.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^marker.Length];
                }
            }

            return name;
        }
    }
}
```

- [ ] **Step 4: Replace the four inline sites in MainWindow**

```bash
git grep -n 'ntildews' -- src/Ntilde.App/MainWindow.axaml.cs
```

At each `suggestedFileName = $"{...Trim()}.ntildews.json";` use `WorkspaceBundleNaming.SuggestedFileName(name)` (or `label`). At each `Patterns = new[] { "*.ntildews.json", "*.json" }` use `Patterns = WorkspaceBundleNaming.PickerPatterns`. Replace the `GetFileNameWithoutExtension` + `EndsWith(".ntildews")` block (four lines) with `string suggestedName = WorkspaceBundleNaming.SuggestedWorkspaceName(bundlePath);`. Then the grep above must return only the comment on the `#171` line, which you reword to mention both suffixes.

- [ ] **Step 5: Build and run the tests**

```powershell
scripts/build.ps1 build src/Ntilde.App -c Debug 2>&1 | Select-String -Pattern 'error|Build succeeded' | Select-Object -First 5
scripts/build.ps1 test tests/Ntilde.App.Tests -c Debug --filter "FullyQualifiedName~WorkspaceBundleNamingTests|FullyQualifiedName~WorkspaceManagerTests" --blame-hang-timeout 5m 2>&1 | Select-String -Pattern 'Passed!|Failed!|Failed ' | Select-Object -First 10
```

Expected: `Build succeeded`, `Passed!`.

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.App/Shell/WorkspaceBundleNaming.cs src/Ntilde.App/MainWindow.axaml.cs tests/Ntilde.App.Tests/Core/WorkspaceBundleNamingTests.cs
git commit -q -F - <<'EOF'
rebrand: workspace bundles use .ntildews.json and still open .novaws.json

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 10: Packaging identity that the sweep could not decide

**Files:**
- Modify: `packaging/linux/build-deb.sh` (control heredoc, around line 379)
- Modify: `packaging/linux/test-build-deb.sh:179`
- Modify: `packaging/arch/build-arch.sh` (PKGBUILD heredoc, lines 359-360)
- Modify: `packaging/arch/test-build-arch.sh:132-133`
- Delete: `packaging/winget/0.3.0/`
- Create: `packaging/winget/template/benyblack.ntilde.yaml`, `benyblack.ntilde.installer.yaml`, `benyblack.ntilde.locale.en-US.yaml`
- Modify: `packaging/winget/README.md`, `packaging/winget/submit-first-time.ps1`
- Modify: `.github/workflows/release.yml` (two Velopack pack-ID comment blocks)

- [ ] **Step 1: Debian: declare the old package**

In the `cat > "$stage/DEBIAN/control" <<EOF` block, add after the `Depends: $depends` line:

```
Replaces: novaterminal
Conflicts: novaterminal
```

In `test-build-deb.sh` after line 179 (`grep -q '^Package: ntilde$' ...`), add:

```bash
    grep -q '^Replaces: novaterminal$'  <<<"$info" || fail "missing Replaces: novaterminal"
    grep -q '^Conflicts: novaterminal$' <<<"$info" || fail "missing Conflicts: novaterminal"
```

- [ ] **Step 2: Arch: declare the old package**

In `build-arch.sh` replace the two PKGBUILD lines:

```bash
provides=('ntilde')
conflicts=('ntilde' 'novaterminal' 'novaterminal-bin')
replaces=('novaterminal-bin')
```

In `test-build-arch.sh` replace lines 132-133:

```bash
  grep -q "^provides=('ntilde')$"                                    "$pkgbuild" && pass "provides"  || fail "provides"
  grep -q "^conflicts=('ntilde' 'novaterminal' 'novaterminal-bin')$" "$pkgbuild" && pass "conflicts" || fail "conflicts"
  grep -q "^replaces=('novaterminal-bin')$"                          "$pkgbuild" && pass "replaces"  || fail "replaces"
```

Run both packaging test scripts where they can run (they use bash and dpkg tooling; on Windows expect a clean early skip, on CI they run):

```bash
bash packaging/arch/test-build-arch.sh 2>&1 | tail -5
```

Expected: every `pass` line, or an explicit skip because `makepkg` is missing. Never a `fail`.

- [ ] **Step 3: winget template**

```bash
git rm -r -q packaging/winget/0.3.0
mkdir -p packaging/winget/template
```

`packaging/winget/template/benyblack.ntilde.yaml`:

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
# __VERSION__ is a placeholder; render with the recipe in ../README.md before validating.
PackageIdentifier: benyblack.ntilde
PackageVersion: __VERSION__
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
```

`packaging/winget/template/benyblack.ntilde.installer.yaml`:

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: benyblack.ntilde
PackageVersion: __VERSION__

# Ntilde releases ship as self-contained AOT bundles zipped per platform
# (see .github/workflows/release.yml). There is no signed installer, so the
# Windows package is delivered as a portable app extracted from the release zip.
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: Ntilde.exe
    PortableCommandAlias: ntilde

# Portable packages install per-user and need no elevation.
UpgradeBehavior: uninstallPrevious

Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/benyblack/ntilde/releases/download/v__VERSION__/ntilde-win-x64-v__VERSION__.zip
    InstallerSha256: __SHA256__

ManifestType: installer
ManifestVersion: 1.6.0
```

`packaging/winget/template/benyblack.ntilde.locale.en-US.yaml`: copy the deleted `benyblack.NovaTerminal.locale.en-US.yaml` from git history (`git show HEAD~1:packaging/winget/0.3.0/benyblack.NovaTerminal.locale.en-US.yaml`, already swept to Ntilde names by Task 4 before deletion), set `PackageVersion: __VERSION__`, `PackageName: Ntilde`, `Moniker: ntilde`, and keep the rest.

- [ ] **Step 4: winget README and submit script**

In `packaging/winget/README.md` replace the `## Layout` code block with:

```
packaging/winget/template/
  benyblack.ntilde.yaml               # version manifest   (__VERSION__ placeholder)
  benyblack.ntilde.installer.yaml     # installer          (__VERSION__, __SHA256__)
  benyblack.ntilde.locale.en-US.yaml  # default-locale metadata
packaging/winget/<version>/            # rendered set, created by the recipe below, committed per release
```

In `## Cutting a manifest for a new release`, replace the "copy the previous version's files" step with:

```powershell
# 2. Render the template into a versioned folder.
New-Item -ItemType Directory -Force -Path packaging\winget\$ver | Out-Null
Get-ChildItem packaging\winget\template\*.yaml | ForEach-Object {
    (Get-Content $_ -Raw).Replace('__VERSION__', $ver).Replace('__SHA256__', $sha) |
        Set-Content -NoNewline (Join-Path packaging\winget\$ver $_.Name)
}
```

Add one paragraph under the intro: `benyblack.NovaTerminal` remains in winget-pkgs as an orphan; winget cannot rename identifiers, so `benyblack.ntilde` is a first-time submission (`submit-first-time.ps1`) on the first Ntilde release.

In `submit-first-time.ps1` change `[string]$Version = "0.3.0",` to `[Parameter(Mandatory)][string]$Version,` and update the `.PARAMETER Version` text to "The rendered manifest folder to submit (see README.md, 'Cutting a manifest')." and the `.EXAMPLE` lines to pass `-Version 1.0.0`.

- [ ] **Step 5: Velopack comments in release.yml**

Find both blocks:

```bash
grep -n 'packId is deliberately NOT' .github/workflows/release.yml
grep -n 'packId matches the Windows lane' .github/workflows/release.yml
```

Rewrite the Windows block (keep the same indentation and line count roughly) to:

```
          # --packId is deliberately NOT "ntilde" (the data folder name), and this must not be
          # "tidied" to match. Velopack installs to %LocalAppData%\<packId> and its uninstall
          # routine deletes that directory. Settings, themes, sessions and logs live in
          # %LocalAppData%\ntilde, and Windows paths are case-insensitive, so a packId of
          # "ntilde" or "Ntilde" makes the install root and the data root the same directory:
          # uninstall would wipe user data. "NtildeApp" keeps them apart.
          #
          # Only the nupkg file names and the install directory derive from packId; --packTitle
          # is what users see. Changing packId AFTER a stable release ships orphans every
          # installed client (a different packId is a different app to the updater), so the
          # NovaTerminalApp -> NtildeApp change at the rebrand was the one allowed break and
          # is documented in README "Coming from NovaTerminal".
```

Rewrite the macOS block's explanatory sentence to reference `NtildeApp`, `Ntilde.app`, and the same case-insensitivity note. Read the surrounding lines first and preserve every non-comment line exactly.

- [ ] **Step 6: Confirm the rest of the lane is consistent**

```bash
grep -nE 'packId|packTitle|mainExe|bundleId|--icon' .github/workflows/release.yml .github/workflows/ci.yml | grep -vE 'NtildeApp|packTitle Ntilde|Ntilde\.exe|com\.benyblack\.Ntilde|ntilde_icon|^\S+:\s*#' 
grep -n 'DEFAULT_BASE' site/astro.config.mjs
grep -n 'PortableCommandAlias\|wingetcreate' .github/workflows/release.yml | head -5
```

Expected: first grep prints nothing (every flag already carries the new values); `DEFAULT_BASE = '/ntilde'`; the winget job references `benyblack.ntilde`.

- [ ] **Step 7: Commit**

```bash
git add -A packaging .github/workflows/release.yml
git commit -q -F - <<'EOF'
rebrand(packaging): deb/AUR supersede the old package, winget becomes a template

- deb: Replaces/Conflicts novaterminal so apt upgrades in place
- AUR: ntilde-bin provides/conflicts/replaces the novaterminal names
- winget: 0.3.0 manifests removed; template with __VERSION__/__SHA256__
  rendered at the first Ntilde release (a fresh identifier, winget
  cannot rename benyblack.NovaTerminal)
- Velopack pack-ID comments rewritten for NtildeApp vs the ntilde folder

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 11: Docs, release notes, and prose fix-ups

**Files:**
- Modify: `README.md` (Install section)
- Create: `docs/announcements/2026-09-15-ntilde-rebrand.md`
- Modify: `packaging/linux/ntilde.1` (`.TH` line)
- Modify: `assets/command-knowledge/command-catalogue.json` (two `"o": "nova"` entries), `scripts/generate-command-catalogue.ps1:523`, `src/Ntilde.CommandAssist/Models/CommandKnowledgeCatalogue.cs:45`
- Modify: any file the prose grep in Step 4 names

- [ ] **Step 1: README "Coming from NovaTerminal"**

Insert directly after the `## Install` heading's first paragraph (before `**Windows**`):

```markdown
### Coming from NovaTerminal

Ntilde is NovaTerminal renamed; nothing else changed in this release. What that means for an
existing install:

- **Settings carry over.** On first launch Ntilde copies settings, themes, connection profiles,
  workspaces, snippets, backups and history from `NovaTerminal` into its own data folder
  (`%LOCALAPPDATA%\ntilde` on Windows, `~/.local/share/ntilde` elsewhere). The old folder is
  left untouched; delete it when you are happy.
- **Saved passwords do not.** Keychain, Credential Manager and Secret Service entries are stored
  under the new name. Re-enter SSH passwords once; the profiles themselves are already there.
- **The old app will not update itself into Ntilde.** Install Ntilde from the links below, then
  uninstall NovaTerminal. Debian and Arch packages supersede `novaterminal` automatically.
- **Package names changed:** `winget install benyblack.ntilde`, `brew install --cask
  benyblack/tap/ntilde`, `ntilde-bin` on the AUR, `.deb` package `ntilde`. The command is now
  `ntilde` (was `nova`).
- **Old files still open.** `.novabackup` bundles, `.novaws.json` workspace exports and `.rec`
  recordings from NovaTerminal import and replay unchanged.
- **Remote shell integration:** re-run the installer from Settings on each host; it writes
  `~/.ntilde-shell-integration.sh`. Remove the old `~/.nova-shell-integration.sh` loader line
  from your rc file by hand.
- **Environment overrides** are renamed `NOVATERM_*` to `NTILDE_*` (for example
  `NTILDE_APPDATA_ROOT`).
```

- [ ] **Step 2: Announcement draft**

`docs/announcements/2026-09-15-ntilde-rebrand.md`:

```markdown
<!--
DRAFT - publish with the first release tagged after the rebrand merges.
Before publishing: fill in the tag, confirm the winget first-time submission
(packaging/winget/README.md) and the Homebrew cask (packaging/homebrew/README.md)
are live, or drop those install lines.
-->

# NovaTerminal is now Ntilde

Same terminal, new name. This release renames the product, the binaries, the packages and
the command (`ntilde`, was `nova`). Your settings move over automatically on first launch;
saved passwords need re-entering once. Full details and every install path are in the README
section "Coming from NovaTerminal".

Why "Ntilde"? The tilde is the shell's home, and the name is short enough to type.
```

- [ ] **Step 3: Small hand edits**

- `packaging/linux/ntilde.1` line 1: `.TH NTILDE 1 "2026-09-15" "Ntilde" "User Commands"` (the sweep left the date; refresh it).
- `assets/command-knowledge/command-catalogue.json`: the two `"o": "nova"` values become `"o": "ntilde"`. Update the matching mention in `CommandKnowledgeCatalogue.cs:45` (`<c>"ntilde"</c>`) and the attribution string in `generate-command-catalogue.ps1:523` (`Entries marked "o": "ntilde" were authored for Ntilde`). Confirm nothing compares the tag at runtime:

```bash
git grep -n '"o"' -- 'src/**/*.cs' | grep -v '///'
```

Expected: nothing.

- [ ] **Step 4: Prose sanity grep**

```bash
git grep -nE 'Ntilde Terminal|Ntilde terminal|ntilde terminal|ntildeterm|Ntildeterm|NtildeTerm|an Ntilde|a ntilde|the the' -- . ':!docs/superpowers/plans' ':!docs/superpowers/specs'
```

For each hit, fix the sentence by hand ("an Ntilde" is correct English, so leave those; fix the rest). Common ones: `ntildeterm_` temp prefixes are fine to leave, but `Ntilde Terminal` must become `Ntilde`.

- [ ] **Step 5: Commit**

```bash
git add -A README.md docs/announcements packaging/linux/ntilde.1 assets/command-knowledge scripts/generate-command-catalogue.ps1 src/Ntilde.CommandAssist
git add -A
git commit -q -F - <<'EOF'
rebrand(docs): migration notes, announcement draft, prose fix-ups

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 12: Merge main after the Homebrew tap PR lands, re-sweep, fix the cask

**Files:**
- Merge from main: `packaging/homebrew/Casks/novaterminal.rb` (becomes `ntilde.rb`), `packaging/homebrew/README.md`, `packaging/homebrew/bootstrap-tap.sh`, the `Update Homebrew tap` step in `release.yml`, three README lines.

**Blocked until:** PR #453 is merged. Check with `gh pr view 453 --json state`. If it is still open when every other task is done, skip to Task 13 and return here before opening the PR; note the skip in the PR description if you must open without it.

- [ ] **Step 1: Merge main**

```bash
git fetch origin main
git merge origin/main
```

Expected conflicts in `.github/workflows/release.yml` and `README.md` (the sweep rewrote surrounding lines). Resolve by taking BOTH sides: keep every swept line from ours and add the new Homebrew step and README lines from theirs verbatim (still spelled NovaTerminal). Finish the merge commit.

- [ ] **Step 2: Re-run the sweep over the newly arrived files**

```bash
python "C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\6baad9ce-5765-4899-be0e-32951a426793\scratchpad\sweep.py" paths
python "C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\6baad9ce-5765-4899-be0e-32951a426793\scratchpad\sweep.py" text
python "C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\6baad9ce-5765-4899-be0e-32951a426793\scratchpad\sweep.py" audit
```

Expected: `renamed 1 paths` (the cask), a handful of rewritten files, `0 unexpected hits`. Any other file main added since `c024293` is swept in the same pass.

- [ ] **Step 3: Hand-check the cask**

`packaging/homebrew/Casks/ntilde.rb` must now read `cask "ntilde" do`, `url ".../benyblack/ntilde/releases/download/v#{version}/ntilde-osx-arm64-v#{version}.zip"`, `name "Ntilde"`, `homepage "https://github.com/benyblack/ntilde"`, and `app "Ntilde.app"`. Fix any line the rules missed. Validate syntax if Ruby is available, otherwise rely on the release lane's `ruby -c`:

```bash
sed -e 's/__VERSION__/1.0.0/' -e 's/__SHA256__/0000000000000000000000000000000000000000000000000000000000000000/' packaging/homebrew/Casks/ntilde.rb > "$TEMP/ntilde.rb" && (ruby -c "$TEMP/ntilde.rb" || echo "ruby not available locally; CI validates")
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -q -F - <<'EOF'
rebrand: sweep the Homebrew tap lane merged from main

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 13: Full verification and PR

**Files:** none new.

- [ ] **Step 1: Clean rebuild of the whole solution**

```powershell
scripts/build.ps1 build Ntilde.sln -c Debug 2>&1 | Select-String -Pattern ' error |Build succeeded|Build FAILED|warning MSB' | Select-Object -First 20
```

Expected: `Build succeeded`, no `MSB` warnings about missing files.

- [ ] **Step 2: Gating test projects**

```powershell
foreach ($p in 'Architecture','McpServer','Core','VT','Platform','Rendering') {
  "== $p"; scripts/build.ps1 test "tests/Ntilde.$p.Tests" -c Debug --no-build 2>&1 | Select-String -Pattern 'Passed!|Failed!' | Select-Object -First 3
}
```

Expected: six `Passed!` lines. Skia-dependent Rendering tests skip on Linux only; on Windows they run.

- [ ] **Step 3: App.Tests, full run, logged**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests -c Debug --no-build --blame-hang-timeout 5m 2>&1 | Tee-Object -FilePath "$env:TEMP\ntilde-apptests.log" | Select-String -Pattern 'Passed!|Failed!|Failed |Total tests' | Select-Object -First 30
```

Expected: `Passed!`. Known polluter `PaneAgentStatusBarTests` (#357) and the Windows PTY backspace flake are the only acceptable reds; re-run once, do not debug.

- [ ] **Step 4: Final audit for the PR description**

```bash
python "C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\6baad9ce-5765-4899-be0e-32951a426793\scratchpad\sweep.py" audit
git grep -c -i nova | awk -F: '{s+=$2} END {print s " remaining case-insensitive hits, all allowlisted"}'
git grep -il nova | sed 's|/[^/]*$||' | sort | uniq -c | sort -rn
```

Expected: `0 unexpected hits`; the per-directory list is only the SSH native interop files, the docker e2e tests, the `.rec`/`.snap` fixtures, the Rust crate, `renovate.json`, and docs quoting `nova2` paths.

- [ ] **Step 5: Push and open the PR**

Confirm HEAD is still `rebrand/ntilde` in the worktree (other sessions switch the main checkout, not worktrees, but check):

```bash
git rev-parse --abbrev-ref HEAD
git push -u origin rebrand/ntilde
```

PR title: `Rebrand NovaTerminal to Ntilde`. Body: link the spec and plan; the decisions table; the audit output from Step 4 in a fenced block; the owner steps from the spec §4 as a checklist (repo rename, tap cleanup, `ASTRO_BASE`, local `~/.claude.json`, winget first-time submission); a note that commit 2 (paths) and commit 3 (text) are reproducible from the sweep script described in Task 2. End with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

```bash
gh pr create --base main --head rebrand/ntilde --title "Rebrand NovaTerminal to Ntilde" --body-file "$TEMP/pr-body.md"
```

- [ ] **Step 6: Watch CI**

```bash
gh pr checks --watch
```

Known flakes (memory): Unit Tests host-hang after all pass, cargo dep fetch on windows-latest, PTY backspace on windows. One re-run each. Any other red is real: read the log, fix on the branch, push.

---

## Self-review

**Spec coverage.** §1 mapping: Tasks 2 to 4 (rules cover every row; `nova_icon`, sidecar, `NovaTerminalApp`, env vars, MCP prefix, shell integration all fall out of the rules and are exercised in the Task 2 dry-run). §2 data folder: Task 6; env var: Task 6 (rename by sweep, no fallback); extensions: Tasks 7, 8, 9; secrets: sweep (fresh start, no code); updater: Task 10 comments and Task 11 README. §3 release assets and Velopack: sweep rule plus Task 10 Step 6 check; Homebrew: Task 12; winget: Task 10; Debian: Task 10; Pages base: sweep rule, checked in Task 10 Step 6; MCP tool names: sweep plus Task 5; dev tooling: sweep. §4 owner steps: PR body in Task 13; commit sequence: Tasks 3, 4, 6 to 11; verification: Tasks 5 and 13. Amendments 1 to 7: Task 1 records them; Tasks 2 (fixture exclusion, asset prefix), 10 (arch, winget), 8 (novarec), 12 (Homebrew), 3/4 (two commits) implement them.

**Placeholders.** None; every step carries its code or exact command. The winget `__VERSION__`/`__SHA256__` strings are deliberate rendered-at-release placeholders, matching the existing cask convention.

**Type consistency.** `AppPaths.MigrateLegacyRoot(string, string) : bool`, `AppPaths.MigrationMarkerFileName`, `AppPaths.LegacyRootDirectory` used identically in Task 6 tests and implementation. `BackupService.LegacyBundleExtension` in Task 7 test and code. `ReplayHeader.TypeToken`, `LegacyTypeToken`, `IsKnownType(string?)` in Task 8 test, recorder, runner. `WorkspaceBundleNaming.{Extension, LegacyExtension, PickerPatterns, SuggestedFileName, SuggestedWorkspaceName}` in Task 9 test and MainWindow. Namespaces post-sweep: `Ntilde.Shell`, `Ntilde.Tests.Core`, `Ntilde.Tests.Backup`, `Ntilde.Tests.ReplayTests`.
