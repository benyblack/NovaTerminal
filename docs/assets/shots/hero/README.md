# Hero shots — real-window capture

Everything else under `docs/assets/shots/` is rendered headlessly by
`tools/NovaTerminal.Shots` (`scripts/shots.ps1`). The two images in this folder are
different on purpose: they capture the **real on-screen NovaTerminal window**, including
the OS drop shadow, rounded corners, and acrylic blur that a headless
`RenderTargetBitmap` cannot produce.

`scripts/capture-hero.ps1` does the capture, but it does **not** drive the app — no
`SendKeys`, no focus stealing, no window positioning. You arrange the window by hand;
the script only reads `DwmGetWindowAttribute` for the true frame bounds and grabs that
rectangle off the screen. This is the design, not a shortcut: foreground automation on
Windows has proven unreliable enough on this project that arrangement is deliberately
left to a human every time.

Follow this document top to bottom the first time. If you're re-capturing a shot you've
done before, skip to "Per-shot arrangement" and "Running the capture".

## Before you do anything: the leak check

**No hero image may contain a real username, hostname, path, or branch name.** This is
the same leak class that got two other scenarios deferred earlier in this project — a
real `C:\Users\yourname\...` path, a real machine name, or your actual git identity
showing up in a marketing screenshot. The headless harness prevents this with an
isolated `DemoWorld` (fake `$HOME`, fake prompt, fake workspace); a real window has none
of that, so **you** are the leak check. Do it twice:

1. **Before you press capture** — look at the arranged window yourself and confirm:
   - The prompt reads `nova@demo ~/projects/nova-demo (feat/sixel-decoder) $` — not your
     real user, host, or working directory.
   - The tab title reads `nova-demo`, not a real path or executable name.
   - The title bar (if your build shows one) has no path in it.
   - Nothing your real shell/git identity would print is on screen — check any git
     output (`git status`, `git log`) shows only the fabricated commits from the setup
     below, not your own repos.
   - No other window (taskbar tooltip, notification toast, another app) overlaps the
     40px padding this script adds around the window frame — `DwmGetWindowAttribute`
     only bounds the NovaTerminal window itself; anything else on screen in that padded
     rectangle gets captured too.
2. **After the PNG is saved** — open it and look again, at full size, before staging it
   for commit. A prompt or path that's too small to notice at a glance is still a leak
   once someone zooms into the published image.

If you're not sure a given piece of on-screen text is safe, treat it as unsafe and fix
the arrangement before capturing.

## One-time setup: the demo workspace and profile

The headless harness's `DemoWorld` builds an isolated fake machine per run and deletes
it afterwards, so it can't be pointed at directly. Build an equivalent by hand, once,
using the same fabricated assets the headless scenarios use (already reviewed for
leaks — nothing in them is real):

```bash
# 1. A scratch workspace outside any real repo.
mkdir -p /tmp/nova-hero-demo/scripts /tmp/nova-hero-demo/src
cd /tmp/nova-hero-demo

# 2. Copy the same fabricated content the headless hero scenarios use.
cp /path/to/nova2/tools/NovaTerminal.Shots/Assets/nova-banner.sh scripts/
cp /path/to/nova2/tools/NovaTerminal.Shots/Assets/demo-test.sh   scripts/
cp /path/to/nova2/tools/NovaTerminal.Shots/Assets/demo-top.sh    scripts/
cp /path/to/nova2/tools/NovaTerminal.Shots/Assets/sixel-decoder.rs src/

# 3. A small, fake git history on a branch literally named feat/sixel-decoder —
#    the branch name is what `git status` and `git log` will actually print, so it
#    has to be real, even though nothing else about the repo does.
git init --initial-branch=feat/sixel-decoder
git config core.autocrlf false
git config user.name nova
git config user.email nova@demo
git add .
git commit -m "feat(vt): add sixel decoder skeleton"
echo '// TODO: raster attributes' >> src/sixel-decoder.rs
git add .
git commit -m "feat(vt): parse sixel raster attributes"

# 4. A second branch merged back in, so `git log --graph --oneline --all --shortstat`
#    (hero-real-split's top-right pane) has an actual shape to draw instead of two
#    commits in a straight column — mirrors what the headless DemoWorld seeds.
git checkout -b feat/sixel-palette
printf '//! Colour registers for the sixel decoder.\n\npub struct Palette;\n' > src/sixel-palette.rs
git add .
git commit -m "feat(vt): add palette register table"
printf '\nimpl Palette {\n    pub const LEN: usize = 256;\n}\n' >> src/sixel-palette.rs
git add .
git commit -m "feat(vt): support 256-color sixel palette"
git checkout feat/sixel-decoder
echo '# nova-demo' > README.md
git add .
git commit -m "docs: describe the decoder pipeline"
git merge --no-ff -m "Merge branch 'feat/sixel-palette' into feat/sixel-decoder" feat/sixel-palette
```

(`git log --graph --oneline --all` and `git status --short --branch` never print your
git identity or machine — only the branch name and commit subjects above — but keep
`user.name`/`user.email` fake anyway in case a future command does.)

Now add a NovaTerminal profile pointed at that workspace:

1. Open Settings → Profiles → **+ Add**.
2. **Name:** `Hero Demo`.
3. **Command:** your Git for Windows `bash.exe` (typically
   `C:\Program Files\Git\bin\bash.exe` — check `where git` and look next to it if yours
   differs).
4. **Arguments:** `--noprofile --norc -i` (suppresses every rc file, so nothing on this
   machine can silently override the prompt you set by hand in "Per-shot arrangement"
   below — this is exactly what `DemoWorld` does for the headless scenarios, for the
   same reason).
5. **Starting directory:** the scratch workspace from above (e.g. `/tmp/nova-hero-demo`,
   or its Windows path if you're not in Git Bash already).
6. Save.

Then, in Settings → Appearance:

- **Theme:** select **GitHub Dark** from the dropdown at the top of the THEME section.
  (Not Dracula — the seeded demo world used to default to Dracula, but that theme file's
  `Foreground` is misconfigured; GitHub Dark is the pinned identity for every demo
  surface in this project.)
- **Window → Blur effect:** **Acrylic**. Real acrylic blur is exactly the kind of window
  chrome a headless render cannot produce — leaving it off defeats the point of doing a
  real capture at all.

## Per-shot arrangement

Open a new tab with the **Hero Demo** profile for each shot. The very first thing you
type in the pane, before anything else, sets the fabricated prompt (because the profile
uses `--norc`, nothing else will):

```bash
export PS1='\[\e]0;nova-demo\a\]\[\e[32m\]nova@demo \[\e[33m\]~/projects/nova-demo \[\e[36m\](feat/sixel-decoder)\[\e[0m\] $ '
clear
```

| Shot | Mirrors | Window size (content area) | Commands (after the prompt above) |
|---|---|---|---|
| `hero-real-single` | `hero-single` (headless) | ~1280×800 | `bash scripts/nova-banner.sh`, `git status --short --branch`, `bash scripts/demo-test.sh` |
| `hero-real-split` | `hero-split` (headless) | ~1920×900 | Left pane: `bash scripts/demo-test.sh`, `bash scripts/nova-banner.sh`, `cat src/sixel-decoder.rs \| head -n 22`. Split right (horizontal), top pane: `git log --graph --oneline --all -12 --shortstat`. Split again (vertical), bottom-right pane: `bash scripts/demo-top.sh`. |

Window size doesn't have to be pixel-exact — there's no automated arrangement to match
it against. Resize close to the target, run the capture (next section), read the
dimensions it prints, and adjust if it's noticeably off.

For every pane: let the command finish and the prompt return to rest before moving on —
a capture mid-scroll or mid-command looks broken the same way it would in the headless
harness.

## Running the capture

From the repo root, with the window arranged and nothing else about to pop up on top of
it (notifications, other windows near its edges):

```powershell
scripts/capture-hero.ps1 -Name hero-real-single
scripts/capture-hero.ps1 -Name hero-real-split
```

The script gives you 5 seconds after you press Enter to bring the NovaTerminal window
to the front / make final adjustments, then captures `DWMWA_EXTENDED_FRAME_BOUNDS` (the
real frame including shadow and rounded corners) padded by 40px on each side, and saves
`docs/assets/shots/hero/<name>.png`. It prints the final pixel size — use that to check
the window size against the table above.

If it fails with `NovaTerminal is not running`, start the app first. If it fails with
`no main window handle yet`, restore/un-minimize the window and re-run. If multiple
NovaTerminal processes are running, it warns and captures the first one it finds — close
the others first if that's not the instance you arranged.

After each capture, verify on the actual PNG (not just by eye on screen):

- The drop shadow is visible on all sides.
- The corners are rounded, not square.
- No other window intrudes into the 40px padding.
- The leak check above, again, on the saved file.

## Committing

```bash
rtk git add scripts/capture-hero.ps1 docs/assets/shots/hero
rtk git commit -m "feat(shots): add the real-window hero capture script"
```

Only commit a hero PNG you personally captured and leak-checked. Never commit a
headless render under this folder — that defeats the reason it exists — and never
commit a capture of some other window relabeled as a hero shot.

## Provenance

Record the OS and build every time a hero image in this folder is replaced, so anyone
can answer "which build produced this image":

| File | OS | Build (commit) | Captured |
|---|---|---|---|
| _(none committed yet — see the report for this task)_ | | | |
