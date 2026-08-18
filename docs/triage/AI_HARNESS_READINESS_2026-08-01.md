# AI Harness CLI Readiness Audit

Date: 2026-08-01
Targets: Claude Code (Ink/Node), OpenAI Codex CLI (ratatui/crossterm), OpenCode (Bubble Tea/opentui)
Method: every claim below was verified against the current tree (`src/NovaTerminal.VT/AnsiParser.cs`, `src/NovaTerminal.App/MainWindow.axaml.cs`), not against `docs/vt_coverage_matrix.md` or `docs/vt_ghostty_gap_matrix.md` (both known to lag the code).

## Status (updated 2026-08-02)

Issues filed #264-#272 plus follow-up #274. Merged: #264 (PR #273), #265 (#275), #266 (#277), #267 (#278), #268 (#280), #269 (#281). Still open: #270 underline styles, #271 OSC 9/9;4, #272 XTVERSION, #274 remaining unguarded CSI handlers (found during review of #273 — includes a live XTSMGRAPHICS misparse). Review also caught and fixed an OSC 52 reply command-injection vector and a pre-existing scrollback-absolute mouse coordinate bug.

## Verdict

NovaTerminal will *run* all three harnesses today without hangs — DA1/DA2/DSR are answered and XTGETTCAP gets a proper failure reply, so feature-detection fences resolve. But there is one real parser bug triggered *at startup by Codex and OpenCode*, and the biggest UX gaps are the kitty keyboard protocol, OSC 11 color query, and OSC 52.

## Already solid (verified in tree)

| Requirement | Used by | Evidence |
|---|---|---|
| DA1 response `CSI ?62;4;22c` | crossterm/bubbletea query fences | `AnsiParser.cs:920` |
| DA2 response `CSI >1;10;0c` | version probes | `AnsiParser.cs:910` |
| DSR 5 / DSR 6 (CPR) | ratatui cursor math, Ink | `AnsiParser.cs:923-933` |
| Synchronized output `?2026` | all three (flicker-free redraw) | `AnsiParser.cs:1121-1123` |
| Bracketed paste `?2004` | all three | `ModeState`, `TerminalInputSender.SendBracketedPaste` |
| Alt screen `?1049/?47/?1047` | Codex, OpenCode | `AlternateScreenTests`, replay suites |
| XTGETTCAP failure reply (`DCS 0+r ST`) | prevents client blocking | `AnsiParser.cs:1385-1396` |
| SGR mouse `?1000/?1002/?1006` | Codex, OpenCode | `ModeState`, `TerminalView` |
| Focus events `?1004` | Codex, OpenCode | `TerminalView.OnGotFocus/OnLostFocus` |
| OSC 0/2 title, OSC 8 hyperlinks, OSC 133 A/B/C/D | all three | `AnsiParser.HandleOsc` (`:1503`, `:1528`, `:1599`) |
| Truecolor + 256-color SGR | all three | `SgrAttributeTests` |
| Unknown OSC silently dropped (OSC 9;4 progress won't leak garbage) | Claude Code | `HandleOsc` falls through |

## P0 — fix before calling it "ready"

### 1. BUG: unguarded `CSI ... u` / `s` / `r` misparse prefixed variants

`case 'u'` (`AnsiParser.cs:865`) runs RestoreCursor with no `leader`/`isPrivate` guard. Codex (crossterm `supports_keyboard_enhancement`) and OpenCode both send the kitty keyboard query `CSI ? u` **at startup** — NovaTerminal executes a spurious cursor restore instead of ignoring it. Kitty push/pop (`CSI > flags u`, `CSI < u`) hit the same path. Likewise `case 's'` (`:862`) would treat XTSAVE (`CSI ? Pm s`) as SaveCursor, and `case 'r'` (`:695`) would treat XTRESTORE (`CSI ? Pm r`) as DECSTBM — setting a bogus scroll region and homing the cursor. Fix: guard all three cases on `leader == '\0'` (compare the existing `case 'm'` guard at `:868-874`).

### 2. Kitty keyboard protocol (`CSI u`)

No implementation (only the graphics protocol exists). Consequences:
- Claude Code: Shift+Enter cannot insert a newline; NovaTerminal isn't in `/terminal-setup`'s known list, so users are stuck with `\`+Enter. Claude Code auto-enables the protocol only when the terminal answers `CSI ? u`.
- Codex: requests DISAMBIGUATE_ESCAPE_CODES, REPORT_EVENT_TYPES, REPORT_ALTERNATE_KEYS; detection fails → degraded key handling (Shift+Enter, Esc disambiguation).
- OpenCode: renderer is configured for kitty keyboard; same degradation.

Verified input side: `MainWindow.axaml.cs:1843` sends `\r` for Enter regardless of Shift. Minimum viable scope: respond to `CSI ? u`, maintain a flags stack (`CSI > u` push / `CSI < u` pop, reset on alt-screen exit + RIS), and encode at least the disambiguate tier (Shift+Enter → `CSI 13;2u`, etc.).

### 3. OSC 10/11 color query (no response)

`HandleOsc` handles only 0/2, 7, 8, 133, 1337, 1339. OpenCode queries `OSC 11;?` for dark/light detection and blocks ~1s on timeout, then wrongly assumes default — every OpenCode launch pays a 1-second startup stall. Vim/neovim `background` detection has the same dependency. Respond with `OSC 11;rgb:RRRR/GGGG/BBBB ST` from the active theme (and OSC 10 for foreground; OSC 4 palette queries are the natural follow-on).

## P1 — visible quality gaps

- **OSC 52 clipboard**: not implemented. Yank/copy inside harness TUIs (and anything over SSH) can't reach the system clipboard. Security-sensitive — default to write-only with a setting, per the note in `vt_ghostty_gap_matrix.md`.
- **`?1003` any-event mouse motion**: mode flag exists but `TerminalView.OnPointerMoved` only reports motion while a button is held. Hover effects in TUIs won't work.
- **DECRQM (`CSI ? Ps $p`)**: unanswered (falls to Unhandled CSI log). Apps probing `?2026` support via DECRQM conclude sync output is unsupported and fall back to flickery redraws even though NovaTerminal supports it. Cheap win: answer for the modes already tracked in `ModeState`.
- **Underline styles `4:3` + SGR 58/59**: parsed but collapsed to plain underline. Harnesses use undercurl for diagnostics/spell-style markup. Cosmetic.

## P2 — nice to have

- **OSC 9;4 progress** (Claude Code `terminalProgressBarEnabled`) — currently ignored cleanly; implementing gives taskbar/tab progress.
- **OSC 9 desktop notifications** — long-run completion pings from harnesses.
- **XTVERSION (`CSI > q`)** — some tools log terminal identity; answer with `DCS >|NovaTerminal x.y ST`.
- **Terminfo advertisement** — still `TERM=xterm-256color`; fine for these three (they runtime-probe), so keep P2 per the existing ghostty-gap recommendation.

## Recommended order

1. Guard `CSI s/u/r` against `?`/`>`/`<`/`=` prefixes (small, fixes an active misparse; add hardening unit tests alongside `AnsiParserHardeningTests`).
2. OSC 10/11 query responses (small; removes OpenCode's 1s stall and theme misdetection).
3. Kitty keyboard protocol, disambiguate tier first (medium; unlocks Shift+Enter across all three harnesses).
4. DECRQM responses for tracked modes (small).
5. OSC 52 write-only with settings gate (medium; needs security policy).
6. `?1003` hover motion, underline styles, OSC 9 / 9;4 (polish).

## Cross-checks performed

- Grepped tree for DECRQM/XTVERSION/XTGETTCAP/kitty-keyboard/modifyOtherKeys: only XTGETTCAP and kitty *graphics* exist.
- Confirmed no OSC 10/11/52/4/9 handlers in `HandleOsc`.
- Confirmed `?2026`, `?2004`, `?1004`, `?1000/1002/1003/1006` in `HandleDECPrivateMode`.
- Confirmed Shift+Enter → `\r` in `MainWindow.axaml.cs`.
- External behavior sourced from: Claude Code terminal-config docs, openai/codex `keyboard_modes.rs` + issues #21699/#18741, OpenCode OSC 11 theme-detection issues #21870/#23196.
