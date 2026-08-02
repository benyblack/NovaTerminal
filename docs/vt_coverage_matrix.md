# VT Conformance Matrix

This document is the **single source of truth** for what NovaTerminal supports (and intentionally does not) in terms of VT/xterm/DEC behavior.

It is designed to be:
- **Actionable**: each row maps to code areas + tests.
- **Auditable**: “Supported” means there is verification evidence (replay, unit test, external suite).
- **Maintainable**: when behavior changes, update **one row** and its linked tests.

---

## Status legend

| Status | Meaning | Verification requirement |
|---|---|---|
| ✅ Supported | Implemented and correct for the documented scope | At least 1 automated test (replay/unit/external) |
| ⚠ Partial | Implemented but with limitations or known deviations | Automated test + deviation note |
| 🧪 Experimental | Works but not yet stable/guaranteed | Optional tests; can change |
| ❌ Not supported | Not implemented | N/A |
| 🚫 Won’t support | Intentionally not supported | Rationale required |

**Verification types**
- **Replay**: `*.rec → *.snap` (golden)
- **Unit**: targeted unit tests (parser/buffer)
- **External**: external suites (e.g., VTTEST) captured into replays

---

## Terminal model assumptions

- Default size: **80×24** unless specified
- Default TERM exposed by PTY layer: **xterm-256color**
- Locale enforced for deterministic captures: **LC_ALL=C, LANG=C**
- Rendering does not affect correctness verification; correctness is asserted on **buffer state**

---

## How to use this matrix

1. When you implement a feature, add/update a row with:
   - **Status**
   - **Evidence** (test path)
   - **Code ownership** (file(s)/module)
2. When a user reports a bug:
   - Add a row or update status to ⚠ Partial
   - Add a replay test reproducing it
3. Before releases:
   - Ensure “✅ Supported” rows have at least one automated verification link.

---

## 1) Input parsing & state machine

| Feature / Sequence | Spec / Notes | Status | Evidence | Ownership (code) | Known deviations |
|---|---|---:|---|---|---|
| C0 controls (BEL, BS, HT, LF, CR) | Basic control chars | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/TabSystemTests.cs`; Replay: `tests/Replays/...` | `Core/AnsiParser.cs`, `Core/TerminalBuffer.cs` | HT now follows stored tab stops instead of inserting spaces; broader C0 coverage is still only partially audited |
| C1 via 7-bit ESC (ESC @.._) | “7-bit C1” translation | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/AnsiParserHardeningTests.cs` | `Core/AnsiParser.cs` | Recognizes CSI/OSC/DCS/APC plus IND/NEL/RI; unsupported `ESC @.._` controls are ignored with recovery rather than fully implemented |
| 8-bit C1 bytes (0x80–0x9F) | If supported, must be explicit | ❌ Not supported | — | `Core/AnsiParser.cs` | (fill) |
| String terminators (ST = ESC \\, BEL) | OSC/APC termination rules | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/AnsiParserHardeningTests.cs` | `Core/AnsiParser.cs` | OSC accepts BEL and ST; DCS/APC also accept BEL as permissive recovery behavior, not strict spec compliance |
| Unknown sequence handling | Ignore/print/strict? | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/AnsiParserHardeningTests.cs` | `Core/AnsiParser.cs` | Unknown `ESC @.._` sequences are ignored and parser resumes on the next valid escape or printable content |
| Error recovery on malformed sequences | Robustness | ⚠ Partial | Fuzz/Unit: `tests/NovaTerminal.App.Tests/AnsiParserHardeningTests.cs`, `tests/NovaTerminal.App.Tests/AnsiCorpusReplayTests.cs` | `Core/AnsiParser.cs` | Malformed OSC/CSI/DCS/APC recover across chunk boundaries and nested ESC, but this is a best-effort parser policy rather than full conformance coverage |

---

## 2) Cursor movement & positioning (CSI)

| Feature / CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| CUU/CUD/CUF/CUB (A/B/C/D) | Cursor up/down/forward/back | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/PendingWrapTests.cs` | Parser+Buffer | Explicit CUF movement is covered via pending-wrap reset; dedicated CUU/CUD/CUB targeted coverage is still missing |
| CUP / HVP (H/f) | Positioning, default params | ✅ Supported | Unit + Replay: `tests/NovaTerminal.App.Tests/CursorPositioningCompletionTests.cs`, `tests/NovaTerminal.App.Tests/ReplayTests/RegressionTests.cs` | Parser+Buffer | |
| CHA/CPL/CNL (G/F/E) | Horizontal absolute / prev/next line | ⚠ Partial | Replay | Parser+Buffer | |
| CHT (I) | Cursor forward tabulation | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/TabSystemTests.cs` | Parser+Buffer | |
| CBT (Z) | Cursor backward tabulation | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/TabSystemTests.cs` | Parser+Buffer | |
| VPA/HPA (d/G/`) | Absolute row/col | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/CursorPositioningCompletionTests.cs` | Parser+Buffer | |
| HPR/VPR (a/e) | Relative row/col | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/CursorPositioningCompletionTests.cs` | Parser+Buffer | |

---

## 3) Erase & insert/delete (CSI)

| Feature / CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| ED (J) | 0/1/2 erase display | ✅ Supported | Replay: `tests/NovaTerminal.App.Tests/ReplayTests/RegressionTests.cs`, `tests/NovaTerminal.App.Tests/Fixtures/Replay/vttest_cursor.rec`; Unit: `tests/NovaTerminal.Platform.Tests/Ssh/NativeSshTerminalParityTests.cs` | Parser+Buffer | |
| EL (K) | 0/1/2 erase line | ✅ Supported | Unit: `tests/NovaTerminal.Platform.Tests/Ssh/NativeSshTerminalParityTests.cs` | Parser+Buffer | |
| ICH ( @ ) | Insert chars | ⚠ Partial | Code path | Parser+Buffer | Implemented in parser/buffer; needs targeted unit coverage |
| DCH (P) | Delete chars | ⚠ Partial | Code path | Parser+Buffer | Implemented in parser/buffer; needs targeted unit coverage |
| IL (L) / DL (M) | Insert/delete lines | ⚠ Partial | Replay | Buffer | Scroll region interactions |
| ECH (X) | Erase chars | ⚠ Partial | Code path | Parser+Buffer | Implemented in parser/buffer; needs targeted unit coverage |

---

## 4) Scrolling, margins, and origin mode

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| DECSTBM (CSI t;b r) | Set top/bottom margins | ⚠ Partial | VTTEST: scroll scenario | Parser+Buffer | |
| IND (ESC D) / RI (ESC M) | Index / Reverse index | ⚠ Partial | Replay | Parser+Buffer | |
| DECOM (origin mode) | Cursor relative to margins | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/DecModeTests.cs` | Parser+Buffer | |
| Wraparound DECAWM | Auto wrap | ⚠ Partial | Replay | Buffer | Wide glyph edge cases |
| Smooth scroll | Not required for correctness | 🚫 Won’t support | — | — | Renderer concern |

---

## 5) Screen buffers & modes (DEC private modes)

| Mode | CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---|---:|---|---|---|
| Alternate screen | ?1049 / ?47 / ?1047 | Switch + save/restore cursor | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/AlternateScreenTests.cs`; Replay: `tests/NovaTerminal.App.Tests/ReplayTests/AlternateScreenReplayTests.cs`, `tests/NovaTerminal.App.Tests/ReplayTests/NativeSshReplayParityTests.cs` | Buffer | Main scrollback is preserved and alt-screen output never enters scrollback. `?47` reuses the existing alternate buffer/state without clearing; `?1047` and `?1049` clear and home the alternate buffer on entry. Nested/redundant alt-screen enters are treated as no-op, and a `?1049` save is consumed by the first exit from alt-screen regardless of whether that exit uses `?47l`, `?1047l`, or `?1049l`. |
| Show cursor | ?25 | | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/DecModeTests.cs`; Replay: `tests/NovaTerminal.App.Tests/ReplayTests/ReplayV2Tests.cs` | Buffer+Renderer | |
| Application cursor keys | ?1 | Impacts input mapping | ⚠ Partial | Unit/Code: `ReplayV2Tests`, app input paths | Parser+Input | Parser/UI wiring exists; needs targeted key-mapping tests |
| Focus event reporting | ?1004 | Emits `CSI I` / `CSI O` on focus transitions | ⚠ Partial | Unit/Code: `DecModeTests`, `TerminalView` | Parser+Input | Mode flag tested; focus emission covered by app path, not headless UI test |
| Bracketed paste | ?2004 | Input feature | ⚠ Partial | Unit | Input layer | |
| Mouse reporting | ?1000/1002/1003/1006 etc | Click (`?1000`) and drag-motion (`?1002`) tracking, plus hover motion with no buttons held under any-event tracking (`?1003`, issue #269); SGR (`?1006`) and legacy X10 coordinate encodings both supported. Motion reports are coalesced to at most one per distinct terminal cell (tracked last-reported cell, reset on buffer attach/resize, alternate-screen switch, pointer exit, and `?1002`/`?1003` mode changes observed between two motion events) so a stationary-but-jittery pointer or a TUI toggling tracking modes doesn't flood the PTY | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/Input/TerminalInputModeEncoderTests.cs`, `tests/NovaTerminal.App.Tests/Input/TerminalViewMouseMotionTests.cs` | Input layer | `?1001` (highlight tracking) and urxvt (`?1015`)/pixel-position (`?1016`) mouse coordinate encodings are not implemented; only X10 and SGR (`?1006`) coordinate encodings are supported. Coordinates are viewport-relative and 1-based on every report path (motion, press, release, wheel); the legacy X10 encoding clamps coordinates to its 223 ceiling rather than promoting the report to the SGR form the application never requested |
| Cursor style | CSI Ps SP q | DECSCUSR block/beam/underline + blink state | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/OscUxTests.cs` | Parser+Renderer | |
| Kitty keyboard protocol | CSI ? u / CSI > Pm u / CSI < Pm u / CSI = Pm ; Pm u | Progressive-enhancement flags: query, push, pop, set. Per-screen-buffer flag stacks (depth 32, oldest evicted on overflow); RIS clears both | ⚠ Partial | Unit: `tests/NovaTerminal.VT.Tests/KittyKeyboardProtocolTests.cs`, `tests/NovaTerminal.App.Tests/Input/KittyKeyboardEncodingTests.cs`, `tests/NovaTerminal.App.Tests/AnsiParserHardeningTests.cs`, `tests/NovaTerminal.App.Tests/CommandAssist/TerminalViewKeyHandlingTests.cs` | Parser+Input | Scope: disambiguate-escape-codes tier (`0b1`) only. Bits `0b10`/`0b100`/`0b1000`/`0b10000` (event types, alternate keys, all-keys, associated text) are accepted but masked out on push/set, so the `CSI ? u` reply only advertises flags actually honored. Key encoding covers Esc, modified Enter/Tab/Backspace, and ctrl/alt/super combinations on the spec's legacy-text keys; keypad and functional keys keep their legacy encodings (including modified arrows/Home/End/PgUp/PgDn - the spec's `CSI 1;mod <letter>` form is not emitted, so e.g. Ctrl+Left stays an unmodified `CSI D`). AltGr on Windows non-US layouts is reported as Control+Alt; the encoder carves that combination out (falls through to legacy/text) so composed characters are not swallowed - the accepted trade-off is that literal Ctrl+Alt+&lt;key&gt; shortcuts on US layouts also lose kitty encoding, same as the pre-existing Alt-sends-ESC carve-out. Gated end-to-end by `TerminalSettings.EnableKittyKeyboardProtocol` (default on): when off, the App-side encoder is skipped unconditionally and the `CSI ? u` reply always reports flags 0 regardless of the pushed stack state. The tab-broadcast path in `MainWindow` builds its own input independently of this encoder and always sends legacy encoding to broadcast targets regardless of their own flag state. |
| DECRQM / DECRPM (mode query) | CSI Ps $ p / CSI ? Ps $ p → CSI [?] Ps ; Pm $ y | Reports live set/reset state for private modes 1, 6, 7, 25, 47, 1000, 1002, 1003, 1004, 1006, 1047, 1049, 2004, 2026 and ANSI modes 4 (IRM), 12 (SRM), 20 (LNM); unrecognized modes report `Pm=0`. Lets apps (e.g. probing `?2026` synchronized output) detect real support instead of assuming it from unanswered queries | ✅ Supported | Unit: `tests/NovaTerminal.VT.Tests/DecrqmTests.cs` | Parser | `?9001` (ConPTY passthrough) is accepted by `CSI ? Ps h/l` but has no tracked boolean state, so DECRQM reports it as `Pm=0` (not recognized) rather than guessing. Dispatch keys on the `$` intermediate exactly, so `CSI ! p` (DECSTR, not implemented) and bare `CSI p` are unaffected. |

---

## 6) SGR attributes & colors

| Feature | CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| Basic SGR (0,1,2,3,4,5,7,9,22,23,24,25,27,29) | | Bold/dim/italic/underline/blink/reverse/strike | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/SgrAttributeTests.cs`; VTTEST: sgr scenario | Parser+Buffer+Renderer | Underline style/color tracked separately |
| 8/16 colors | 30–37/90–97, 40–47/100–107 | | ⚠ Partial | Replay | Parser+Buffer | |
| 256-color | 38;5;N / 48;5;N | | ⚠ Partial | Replay | Parser+Buffer | |
| Truecolor | 38;2;r;g;b / 48;2;r;g;b | | ⚠ Partial | Replay | Parser+Buffer | |
| Underline styles | 4:1.. | xterm | ❌ Not supported | — | Buffer | |

---

## 7) Tabs

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| HT (tab) movement | | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/TabSystemTests.cs` | Parser+Buffer | Custom tab stops are clipped on width shrink; columns exposed by width growth start with default 8-column tab stops |
| Tab stops set/clear | ESC H, CSI g | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/TabSystemTests.cs` | Parser+Buffer | `CSI g` supports current-stop clear (`0`/default) and clear-all (`3`); other parameters are ignored |

---

## 8) OSC sequences

| OSC | Purpose | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| OSC 0/2 | Set title | ⚠ Partial | Manual/Unit | App/UI | |
| OSC 7 | CWD reporting | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/OscUxTests.cs` | Parser+App | |
| OSC 10/11 | Foreground/background color query | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/OscUxTests.cs` | Parser+App | Query form (`?`) only; set form is ignored safely, no runtime palette change. Colors sourced from `AnsiParser.DefaultForeground`/`DefaultBackground`, wired from the active theme at parser creation and on `TerminalPane.ApplySettings`; falls back to fg C0C0C0 / bg 000000 when unset |
| OSC 52 | Clipboard | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/OscUxTests.cs`, `tests/NovaTerminal.App.Tests/Controls/PaneParserWiringTests.cs` | Parser+App | Write-only (issue #268): valid base64 decodes and raises `AnsiParser.OnClipboardWrite`, capped at 1 MiB decoded (rejected on encoded length before decoding, re-checked after); invalid base64 dropped silently; a sequence with no payload separator at all is ignored rather than clearing the clipboard. Targets `c`/`p` both map to the system clipboard, other selection chars ignored. Query (`?`) always gets an empty-payload denial reply, never real clipboard contents — clipboard READ is unsupported by design (security). The echoed selection parameter is whitelisted to `c p q s 0-7` and length-capped first: responses go to the child process's stdin, so an unsanitized echo was a command-injection primitive (PR #280 review). Gated by `TerminalSettings.AllowOsc52ClipboardWrite` (default true, single global setting; per-profile/SSH-scoped opt-in is future work) |
| OSC 8 | Hyperlinks | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/OscUxTests.cs` | Parser+Renderer+UI | Ctrl-click open path is app-level |
| OSC 133 | Shell integration lifecycle | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/OscShellIntegrationTests.cs` | Parser+Command Assist | Supports A/B/C/D markers; broader semantic prompt extensions not audited |
| OSC 1337 | iTerm2 inline images | ⚠ Partial | Manual: `docs/qa/QA_GRAPHICS.md` | Parser+Renderer | Parser support exists, but targeted automated replay/unit coverage for OSC 1337 is still missing |
| OSC 1339 | Windows conpty tunnel | 🧪 Experimental | Manual | Parser+Win | |

---

## 9) Kitty graphics / APC

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| Kitty graphics protocol | APC / OSC forms | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/GraphicsTests.cs`, `tests/NovaTerminal.App.Tests/AnsiParserHardeningTests.cs` | Parser+Renderer | |
| Placement, z-index, scrolling | Complex interactions | ⚠ Partial | Manual | Buffer+Renderer | |

---

## 10) SIXEL

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| SIXEL decode/render | | ⚠ Partial | Manual | Decoder+Renderer | |
| SIXEL scrolling behavior | | ❌ Not supported | — | Buffer+Renderer | |

---

## 11) Clipboard, selection, and hyperlinking

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| Selection model | UI behavior | ⚠ Partial | Manual | UI | |
| Copy on select | Configurable | ❌ Not supported | — | UI | |
| Hyperlinks | OSC 8 | ✅ Supported | Unit: `tests/NovaTerminal.App.Tests/OscUxTests.cs` | Parser+UI | Ctrl-click open path is app-level |

---

## 12) Unicode width, graphemes, and font behavior

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| wcwidth-like width | CJK/emoji width | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/WidthTests.cs`, `tests/NovaTerminal.App.Tests/UnicodeWidthModelV2Tests.cs`; Replay: `tests/NovaTerminal.App.Tests/Fixtures/Replay/mixed_unicode.rec` | Buffer+Renderer | Deterministic 0/1/2-cell model for combining marks, emoji modifiers, ZWJ emoji, variation selectors, and regional-indicator flags. No Unicode-version pin or full UAX #11 conformance table is documented yet. |
| Combining marks | Grapheme clusters | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/GraphemeAttachmentTests.cs`, `tests/NovaTerminal.App.Tests/SurrogateTests.cs`, `tests/NovaTerminal.App.Tests/UnicodeWidthModelV2Tests.cs`, `tests/NovaTerminal.App.Tests/ScrollAndWrapCorrectnessTests.cs` | Buffer+Renderer | Combining/variation attachment is covered for common terminal cases, including pending-wrap boundaries. Full extended-grapheme-cluster conformance is not claimed. |
| ZWJ emoji sequences | | ⚠ Partial | Unit: `tests/NovaTerminal.App.Tests/GraphemeAttachmentTests.cs`, `tests/NovaTerminal.App.Tests/WidthTests.cs`, `tests/NovaTerminal.App.Tests/UnicodeWidthModelV2Tests.cs` | Buffer+Renderer | Chunked ZWJ families, emoji modifiers, VS15/VS16, and chunked regional-indicator flag pairs are covered. Remaining gaps: cursor-addressing CSI remains cell-oriented, and width-changing selectors that arrive after a base glyph already placed at the last column are not guaranteed to retroactively reflow. |

---

## 13) Verification inventory

### External suites
- VTTEST capture adapter: `tests/NovaTerminal.ExternalSuites/`  
  Recordings: `tests/Replays/Vttest/*.rec`

### Replay suites (goldens)
- Add all `.rec/.snap` pairs under: `NovaTerminal.Tests/Fixtures/Replay/`

### Unit tests
- Parser/Buffer tests under: `NovaTerminal.Tests/`

---

## 14) Maintenance rules (non-negotiable)

1. **No “✅ Supported” without evidence.**
2. Evidence must be a stable path in the repo.
3. If behavior differs from xterm/wezterm, document it under “Known deviations”.
4. When you fix a deviation, update the row and add/adjust tests.
5. Every new OSC/APC/CSI feature must add a row here.

---

## 15) Roadmap linkage

When a feature is planned but not implemented, add a row with:
- Status: ❌ Not supported
- Evidence: “Planned”
- Link: roadmap item / issue number

Example:
- `❌ Not supported` → “Roadmap: M4 Font & Text Excellence”
