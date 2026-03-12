# Command Assist M4.3 Auto-Open Policy Plan

Date: 2026-03-11
Status: In progress

## Goal

Refine popup auto-open behavior so Command Assist stays lightweight near the prompt and only expands when richer content is warranted.

## Scope

In scope:
- popup visibility policy in `CommandAssistController`
- bubble vs popup presentation mapping in `CommandAssistBarViewModel`
- deterministic controller and layout tests for the refined policy

Out of scope:
- anchor geometry changes
- VT/render/buffer changes
- new providers or new terminal shortcuts

## Policy

### Suggest

- bubble visible while typing or after paste
- popup stays closed by default
- popup opens when the user browses suggestions with assist-owned navigation

### Search

- explicit history search opens the popup immediately
- popup remains explicit rather than inferred from mode label alone

### Help

- explicit help opens the popup immediately

### Fix

- high-confidence fix results open the popup immediately
- low-confidence fix results show a collapsed bubble affordance instead of staying fully dormant
- browsing low-confidence fix suggestions opens the popup

## Implementation Notes

- remove the `mode != Suggest` fallback that currently forces popup visibility in `CommandAssistBarViewModel`
- make the controller set `IsPopupOpen` explicitly for every mode transition
- keep assist-owned key routing unchanged for now; browsing already exists and is sufficient to trigger expansion

## Verification

- `CommandAssistControllerTests`
- `CommandAssistLayoutTests`
- `TerminalPaneCommandAssistShortcutTests`
