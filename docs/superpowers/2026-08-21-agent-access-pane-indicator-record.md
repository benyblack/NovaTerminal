# Agent-access pane indicator — execution record

**Date:** 2026-08-21 → 2026-08-27
**Feature:** per-pane indicators for agent access (PR #339)
**Design:** [`specs/2026-08-21-agent-access-pane-indicator-design.md`](specs/2026-08-21-agent-access-pane-indicator-design.md)
**Plan:** [`plans/2026-08-21-agent-access-pane-indicator.md`](plans/2026-08-21-agent-access-pane-indicator.md)

> **Provenance.** The contemporaneous ledger lived in a git-ignored scratch directory
> inside a worktree and was **destroyed** when that worktree was removed between
> sessions. Only the manual smoke-test entry survived. Everything above that line is
> **reconstructed** from the commit history and the working conversation. The commits it
> cites are real and verifiable; the narrative around them is a faithful retelling, not a
> contemporaneous log. It is committed here so the next such record cannot be lost the
> same way — which is the first lesson in it.

## Why this record exists

The interesting output of this work was not the feature. It was the **defect pattern**:
sixteen defects were found in the implementation plan's own inline code during execution,
and none of them by the author's review of that plan before dispatch. Several were found
only because each implementer was required to prove a new test could fail. The value of
writing this down is that the same traps are waiting in the next feature.

## What shipped

Three signals, each carrying exactly one meaning:

- **Window light** — visible while agent *observe* is on; brightens while a `waitForEvents`
  long poll is parked, or while a pane whose own segment is not on screen is being read.
- **Pane status-bar segment** on *actable* panes only — quiet `agent access`, blue
  `agent reading`, amber `agent typed`. The amber state is sticky: it clears when you look
  at the pane, but never sooner than 10 s after the write.
- **Tab-label marker** — keyboard glyph for writes; eye glyph for reads only under the new
  `AgentIndicatorTabRollup = "All"` setting (default `"WritesOnly"`).

There is deliberately no way to silence them: an indicator the user can switch off is not a
safety surface.

## The recurring failure mode

**Every defect in this feature failed in the same direction: silently under-reporting.**
That is the thing to review for in anything that touches these indicators. A short list of
the ones that mattered:

| Defect | How it under-reported |
|---|---|
| `waitForEvents` has no `paneId` | The design attributed a long poll to a pane; there is no pane to attribute it to. Caught before implementation. |
| Actability never reached the pane | `RefreshActability` wrote `IsAgentActable` with no event, so enabling *act* showed no bar until an agent happened to touch that pane — possibly never. |
| Read of a non-allowlisted SSH pane | The window light's read condition keyed on "act is off", but an SSH pane without `AllowAgentAccess` also carries no bar. Reads of exactly the panes the user fenced off produced no signal anywhere. |
| `exportReplay` marked nothing | It writes a pane's whole retained output to disk — strictly more disclosure than `read_screen`, which does mark. |
| Zoomed-out sibling pane | The light treated "tab is selected" as "bar is on screen"; pane zoom renders only one pane per tab. |
| **Focus ≠ visibility (P1)** | `IsActivePane` means "selected *within the app*". Minimize the window and it stays true, so the tick acknowledged a write after 10 s and cleared the marker **while the user was away** — the exact case the sticky tier exists for. |
| `list_sessions` (open) | Returns every pane's title, profile and host — including real SSH hostnames — and marks nothing. |

## Decisions the human made

1. **Layered indicator** (reachability + activity), not one or the other.
2. **Signals placed where the permission lives** — app-level light for observe, pane-level
   mark for act, because observe-reachability is uniform and would become wallpaper.
3. **Two tiers, reads quiet and writes loud** — treating "read my screen" and "pressed Enter
   in my prod shell" alike fails at the moment it matters.
4. **Reuse the existing pane `StatusBar`** rather than a new overlay.
5. **Write clears on focus with a 10 s floor** — the only rule where "the signal is gone"
   implies "you saw it".
6. **Tab rollup is a setting** (`WritesOnly` default), because the read/write question is a
   genuine judgement call.
7. **All tab attention markers survive truncation** — including the pre-existing bell and
   activity markers, chosen over the narrower fix so the tab strip stays consistent.
8. Three plan-versus-code conflicts adjudicated in favour of correctness over the plan's
   literal text. A fourth (the actability wiring) was decided by the controller without
   escalating, on the grounds it was the same pattern already ruled on three times; the
   final review independently agreed.

## Lessons worth carrying forward

- **A plan's inline code is a draft, not a specification.** Precision of format (exact line
  numbers, complete code blocks) produced false confidence: line references pointed at the
  wrong function, types that did not exist, and tests that could not fail. Verifying the
  plan's API assumptions against the real code before dispatch cost a fraction of a fix
  round and caught more.
- **Require every new test to be proven able to fail.** This caught a test that passed with
  its feature deleted entirely, and later caused an implementer to *discard* a test it had
  written after finding it vacuous.
- **Inspect the worktree diff whenever a subagent stops mid-round.** One stalled agent left
  a deliberately-broken experiment line in production code with a plausible-looking comment;
  nothing external signalled it.
- **Scope the verification, not just the edit.** A new `TerminalSettings` field must be
  mirrored into `SettingsTools` or two *gating* drift-guard tests fail. That rule was known
  and written down, and still missed — because every test command across 25 commits was an
  `App.Tests` filter, so `McpServer.Tests` never ran and no reviewer could catch it. The
  branch was reported green and opened as a PR while red.
- **A green test result in this repo is not evidence the suite passed.** The number of
  `App.Tests` tests that execute varies between runs, locally *and* in CI, while always
  printing `Passed! Failed: 0`: 2735 on one CI run, then 1495 (ubuntu) and 38 (windows) four
  commits later, the latter in 222 ms. `continue-on-error` means nothing gates on it. This
  deserves its own issue and is not specific to this branch.
- **`scripts/build.sh` has been observed exiting 0 while reporting `3 Error(s)`.** Judge
  builds by grepping the log, never the exit code.
- **Don't keep the only copy of the record in a git-ignored directory.** See Provenance.

## Manual smoke test (2026-08-27, contemporaneous)

Driven against the worktree build with the agent side over real MCP, not simulated.

- Window light present, tooltip reads "Agent access is enabled. Click to open the agent
  activity journal." **Confirmed.**
- Pane segment renders the quiet `agent access` state on actable panes. **Confirmed.**
- **Read tier** — four marking reads (three `read_screen`, one `get_session_status`) turned
  the segment blue with `agent reading`, decaying to grey ~3 s after the last. **Confirmed.**
- **Failed-write negative** — `send_input` to two panes whose shells had exited returned
  `sessionNotRunning` and marked **nothing**. **Confirmed live.**
- **Write tier** — `send_input` to a local pane in a *background* tab produced the keyboard
  glyph on that tab header (under the default `WritesOnly`) and amber `agent typed` on the
  pane bar. **Confirmed.**
- **Acknowledgement lifecycle** — clicking onto the written tab cleared both markers ~10 s
  later. Correct: selecting the tab *is* looking at the pane. (The first run was
  mis-instructed to click the tab, which invalidated it; re-run without clicking.)
- **P1 fix confirmed by hand (`408ee3b`)** — wrote to an unfocused background pane,
  minimized 15+ s, restored: **the marker was still there.** Headless Avalonia cannot raise
  platform `Activated`/`Deactivated`, so no test on this branch reaches that path. This run
  is its only evidence.
- `list_sessions` again returned every pane's title, profile and host including real SSH
  hostnames while marking nothing — the open follow-up, and worse in practice than on paper.

## Open follow-ups

- `list_sessions` discloses pane metadata and marks nothing.
- The tab marker is double-rendered in the context-menu label and the **screen-reader**
  automation label (both source the title from rendered header text).
- `MainWindow.axaml.cs` has two unguarded `async void` click handlers with the same hazard
  fixed on the pane segment in `7a3beff`.
- Tests that construct `MainWindow` mutate the process-global `NOVATERM_APPDATA_ROOT` in
  separate xUnit collections; they want one shared collection.
- `Debug.WriteLine` is the only record for swallowed exceptions in the attention plumbing —
  a no-op in Release.
- The `App.Tests` partial-run problem described above.
