# Command Assist M4.3 Placement And Density Plan

Date: 2026-03-11
Status: In progress

## Goal

Tune prompt-adjacent placement so the bubble and popup feel lighter in medium panes and remain stable in short panes.

## Scope

In scope:
- responsive bubble and popup target sizing in `TerminalPane`
- explicit side-floating popup fallback in `CommandAssistAnchorCalculator`
- updated compact-density thresholds for the bubble
- deterministic anchor and layout tests

Out of scope:
- controller mode changes
- VT/render/core terminal changes
- major popup view restructuring

## Intended Behavior

### Medium panes

- do not always request the maximum `420/520` widths
- scale the bubble and popup down toward the pane width so they feel attached to the prompt instead of pane-sized

### Narrow panes

- trigger compact bubble density earlier
- keep the bubble readable by hiding query text when width pressure is real, not only at the smallest breakpoint

### Short panes

- if the popup cannot fit meaningfully above or below the bubble, place it beside the bubble when horizontal room exists
- prefer a stable side-floating card over a vertically clamped card that crowds the prompt

## Verification

- `CommandAssistAnchorCalculatorTests`
- `CommandAssistLayoutTests`
- full `CommandAssist` filtered run after implementation
