# Command Assist M4.2 Prompt-Adjacent UX Design

Date: 2026-03-10
Status: Approved design

## Goal

Replace the current bottom-docked Command Assist panel with a prompt-adjacent floating interaction model that preserves continuous typing flow.

This change is a UX refactor, not a Command Assist domain rewrite.

## Problem

The current footer-style assist surface works functionally, but it competes with the bottom of the terminal where the user is already typing and reading output.

Observed issues:
- visual overlap with the active prompt area
- too much eye travel away from the typed command
- the panel feels like a second dashboard rather than an attached assistant
- the bottom edge of the pane becomes visually crowded

For a terminal-oriented product, this makes the assist feel heavier than it should.

## Design Objective

Optimize for continuous typing flow first.

That means:
- keep the user’s eyes close to the active command line
- avoid resizing the pane or carving out a persistent footer
- keep the assist visually light until the user explicitly asks for more
- preserve the rule that assist content never renders into terminal grid content

## Recommended UX Model

Use a prompt-adjacent floating model with two layers:

1. **Collapsed bubble**
- small floating assist strip anchored near the prompt region
- always lightweight
- shows mode label, top suggestion or status, and compact hints

2. **Expanded popup**
- opens from the bubble when needed
- shows result list, detail content, and helper states
- used for search/help/fix and deeper suggestion browsing

This replaces the bottom docked panel as the primary interaction surface.

## Interaction Model

### Suggest

Default while typing.

Behavior:
- show only the collapsed bubble by default
- open the popup only when the user browses, expands, or explicitly requests richer content

This keeps typing flow fast and visually calm.

### Search

Behavior:
- bubble plus popup
- popup shows compact ranked results
- remains close to the prompt region

### Help

Behavior:
- explicit mode
- popup opens with command docs/examples/recipes
- more detail-first than list-first

### Fix

Behavior:
- high-confidence failures may auto-open the popup
- lower-confidence failures should show a subtle prompt-adjacent affordance rather than forcing a large panel open

## Placement Strategy

Placement should be stable and predictable, not clever.

### Anchor model

Do not attach to exact shell glyph geometry.

Instead:
- derive a prompt-region hint from the visible cursor row and terminal cell metrics
- convert that into a safe pane-local anchor rectangle
- clamp placement inside the pane with padding

This keeps the assist close to the prompt without making it depend on perfect shell-line reconstruction.

### Default placement

- place the bubble slightly above the prompt region
- left-align it near the active input row
- keep it inside the pane bounds with safe margins

### Popup placement

Preferred order:
1. expand upward from the bubble
2. if insufficient room, expand downward
3. if vertical space is poor, use a side-floating popup anchored to the bubble

Never resize the terminal layout to make room.

## Degraded Layouts

The assist must remain usable in constrained panes.

### Narrow panes

- bubble keeps only mode plus top suggestion
- hide verbose hotkey text
- popup becomes single-column and narrower

### Short panes

- keep only the collapsed bubble by default
- require explicit expansion for richer content

### Unreliable anchor

If prompt-region anchoring is uncertain:
- snap to a stable lower-right or lower-left safe zone inside the pane
- prefer stability over almost-correct tracking

## Visual Behavior

### Bubble

- compact
- soft contrast
- subtle shadow/border
- minimum motion
- visually attached to the typing region, not the whole pane

### Popup

- compact floating card
- bounded size
- internal scrolling if content exceeds limits
- not a full-height sheet or footer

### Motion

- short fade/slide only
- no dramatic movement
- the UI should feel attached to the prompt, not animated independently

## Architecture Impact

This should reuse the current Command Assist domain/controller stack.

Keep:
- `CommandAssistController`
- provider interfaces and helper services
- pane-local ownership in `TerminalPane`
- alt-screen hide rules
- existing key routing model

Add:
- `CommandAssistAnchorCalculator`
- `CommandAssistBubbleView`
- `CommandAssistBubbleViewModel`
- `CommandAssistPopupView`
- `CommandAssistPopupViewModel`

Possible adapter:
- a thin pane-local presentation state object that maps controller state into bubble/popup state without moving domain logic into the view

## Responsibilities

### Controller

Still owns:
- mode
- results
- selection
- help/fix/search logic

Should not own:
- pixel placement
- popup orientation
- pane geometry decisions

### TerminalPane

Owns:
- overlay composition
- passing pane bounds / terminal metrics to placement logic
- alt-screen visibility

### Anchor calculator

Owns:
- prompt-region estimate
- bubble rectangle
- popup rectangle
- fallback placement rules

## Boundaries

These remain unchanged:
- VT parser
- renderer
- terminal buffer/grid behavior
- PTY transport
- shell integration contracts

The refactor must remain App/Avalonia-side.

## Risks

### Technical

- overfitting placement to exact cursor geometry and creating instability
- popup positioning bugs in narrow or tiny panes
- regressions in keyboard ownership between collapsed and expanded states

### UX

- popup appearing too often and still feeling intrusive
- bubble becoming too dense with hotkey text
- inconsistent placement if fallback rules are not strict

### Mitigations

- use prompt-region estimates, not exact shell reconstruction
- design stable fallback positions
- keep the bubble minimal
- open the popup only when needed
- test narrow panes, short panes, and alt-screen transitions explicitly

## Rollout Recommendation

Implement in two phases:

### M4.2

- replace the footer panel with prompt-adjacent bubble + popup
- preserve existing controller and provider behavior
- keep behavior changes minimal outside visual presentation and placement

### M4.3

- placement and density tuning
- refine auto-open policy for popup
- narrow/short pane polish

## Definition Of Done

M4.2 is complete when:
- the bottom-docked assist panel is no longer the default interaction surface
- a prompt-adjacent bubble appears near the active input region
- richer content opens in a floating popup rather than a footer
- no pane layout resizing occurs for assist UI
- alt-screen still hides assist immediately
- all content remains outside the terminal grid
