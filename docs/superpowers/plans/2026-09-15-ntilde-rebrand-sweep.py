#!/usr/bin/env python3
# Final version of the throwaway sweep that produced commits d6a9222 (paths) and 7316b69 (text).
# Kept beside the plan as the reproducibility record; it is not part of the build. Known leaks of
# the unscoped "nova" protection, fixed by hand afterwards: the deb Description in
# packaging/linux/build-deb.sh and the catalogue-origin test literal.
"""Throwaway NovaTerminal -> Ntilde sweep. Run from the worktree root on a clean tree.

  python sweep.py paths   # git mv every tracked path whose components contain a token
  python sweep.py text    # rewrite tracked text files in place
  python sweep.py audit   # list remaining 'nova' hits outside the allowlist; exit 1 if any

  python sweep.py text --only merged.txt   # restrict paths/text to a listed subset
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
SKIP_FILE_SUFFIXES = (
    "/NativeSsh/Dockerfile",
    "docs/superpowers/specs/2026-09-15-ntilde-rebrand-design.md",
    "docs/superpowers/plans/2026-09-15-ntilde-rebrand.md",
)   # docker e2e fixture + the rebrand's own spec/plan (both names on purpose)
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
    # macOS bundle ID keeps product casing (com.benyblack.Ntilde / ...NtildeApp);
    # must come before the winget-identifier rule below, which would otherwise
    # also match this and lowercase it.
    (r"com\.benyblack\.NovaTerminal", "com.benyblack.Ntilde"),
    # repair: fix-round-2 recase of already-swept bundle IDs
    (r"com\.benyblack\.ntilde", "com.benyblack.Ntilde"),
    (r"benyblack\.NovaTerminal", "benyblack.ntilde"),
    (r"github\.io/NovaTerminal", "github.io/ntilde"),
    (r"'/NovaTerminal'", "'/ntilde'"),
    # Release asset prefix is lowercase: NovaTerminal-win-x64-v1.zip -> ntilde-win-x64-v1.zip
    (r"NovaTerminal-(?=Setup|win-|linux-|osx-|\$|\{|<|sidecar)", "ntilde-"),
    # User-visible even inside the native crates: an env var users set, and a file
    # suffix users see on disk (partial-download scratch files). Not FFI symbols,
    # so unlike NOVA_SSH_*/nova_ssh_* they follow the rename (controller ruling R5).
    (r"NOVA_PTY_NO_PASSTHROUGH", "NTILDE_PTY_NO_PASSTHROUGH"),
    (r"novapart", "ntildepart"),
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
    r"NovaSsh|nova_ssh_|NOVA_SSH_\w*|NovaSftpTransferProgressCallback|NovaClientHandler"
    r"|\.nova-rust-inputs|nova2|[Rr]enovate|nova\$|\"nova\"|'nova'|/home/nova|nova-pass"
    r"|nova-key-pass|kbdnova|nova:nova|novaterm-native-ssh-e2e|novaterm-keys"
    r"|novaterm-entrypoint|novaterm\.conf|__NOVA_(BASHRC|ZSHRC|ZPROFILE|FISHRC)__"
    r"|\"user\":\"nova\""
    # Rust-only unit-test scratch temp-dir prefixes (created and removed within the
    # same test fn); never referenced from C# or docs, so not user-visible/cross-boundary.
    r"|nova-exclusive-|nova-rename-|nova-partial-"
    r"|/NativeSsh/Dockerfile:|\.rec:|\.snap:|useradd .*nova|Match User kbdnova"
    r"|docs/superpowers/specs/2026-09-15-ntilde-rebrand-design\.md:|docs/superpowers/plans/2026-09-15-ntilde-rebrand\.md:"
    # Coincidental substring, not a product-name reference (…HasNoValidationErrors…).
    r"|HasNoValidationErrors"
)


def tracked():
    out = subprocess.run(["git", "ls-files", "-z"], capture_output=True, check=True).stdout
    return [p.decode("utf-8") for p in out.split(b"\0") if p]


def parse_only(argv):
    """Return the --only LISTFILE wanted-set from argv, or None if not requested.

    argv is the full sys.argv (argv[0] is the script, argv[1] the subcommand). When
    argv[2:] == ["--only", listfile], the returned set holds the paths listed in that
    file (one per line, forward slashes, relative to the repo root, blank lines
    ignored). Callers that rename paths must treat the result as mutable: as each
    `git mv old new` lands, rewrite any entry equal to `old` or starting with
    `old + "/"` to the `new` prefix, or later passes will filter against stale paths.
    """
    rest = argv[2:]
    if not rest:
        return None
    if len(rest) == 2 and rest[0] == "--only":
        with open(rest[1], "r", encoding="utf-8") as f:
            return {line.strip() for line in f if line.strip()}
    sys.exit(f"unrecognized arguments: {rest}")


def filter_paths(paths, wanted):
    if wanted is None:
        return paths
    return [p for p in paths if p in wanted]


def selected_paths(argv):
    """Filter tracked() to an --only LISTFILE subset, if the command line asked for one.

    Safe for single-pass consumers (e.g. rewrite_text) where the tracked path list
    doesn't change out from under the filter mid-command. rename_paths must NOT use
    this helper across its multi-pass loop; see parse_only's docstring.
    """
    return filter_paths(tracked(), parse_only(argv))


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
    # NOTE: wanted (from --only) must stay in sync with renames across passes. Each
    # pass only renames the leading path component that changed (a directory move
    # also relocates every tracked file beneath it), so a stale wanted entry like
    # "src/NovaTerminal.App/NovaTerminal.App.csproj" would stop matching after
    # "src/NovaTerminal.App" -> "src/Ntilde.App" and drop out of later passes,
    # silently leaving "src/Ntilde.App/NovaTerminal.App.csproj" un-renamed.
    wanted = parse_only(sys.argv)
    total = 0
    while True:
        moves = {}
        for p in filter_paths(tracked(), wanted):
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
            if wanted is not None:
                wanted = {
                    (new + w[len(old):]) if (w == old or w.startswith(old + "/")) else w
                    for w in wanted
                }
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
    for p in selected_paths(sys.argv):
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
