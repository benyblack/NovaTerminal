# Command Assist SSH Conservative Fallback Design

Date: 2026-03-11
Status: Approved — **superseded for mark-anchored sessions** (V2 Phase 2a, 2026-08-02)

> **Scope note (2026-08-02).** Everything below describes what to do when the prompt row is a
> guess. V2 Phase 2a made it a fact wherever an `OSC 133;B` mark is live and on screen: those
> sessions anchor directly to the marked row and consult none of this — not the
> `HasReliablePromptAnchor = false` policy, not the cursor-band placement, not the short-pane
> suppression, and not the placement-correction passes that were added alongside it. The design
> remains in force, unchanged, for markless sessions, which is why none of the code it describes
> was deleted. See `docs/plans/2026-08-01-command-assist-v2-plan.md` Phase 2 tasks 1–2.
>
> The premise in *Problem* below — "SSH sessions currently set `HasReliablePromptAnchor = false`" —
> is no longer unconditionally true. The rule is now: a mark makes the anchor reliable regardless of
> session type; without one, SSH is still untrusted exactly as described here.

## Goal

Keep Command Assist visually close to the active input line in SSH heuristic sessions without claiming that the remote shell is integrated or that the prompt row is fully trustworthy.

## Problem

SSH sessions currently set `HasReliablePromptAnchor = false`, which avoids overlaps with login banners and prompt redraw noise, but it forces the bubble into a static lower safe zone. In stable remote shells this makes the UI feel detached from the prompt and input line.

## Constraints

- Keep Command Assist separate from terminal core logic.
- Do not add shell-specific integration for SSH in this slice.
- Preserve the conservative behavior that avoids anchoring directly to untrusted remote prompt metadata.
- Keep popup behavior unchanged unless required by the new fallback placement.

## Approach

Add a second fallback placement mode for unreliable prompt anchors:

- Keep `UsesPromptAnchor = false` for SSH heuristic sessions.
- Continue using the existing lower safe zone when the cursor is near the top of the pane or there is not enough vertical room.
- When the visible cursor row is in a stable lower region of the pane, place the bubble in a conservative band near that cursor row instead of at the pane bottom.
- Keep explicit clearance from the cursor row so the bubble does not sit on top of typed input.

This preserves the “do not trust SSH prompt metadata” policy while still making the UI feel prompt-adjacent once the session has settled.

## Intended Behavior

### Stable SSH prompt near the bottom

- Bubble appears above the active input area.
- Bubble remains visually near the cursor line.
- Popup positioning still follows the existing calculator logic relative to the bubble.

### SSH session near banners or early startup

- Bubble stays in the lower safe zone.
- No direct anchoring to the cursor row while the prompt is still too high in the pane.

### Non-SSH integrated shells

- No behavioral change.
- Existing prompt-adjacent anchor rules remain authoritative.

## Files

- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistAnchorCalculator.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistAnchorCalculatorTests.cs`

## Verification

- Add deterministic calculator coverage for unreliable-prompt cursor-band placement.
- Add pane-level SSH layout coverage for:
  - settled cursor near bottom => bubble near input
  - high cursor near top => lower safe-zone fallback
- Run focused `CommandAssist` tests.
- Run full `NovaTerminal.Tests` suite.
