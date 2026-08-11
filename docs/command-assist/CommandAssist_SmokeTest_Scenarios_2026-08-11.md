# Command Assist Smoke Test Scenarios (V2)

Date: 2026-08-11
Supersedes: `CommandAssist_SmokeTest_Scenarios_2026-03-14.md` (V1).

Two entries in that document are not merely stale, they are wrong in a way that makes a
working build look broken: its scenario 3 asserts that typing shows nothing, which V2
deliberately inverted in Phase 3b (#293), and its scenario 10 names `Ctrl+Shift+P` for pin,
which is now unconditionally the command palette (pin moved to `Ctrl+Shift+S`). It also has
14 scenarios, not the 8 the phased plan refers to.

Scope: the V2 rebuild, PRs #276-#308. This is re-enable criterion 6, and running it is also
the only route to criteria 1 and 2 and to the untested half of criterion 4.

## How to run this

Two passes are required and their results are not interchangeable.

- **Pass A - Windows, local `pwsh` 7**, shell integration on. The instrumented case.
- **Pass B - Unix over SSH**, bash or zsh on a real remote host, with the remote integration
  installed per scenario 10. Run scenarios 1-8 again here and note any difference.

Scenario 9 (degraded) runs on `cmd.exe`, Pass A only.

Why both: today's evidence for "grid truth works on all four shells" is a real-PTY harness
test for bash, zsh and fish that no human has watched, plus a human who has only ever watched
pwsh - which is the one shell with no harness test. Pass B is what closes that gap.

Record each scenario PASS / FAIL / N-A with one line of what you saw, and file the result as
`docs/command-assist/SmokeResults_<YYYY-MM-DD>.md`.

## Preconditions

- Build via `scripts/build.ps1`. Launch with `NOVATERM_APPDATA_ROOT` pointed at a scratch
  directory. **Do not run this against your real history.**
- Settings -> Terminal -> Command assistant: master toggle ON, "Suggestion bubble while
  typing" ON, "Remember commands" ON, "Shell integration" ON.
- Seed history by running, in one directory and in this order: `git status`,
  `git log --oneline`, `echo git-alpha`, `dotnet --list-sdks`, `cd ..`, `cd -`. Then run one
  command from a *different* directory, so the same-directory bonus has something to beat.
- Keep the terminal log somewhere you can grep it. Scenario 8 needs it.
- Have one directory whose name contains a space.
- **Resize the window once after the first prompt appears, before starting.** That is the
  #298 reflow case, and it is the failure people actually hit.

## Vocabulary

- **Bubble** - the one-line strip just above the prompt.
- **Popup** - the multi-row list. Never opens on its own except for a high-confidence Fix.
- **Integration indicator** - a filled dot (integrated) or hollow dot (basic) at the right end
  of the bubble, and the word `integrated` / `basic` in the popup footer.

---

## 1. Tab stays the shell's, in every shell

**Precondition:** at a prompt.

**Steps:** type `cd ~/D` (Pass A) or `cd /u` (Pass B) and press `Tab`. Repeat in a `cmd.exe`
pane with `cd \Us`.

**Pass:** the shell's own completion fires and rewrites the line. No assist row is inserted
and the bubble accepts nothing. Press `Tab` again - it cycles the shell's completions, not an
assist list.

`Tab` is permanently shell-owned and is deliberately not a rebind target.

---

## 2. Two characters, one row, and Ctrl+Enter takes it

**Precondition:** history seeded, prompt empty, no popup open.

**Steps:**

1. Type `g`. Wait a second.
2. Type `i` (the line is now `gi`). Wait a second.
3. Press `Ctrl+Enter`.

**Pass:**

- After step 1, **nothing appears** - the two-character floor.
- After step 2 the **bubble** appears within about a blink, showing one suggestion
  (`git status` or `git log --oneline`), with the tail of the suggestion visually distinct
  from what you typed. No popup opens.
- After step 3 the rest of the command is **appended**: the line reads the whole command, the
  cursor is at the end, and **nothing has run**.
- The integration indicator is the **filled** dot.

**Also check, Pass A only - PSReadLine predictions.** Run
`Set-PSReadLineOption -PredictionSource History -PredictionViewStyle InlineView`, type
`dotne`, and confirm the shell paints a dim grey suffix past the cursor. `Ctrl+Enter` must
still insert, and the bubble must rank on `dotne` - what you typed - not on the prediction.
This is the #301 case; before that PR it refused on every prompt.

---

## 3. Escape is staged, and it stays gone for the line

**Precondition:** scenario 2 passed. Prompt empty.

**Steps:**

1. Type `gi`. The bubble appears.
2. Press `Down`. The popup opens.
3. Press `Esc` once.
4. Press `Esc` again.
5. Type another character.
6. Submit the line (or `Ctrl+C`), then type `gi` again at the next prompt.

**Pass:** step 3 closes the **popup only** - the bubble is still there with its suggestion.
Step 4 removes the bubble. Step 5 brings **nothing** back. Step 6 brings the bubble back.

`Ctrl+Space` and `Ctrl+R` must still open after step 4: suppression applies only to surfaces
you did not ask for.

---

## 4. Ctrl+R filters as you type, and accepting replaces what you typed

**Precondition:** history seeded, including `echo git-alpha`. Prompt empty.

**Steps:**

1. Press `Ctrl+R`.
2. Type `git`.
3. Press `Backspace` twice.
4. Type `it` again, then `zzzz`.
5. Backspace the `zzzz` away, leaving `git`.
6. Arrow to the **`echo git-alpha`** row - a row that does *not* start with `git`.
7. Press `Enter`.

**Pass:**

- Step 1: the popup opens on the recency list, most-recent first, with a relative time
  ("2m ago") in the footer for the selected row. The command you ran most **recently** is at
  or near the top - not the one you ran most **often**.
- Step 2: the popup **stays open** and the rows narrow. What you type is visible on the shell
  line behind the popup. The matched substring is **highlighted** inside each row.
- Step 3: the list re-widens.
- Step 4: at `gitzzzz` the popup is still open, showing `No matching commands in history.`
- Step 6: `echo git-alpha` is selectable even though it is not a prefix of `git`, because
  matching is by subsequence.
- Step 7: the line becomes exactly `echo git-alpha`. **Your typed `git` is erased**, not
  prefixed, and **nothing runs**. No stray characters: not `gitecho git-alpha`, and in
  particular not `gecho git-alpha`.

This is #304 plus #307. Step 7 is the only place in the product where Command Assist deletes
characters you typed. The `gecho` shape specifically would mean the backspace count came out
one short - report it rather than shrugging it off.

---

## 5. The additive boundary holds everywhere except Ctrl+R

**Precondition:** history contains `echo git-alpha`. Prompt empty.

**Steps:**

1. Type `git` - no `Ctrl+R`. The bubble appears.
2. Press `Down` to open the popup and arrow to a row that does **not** start with `git`, for
   example `echo git-alpha`. If no such row is offered, note that and skip to step 4.
3. Press `Enter`.
4. Separately: type `git` and press `Ctrl+Enter` on a bubble whose suggestion does extend
   `git`.

**Pass:** in step 3, either the row is not offered at all, or `Enter` **is not consumed** - it
falls through and the shell submits `git`, printing git's usage. What must **never** happen is
the line being erased and replaced. In step 4, only the missing suffix is appended.

Suggest mode and the passive bubble are strictly additive; only Search replaces. A failure
here means the fzf semantics leaked out of their fence, which is more serious than any
cosmetic finding in this document.

---

## 6. The popup is one line per row, dims failures, and fits its content

**Precondition:** produce one failing command first - run `git stauts` - then seed at least
four more successful commands.

**Steps:**

1. `Ctrl+R`, and look at the list without typing.
2. Note how many rows fit and how tall the popup is.
3. Type a query that narrows the list to exactly one row.
4. Resize the pane narrow, about a third of the window, and repeat step 1.

**Pass:**

- Every row is **exactly one line**: a caret, an optional pin glyph, and the command text. No
  per-row description, no per-row timestamp, no badge column, and **no detail panel** to the
  right.
- The failed `git stauts` row is **visibly dimmer** than the successful rows, with no "failed"
  badge text. Select it - it must stay legible while selected.
- One footer line only: selected row's metadata, then the key hints, then the dot.
- Step 3: the popup is about **one row tall plus chrome**. It does not reserve space for five.
- Step 4: the key hints and the dot survive; the metadata is what ellipsizes.

The dim is a judgement call and this is the first time it has been seen on a real monitor -
the question is "quietly de-emphasised" versus "greyed out and unreadable". Say which.

---

## 7. Fix mode fires on a typo and stays quiet on ordinary failures

**Precondition:** a git repository. Prompt empty.

**Steps:**

1. Run `gti status`.
2. At the next prompt, type `gt`.
3. Run `git stauts`.
4. Run something that fails for a reason nothing can recognise - `exit 3` in bash, or
   `cmd /c exit 3` in pwsh.
5. Pass B only: run `cat /etc/shadow` as a non-root user.

**Pass:**

- Step 1: the **Fix popup opens** with "Did you mean git?". The headline is the *fix*, not the
  failed command - the failed command is already on screen above.
- Step 2: `gti status` is **not offered**, and neither is any other `gti ...` line in history.
- Step 3: recognised. Git prints its own "The most similar command is / status" and the fix
  quotes it back.
- Step 4: **no Fix bubble at all.** A non-zero exit on its own is not enough; only a row
  produced by a recogniser that actually read the output surfaces.
- Step 5: a Fix row explaining permission denied.

**Flag step 5 loudly if nothing appears.** That recogniser's pattern was transcribed from
documentation rather than captured from real output - Git Bash on NTFS cannot produce the
message - so this is the first time it meets a real one.

---

## 8. Placement is anchored to the prompt, with no corrections

**Precondition:** Pass B, SSH, integration installed. Terminal log capturing.

**Steps:**

1. Split the tab into two panes, both SSH to the instrumented host.
2. In each pane, trigger the bubble by typing two characters, then open the popup with
   `Ctrl+R`.
3. Do this with the prompt near the top of the pane, in the middle, and near the bottom.
4. Enter and leave a full-screen program - `htop`, or `vim` then `:q`.
5. Close the app and grep the terminal log for `[AssistAnchor][SSH][Corrected]`.

**Pass:**

- The bubble sits directly above the prompt line at every position, in **both** panes.
  Neither pane is silently missing its surface.
- The integration indicator reads **integrated**.
- Step 4: the overlay disappears the instant the alt screen is entered, and returns on exit.
- Step 5: **zero `[Corrected]` lines.** `[Applied]` lines are expected and fine.

This is re-enable criterion 2, and reading that log is the only way to check it. A test
cannot: the criterion is a property of the diagnostic output, not of the code.

---

## 9. Degraded (markless) sessions: browse yes, guess no

**Precondition:** a `cmd.exe` pane with three commands already run in it.

**Steps:**

1. Type `di` and wait.
2. Press `Ctrl+R`.
3. With the line **empty**, arrow to a row and press `Enter`.
4. Press `Ctrl+R` again, type `d` so there is text on the line, arrow to a row, press `Enter`.
5. Watch the integration indicator throughout.

**Pass:**

- Step 1: **no bubble.** There is no readable command line, so there is no query to rank.
- Step 2: the popup opens on the recency list. An explicit request is always honoured.
- Step 3: the row **is** inserted - the pane can prove the line was empty.
- Step 4: insertion is **refused**, `Enter` falls through, and the shell submits the `d` you
  typed. `Enter` must not become a dead key.
- Step 5: the indicator reads **basic** throughout, as a plain statement of mode rather than a
  warning.

---

## 10. Remote integration installs from one pasted line

**Precondition:** an SSH profile to a Linux host running bash or zsh, currently **not**
instrumented - the indicator reads `basic`.

**Steps:**

1. Settings -> Terminal -> Command assistant -> Remote shell integration. **Look at this row
   before touching it:** confirm the shell dropdown, "Copy installer" and "Copy plain snippet"
   are all fully visible and not clipped at the right edge.
2. Pick the remote shell and press **Copy installer**.
3. In the SSH pane, paste the one line at the prompt and press Enter.
4. Read what it printed.
5. Paste and run it a **second** time.
6. Open a **new** session to the same host.
7. Type two characters; run a command; run a failing command.

**Pass:**

- Step 3 prints `nova: wrote ~/.nova-shell-integration.sh` and `nova: added loader line to
  ~/.<rc>` and nothing else - no base64/gzip complaint, no shell syntax error, no `>`
  continuation prompt.
- Step 5 reports no change and writes nothing.
- Step 6: the indicator flips to **integrated**.
- Step 7: the bubble appears while typing, the command lands in `Ctrl+R` with an exit code,
  and the failure reaches Fix mode.

**If step 3 gives you `unexpected EOF while looking for matching '` or leaves you at a `>`
prompt:** that is the 4096-byte canonical-mode paste truncation. Press Ctrl-C, use **Copy
plain snippet** instead, and record it - that failure has never been reproduced deliberately,
so a real sighting is worth capturing.

**Step 1 is not a formality.** The button row is estimated at roughly 430px of content in a
fixed 360px column, so if it overflows it does so at every window size. "Copy plain snippet"
is the documented escape hatch for the truncation case immediately above; if it is clipped,
the workaround is unreachable.

**Not covered here: fish.** Its installer has never been executed anywhere except Linux CI.

---

## What this checklist does not cover

Stated so that a clean run is not mistaken for more than it is:

- **fish, anywhere.** No fish on the development box; the installer and the shell integration
  are exercised only by Linux CI, in a lane that is `continue-on-error`.
- **zsh and fish under scenario 4's replace path.** The backspace-count argument is measured
  against real shells for pwsh 7, Windows PowerShell 5.1 and bash only. zsh and fish rest on
  documentation. Replace is the only destructive write the feature performs.
- **A real 4096-byte tty truncation.** The byte offsets are measured and the limit is well
  established, but nothing has been pasted into a live canonical-mode shell.
- **Non-default DPI or UI font scale.** The popup's height constants were measured at the
  headless default.
- **Two NovaTerminal instances at once.** The history store assumes a single process owns the
  file. Two windows are one process and are fine; two installs are not a supported
  configuration.
