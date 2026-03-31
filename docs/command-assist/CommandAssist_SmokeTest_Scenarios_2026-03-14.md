# Command Assist Smoke Test Scenarios

Date: 2026-03-14
Scope: Shell-first Tab behavior, explicit history mode, local path suggestions, and AOT parity.

## Preconditions

- Command Assist is enabled in settings.
- Test in both `pwsh` and `cmd`.
- Keep at least a few known history items (for `git`, `dotnet`, `cd`).
- Have at least one path containing a space (for escape behavior checks).

## Scenarios

1. Tab is shell-owned in PowerShell:
   Type `cd ~/h`, press `Tab`.
   Expected: Native shell path completion runs; Command Assist does not accept a suggestion.

2. Tab is shell-owned in CMD:
   Type `cd \Us`, press `Tab`.
   Expected: Native shell completion runs; Command Assist does not accept a suggestion.

3. Passive typing does not show history:
   With history containing `git ...` commands, type `git ` without opening assist.
   Expected: No history/snippet rows are shown.

4. Passive typing shows path suggestions:
   Type `cd ./` or `cd ~/`.
   Expected: Filesystem path rows appear.

5. Path insertion appends suffix only:
   Start with partial path, accept a path suggestion.
   Expected: Existing typed text is preserved; only missing suffix is inserted.

6. PowerShell escaping for spaces:
   With folder `My Folder`, type `cd .\M` in `pwsh`, accept path suggestion.
   Expected: Inserted text escapes space correctly (backtick escaping).

7. Explicit assist mode enables history/snippets:
   Press `Ctrl+Space`, then type `git st`.
   Expected: History/snippet rows are available and selectable.

8. History search mode is history-focused:
   Press `Ctrl+R`, search for `git`.
   Expected: History results appear in popup history mode.

9. Accept selected suggestion uses `Ctrl+Enter`:
   With a row selected, press `Ctrl+Enter`.
   Expected: Selected suggestion is inserted into command line.

10. Pin/unpin shortcut works:
    In explicit mode on history/snippet item, press `Ctrl+Shift+P`.
    Expected: Item pin state toggles and persists.

11. Alt-screen auto-hide:
    Open fullscreen TUI app (alternate screen).
    Expected: Command Assist UI hides while alternate screen is active.

12. Placement sanity:
    Trigger suggestions near top/middle/bottom of viewport.
    Expected: Assist surface anchors near prompt and avoids unnecessary overlap.

13. Paste behavior:
    Paste command text into prompt.
    Expected: No accidental suggestion acceptance; prompt text remains correct.

14. AOT parity check:
    Run same checks (1, 3, 4, 7, 9) on AOT build output.
    Expected: Same behavior as non-AOT build.

## Pass Criteria

- All scenarios behave as expected in both `pwsh` and `cmd`.
- No scenario causes unintended command replacement on `Tab`.
- No visual corruption/regression appears in AOT build.
