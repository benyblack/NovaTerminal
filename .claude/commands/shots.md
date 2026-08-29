---
description: Regenerate marketing screenshots and clips, then review every image
---

Regenerate NovaTerminal's marketing assets and verify each one visually.

Scenarios requested: $ARGUMENTS (empty means `all`).

1. Build and run the harness. Never use raw `dotnet build`:

   `scripts/shots.ps1 $ARGUMENTS --scale 2`

   `all` runs every scenario `ScenarioCatalog` registers — it is not the full Tier 2 set from
   the design doc. `sixel-graphics`, `iterm2-inline-image`, `connection-manager`, and
   `remote-files` are deliberately unregistered (no image decoder to demo; the SSH profile
   store and remote-files paths need real developer state or a live SSH session this harness
   can't isolate) and produce no assets. Don't report or imply they ran.

2. Read `artifacts/shots/shots.json` to get the produced asset list.

3. **Look at every PNG you produced.** List each scenario's `Intent` with
   `scripts/shots.ps1 --list`. Two things complicate a naive "one image, one Intent" match:

   - **A scenario can produce more than one image.** `agent-session` produces both
     `agent-session` and `agent-session-journal` from a single `Intent` string. Judge each
     image against the clause of that Intent that actually describes *its own* suffix, not
     the whole string as one blob — an image named `-journal` should not be marked wrong for
     not showing what the base image's clause describes, and vice versa.
   - **Some Intents disclose engineering facts, not visual ones.** `settings-appearance`'s
     Intent names itself a two-part composite; `clip-split`'s names the leading-space
     mitigation it uses for a known product race. Judge what the image actually shows against
     the *visual* part of the clause — a disclosed implementation detail is not itself a
     defect to flag.

   For each image, judge:
   - Is the pane full of content, or mostly empty?
   - Is the overlay the scenario opened actually open?
   - Is any text clipped at an edge?
   - Did the intended theme apply?
   - Does the frame look half-drawn — a partial redraw, a missing tab strip?
   - Does anything real leak in: a real username, hostname, path, or branch name?

4. Re-run only the scenarios that failed judgement. The usual causes, in order of likelihood:
   a command captured before its output settled (extend the settle wait), a split taken before
   the previous pane filled, or a theme re-seeded after the window was constructed.

5. When every image passes, publish and report:

   `scripts/shots.ps1 all --scale 2 --publish`

   Then give a table of what changed under `docs/assets/shots/`.

Never publish an image you have not looked at.
