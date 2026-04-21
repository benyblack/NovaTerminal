# NovaTerminal VT vs Ghostty Gap Matrix

Date: 2026-04-19

Purpose: identify what NovaTerminal already has, where `docs/vt_coverage_matrix.md` is stale, and which Ghostty-level VT features are real gaps.

Sources:
- Nova local evidence: `src/NovaTerminal.VT/AnsiParser.cs`, `src/NovaTerminal.VT/ModeState.cs`, `src/NovaTerminal.App/Core/TerminalView.cs`, `tests/NovaTerminal.Tests/OscUxTests.cs`, `tests/NovaTerminal.Tests/DecModeTests.cs`, `tests/NovaTerminal.Tests/SgrAttributeTests.cs`, `docs/vt_coverage_matrix.md`
- Ghostty docs: <https://ghostty.org/docs/help/terminfo>, <https://ghostty.org/docs/vt/reference>, <https://ghostty.org/docs/vt/external>, <https://ghostty.org/docs/vt/osc/52>, <https://ghostty.org/docs/install/release-notes/1-3-0>

## Legend

| Label | Meaning |
|---|---|
| Already have | Implemented in code and at least partially exercised by tests or app wiring. |
| Doc stale | Current code is ahead of `docs/vt_coverage_matrix.md`. |
| Partial | Implemented, but not Ghostty-level complete or not fully verified. |
| Real gap | No meaningful implementation found in the current tree. |

Priority:
- P0: high impact for modern TUI/app compatibility or feature discovery.
- P1: visible parity gap for power users and modern shells.
- P2: useful but less likely to block daily workflows.

## Executive Summary

NovaTerminal is stronger than its current conformance matrix says. The matrix is stale for OSC 7, OSC 8, application cursor keys, DECOM, ICH/DCH/ECH, focus reporting, cursor style, and Unicode/grapheme support.

The biggest real Ghostty parity gaps are not basic VT rendering. They are feature discovery, modern keyboard reporting, richer OSC support, complete shell-integration semantics, styled underline/color handling, and systematic conformance documentation.

## Gap Matrix

| Area | Nova state | Ghostty-level target | Classification | Priority | Evidence / notes |
|---|---|---|---|---:|---|
| TERM / terminfo advertisement | Forces `TERM=xterm-256color` with `COLORTERM=truecolor`. | Ghostty uses `xterm-ghostty` with a terminfo entry to advertise advanced capabilities. | Real gap | P0 | `src/NovaTerminal.App/native/src/lib.rs`, `src/NovaTerminal.Core/Ssh/Native/NativeSshConnectionOptions.cs`; Ghostty terminfo docs say fallback `xterm-256color` loses advanced features such as styled underlines. |
| Canonical VT matrix accuracy | Several rows say not supported while code supports them. | Source-of-truth matrix should match implementation and evidence. | Doc stale | P0 | `docs/vt_coverage_matrix.md` conflicts with `AnsiParser.cs`, `ModeState.cs`, `OscUxTests.cs`, and `DecModeTests.cs`. |
| Application cursor keys | Parser tracks `?1`, UI emits SS3 arrow sequences. | Correct DECCKM behavior. | Already have / doc stale | P0 | `ModeState.IsApplicationCursorKeys`, `TerminalView` and `MainWindow` key paths. |
| Focus events | Parser tracks `?1004`; view sends `CSI I` / `CSI O` on focus transitions. | FocusIn/FocusOut reporting. | Already have / doc stale | P1 | `AnsiParser.cs`, `TerminalView.OnGotFocus`, `TerminalView.OnLostFocus`, `DecModeTests.FocusEventReporting_SetsModeFlag`. |
| Bracketed paste | Parser tracks `?2004`; paste helper wraps content. | Standard bracketed paste behavior. | Already have | P1 | `TerminalInputSender.SendBracketedPaste`, `DecModeTests`, `TerminalInputSenderTests`. |
| Mouse reporting | Supports X10/button/any/SGR mode flags and SGR/X10 event encoding. | Full xterm/SGR behavior, including any-event hover semantics and modifier precision. | Partial | P1 | Current `TerminalView.OnPointerMoved` only sends motion when a button is pressed, even if `?1003` is set. |
| Kitty keyboard protocol / `CSI u` | No implementation found. | Ghostty supports modern keyboard protocols through its VT/input stack. | Real gap | P0 | No local matches for Kitty keyboard, `CSI u`, or `modifyOtherKeys`. |
| `modifyOtherKeys` / xterm key modifier controls | Non-SGR `CSI > ... m` is intentionally ignored as non-style input. | Query/configure modified key behavior where expected by advanced apps. | Real gap | P1 | `SgrAttributeTests` guards against treating `CSI > ... m` as SGR, but there is no input-protocol implementation. |
| Cursor style `DECSCUSR` | Parser maps `CSI Ps SP q`; renderer supports block/beam/underline and blink state. | Cursor style and blink control. | Already have / doc stale | P1 | `AnsiParser.ApplyCursorStyle`, `OscUxTests.CsiQ_UpdatesCursorStyleMode`, render snapshot cursor fields. |
| Basic CSI editing | ICH, DCH, ECH, IL, DL exist in parser/buffer. | Ghostty reference includes ICH/DCH/ECH/IL/DL. | Already have / doc stale | P1 | `AnsiParser.cs` handles `@`, `P`, `X`, `L`, `M`; matrix still marks ICH/DCH/ECH unsupported. |
| Tab controls | HT exists; CHT/CBT/TBC/custom tab stops not implemented in matrix. | Ghostty reference lists CHT, CBT, TBC. | Real gap | P1 | Matrix lists CHT/CBT/TBC as unsupported; no local tab-stop state found. |
| Horizontal margins `DECSLRM` | No implementation found. | Ghostty reference lists `CSI Pl ; Pr s`. | Real gap | P2 | No local state for left/right margins. |
| Repeat previous char `REP` | No implementation found. | Ghostty reference lists `CSI Pn b`. | Real gap | P2 | No local handler for CSI `b`. |
| OSC 0/2 title | Parser emits title callback. | Change window title/icon title. | Already have | P1 | `AnsiParser.HandleOsc`. |
| OSC 7 current directory | Parser emits working-directory callback; Command Assist docs rely on it. | Change current working directory metadata. | Already have / doc stale | P0 | `OscUxTests.Osc7_ReportsWorkingDirectory`; matrix still marks OSC 7 unsupported. |
| OSC 8 hyperlinks | Parser stores hyperlink metadata and UI ctrl-click opens absolute URI. | Hyperlinks. | Already have / doc stale | P1 | `OscUxTests.Osc8_Hyperlink_IsAttachedToWrittenCells`, `TerminalView` ctrl-click path; matrix still marks unsupported. |
| OSC 52 clipboard | No implementation found. | Ghostty supports query/change clipboard data with `OSC 52`. | Real gap | P0 | No local `OSC 52`; Ghostty has dedicated OSC 52 docs. |
| OSC 4 / 10-19 / 104 / 110-119 colors | No implementation found. | Query/change/reset palette and dynamic colors. | Real gap | P1 | Ghostty VT reference lists palette, special, dynamic, and reset color OSCs. |
| OSC 21 Kitty Color Protocol | No implementation found. | Query/change colors using Kitty Color Protocol. | Real gap | P2 | Ghostty external protocol docs list OSC 21 support. |
| OSC 22 pointer shape | No implementation found. | Change pointer shape. | Real gap | P2 | Ghostty VT reference lists OSC 22. |
| OSC 9 desktop notification and OSC 9;4 progress | No implementation found. | Notifications and progress state. | Real gap | P2 | Ghostty VT reference lists OSC 9 and 9;4. |
| OSC 133 shell integration | Supports A/B/C/D and base64 command payload. | More complete semantic prompt behavior, click-to-move-cursor extensions, richer prompt/output regions. | Partial | P1 | `AnsiParser.cs` supports basic lifecycle markers; Ghostty 1.3 notes call out more complete OSC 133 plus click-events and `cl=line`. |
| Command-finished notifications | Nova can observe OSC 133 finish; no Ghostty-style notification policy found. | Notify on long-running command finish via shell integration. | Real gap | P2 | Ghostty 1.3 release notes describe notification policy built on OSC 133. |
| SGR italic/strike/faint/blink | Parser and renderer support italic/strike/faint/blink flags. | Core styling parity. | Already have / doc stale | P1 | `SgrAttributeTests`, `TerminalDrawOperation` decoration rendering. |
| Styled underlines | `4:x` is parsed only as underline on/off; no distinct underline style model. | Colored and styled underlines advertised by Ghostty terminfo. | Partial | P1 | `AnsiParser` consumes underline style selector but collapses it; no `UnderlineStyle` storage. |
| Underline color `SGR 58/59` | Parameters are consumed, but color is not rendered separately. | Colored underline support. | Partial | P1 | `AnsiParser` comments say underline color is not rendered separately. |
| Unicode graphemes and emoji | Code and tests support combining attachment, ZWJ emoji, regional indicators, HarfBuzz shaping path. | Ghostty 1.3 calls out Unicode 17 and grapheme conformance for selection, cursor movement, width. | Partial / needs audit | P1 | Nova has `GraphemeAttachmentTests`, `WidthTests`, HarfBuzz renderer; no Unicode-version conformance target is documented. |
| Complex script rendering | HarfBuzz shaping path and golden font tests exist. | Correct Brahmic/complex scripts and grapheme clustering. | Partial / likely close | P1 | `GoldenFontPngTests` covers Arabic and Devanagari; compare against Ghostty's broader claim before marking complete. |
| Graphics protocols | Kitty graphics, iTerm2 inline images, Sixel, and OSC 1339 tunnel exist. | Robust graphics placement, scrolling, query, and platform behavior. | Partial | P1 | `IMAGE_PROTOCOL_SUPPORT.md` is strong; matrix still marks SIXEL scrolling unsupported and Kitty placement partial. |
| Synchronized output | Parser maps `?2026` to `BeginSync`/`EndSync`. | Synchronized output behavior. | Already have / needs matrix precision | P2 | `AnsiParser.HandleDECPrivateMode`, `SynchronizedRenderingTests`. |
| Parser robustness / fuzzing | Matrix says partial; fuzz/stress aspirations exist. | Ghostty reports AFL++ fuzzing and robust error-path testing. | Partial | P2 | Nova has tests and replay infra, but no comparable continuous fuzz target documented as release gate. |

## Recommended Order

1. Update `docs/vt_coverage_matrix.md` for rows that are plainly stale: OSC 7, OSC 8, DECOM, application cursor keys, ICH/DCH/ECH, focus events, cursor style, Unicode/graphemes.
2. Add a terminfo/capability-advertising design before adding many more protocols. Without this, apps will keep treating Nova as plain `xterm-256color`.
3. Implement Kitty keyboard protocol / `CSI u` and decide whether to support xterm `modifyOtherKeys`.
4. Implement secure OSC 52 with explicit policy controls. This needs UI/security design, not just parser code.
5. Add OSC color query/reset support and styled underline/underline-color rendering.
6. Expand OSC 133 toward semantic prompt regions and click-to-move-cursor if Command Assist will use shell structure deeply.

## Notes

- This document is an audit aid, not the canonical conformance source. Once rows are confirmed, move the durable status into `docs/vt_coverage_matrix.md`.
- Do not broaden VT parsing just for Command Assist. Keep Command Assist as a separate subsystem and expose only small parser events where needed.
- Any OSC 52 implementation should default conservative because terminal-output-to-clipboard is a security-sensitive feature.
