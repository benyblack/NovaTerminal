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
| C0 controls (BEL, BS, HT, LF, CR) | Basic control chars | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/TabSystemTests.cs`; Replay: `tests/Replays/...` | `Core/AnsiParser.cs`, `Core/TerminalBuffer.cs` | HT now follows stored tab stops instead of inserting spaces; broader C0 coverage is still only partially audited |
| C1 via 7-bit ESC (ESC @.._) | “7-bit C1” translation | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/AnsiParserHardeningTests.cs` | `Core/AnsiParser.cs` | Recognizes CSI/OSC/DCS/APC plus IND/NEL/RI; unsupported `ESC @.._` controls are ignored with recovery rather than fully implemented |
| 8-bit C1 bytes (0x80–0x9F) | If supported, must be explicit | ❌ Not supported | — | `Core/AnsiParser.cs` | (fill) |
| String terminators (ST = ESC \\, BEL) | OSC/APC termination rules | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/AnsiParserHardeningTests.cs` | `Core/AnsiParser.cs` | OSC accepts BEL and ST; DCS/APC also accept BEL as permissive recovery behavior, not strict spec compliance |
| Unknown sequence handling | Ignore/print/strict? | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/AnsiParserHardeningTests.cs` | `Core/AnsiParser.cs` | Unknown `ESC @.._` sequences are ignored and parser resumes on the next valid escape or printable content |
| Error recovery on malformed sequences | Robustness | ⚠ Partial | Fuzz/Unit: `tests/NovaTerminal.Tests/AnsiParserHardeningTests.cs`, `tests/NovaTerminal.Tests/AnsiCorpusReplayTests.cs` | `Core/AnsiParser.cs` | Malformed OSC/CSI/DCS/APC recover across chunk boundaries and nested ESC, but this is a best-effort parser policy rather than full conformance coverage |

---

## 2) Cursor movement & positioning (CSI)

| Feature / CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| CUU/CUD/CUF/CUB (A/B/C/D) | Cursor up/down/forward/back | ✅ Supported | Replay: `...` | Parser+Buffer | |
| CUP / HVP (H/f) | Positioning, default params | ✅ Supported | Unit + Replay: `tests/NovaTerminal.Tests/CursorPositioningCompletionTests.cs`, `tests/NovaTerminal.Tests/ReplayTests/RegressionTests.cs` | Parser+Buffer | |
| CHA/CPL/CNL (G/F/E) | Horizontal absolute / prev/next line | ⚠ Partial | Replay | Parser+Buffer | |
| CHT (I) | Cursor forward tabulation | ✅ Supported | Unit: `tests/NovaTerminal.Tests/TabSystemTests.cs` | Parser+Buffer | |
| CBT (Z) | Cursor backward tabulation | ✅ Supported | Unit: `tests/NovaTerminal.Tests/TabSystemTests.cs` | Parser+Buffer | |
| VPA/HPA (d/G/`) | Absolute row/col | ✅ Supported | Unit: `tests/NovaTerminal.Tests/CursorPositioningCompletionTests.cs` | Parser+Buffer | |
| HPR/VPR (a/e) | Relative row/col | ✅ Supported | Unit: `tests/NovaTerminal.Tests/CursorPositioningCompletionTests.cs` | Parser+Buffer | |

---

## 3) Erase & insert/delete (CSI)

| Feature / CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| ED (J) | 0/1/2 erase display | ✅ Supported | Replay | Parser+Buffer | |
| EL (K) | 0/1/2 erase line | ✅ Supported | Replay | Parser+Buffer | |
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
| DECOM (origin mode) | Cursor relative to margins | ✅ Supported | Unit: `tests/NovaTerminal.Tests/DecModeTests.cs` | Parser+Buffer | |
| Wraparound DECAWM | Auto wrap | ⚠ Partial | Replay | Buffer | Wide glyph edge cases |
| Smooth scroll | Not required for correctness | 🚫 Won’t support | — | — | Renderer concern |

---

## 5) Screen buffers & modes (DEC private modes)

| Mode | CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---|---:|---|---|---|
| Alternate screen | ?1049 / ?47 / ?1047 | Switch + save/restore cursor | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/AlternateScreenTests.cs`; Replay: `tests/NovaTerminal.Tests/ReplayTests/AlternateScreenReplayTests.cs`, `tests/NovaTerminal.Tests/ReplayTests/NativeSshReplayParityTests.cs` | Buffer | Main scrollback is preserved and alt-screen output never enters scrollback. `?47` reuses the existing alternate buffer/state without clearing; `?1047` and `?1049` clear and home the alternate buffer on entry. Nested/redundant alt-screen enters are treated as no-op, and a `?1049` save is consumed by the first exit from alt-screen regardless of whether that exit uses `?47l`, `?1047l`, or `?1049l`. |
| Show cursor | ?25 | | ✅ Supported | Unit/Replay | Buffer+Renderer | |
| Application cursor keys | ?1 | Impacts input mapping | ⚠ Partial | Unit/Code: `ReplayV2Tests`, app input paths | Parser+Input | Parser/UI wiring exists; needs targeted key-mapping tests |
| Focus event reporting | ?1004 | Emits `CSI I` / `CSI O` on focus transitions | ⚠ Partial | Unit/Code: `DecModeTests`, `TerminalView` | Parser+Input | Mode flag tested; focus emission covered by app path, not headless UI test |
| Bracketed paste | ?2004 | Input feature | ⚠ Partial | Unit | Input layer | |
| Mouse reporting | ?1000/1002/1003/1006 etc | | ⚠ Partial | Manual/Unit | Input layer | |
| Cursor style | CSI Ps SP q | DECSCUSR block/beam/underline + blink state | ✅ Supported | Unit: `tests/NovaTerminal.Tests/OscUxTests.cs` | Parser+Renderer | |

---

## 6) SGR attributes & colors

| Feature | CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| Basic SGR (0,1,2,3,4,5,7,9,22,23,24,25,27,29) | | Bold/dim/italic/underline/blink/reverse/strike | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/SgrAttributeTests.cs`; VTTEST: sgr scenario | Parser+Buffer+Renderer | Underline style/color tracked separately |
| 8/16 colors | 30–37/90–97, 40–47/100–107 | | ⚠ Partial | Replay | Parser+Buffer | |
| 256-color | 38;5;N / 48;5;N | | ⚠ Partial | Replay | Parser+Buffer | |
| Truecolor | 38;2;r;g;b / 48;2;r;g;b | | ⚠ Partial | Replay | Parser+Buffer | |
| Underline styles | 4:1.. | xterm | ❌ Not supported | — | Buffer | |

---

## 7) Tabs

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| HT (tab) movement | | ✅ Supported | Unit: `tests/NovaTerminal.Tests/TabSystemTests.cs` | Parser+Buffer | Custom tab stops are clipped on width shrink; columns exposed by width growth start with default 8-column tab stops |
| Tab stops set/clear | ESC H, CSI g | ✅ Supported | Unit: `tests/NovaTerminal.Tests/TabSystemTests.cs` | Parser+Buffer | `CSI g` supports current-stop clear (`0`/default) and clear-all (`3`); other parameters are ignored |

---

## 8) OSC sequences

| OSC | Purpose | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| OSC 0/2 | Set title | ⚠ Partial | Manual/Unit | App/UI | |
| OSC 7 | CWD reporting | ✅ Supported | Unit: `tests/NovaTerminal.Tests/OscUxTests.cs` | Parser+App | |
| OSC 52 | Clipboard | ❌ Not supported | — | App/UI | |
| OSC 8 | Hyperlinks | ✅ Supported | Unit: `tests/NovaTerminal.Tests/OscUxTests.cs` | Parser+Renderer+UI | Ctrl-click open path is app-level |
| OSC 133 | Shell integration lifecycle | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/OscShellIntegrationTests.cs` | Parser+Command Assist | Supports A/B/C/D markers; broader semantic prompt extensions not audited |
| OSC 1337 | iTerm2 inline images | ✅ Supported | Replay/Manual | Parser+Renderer | |
| OSC 1339 | Windows conpty tunnel | 🧪 Experimental | Manual | Parser+Win | |

---

## 9) Kitty graphics / APC

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| Kitty graphics protocol | APC / OSC forms | ✅ Supported | Manual/Replay | Parser+Renderer | |
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
| Hyperlinks | OSC 8 | ✅ Supported | Unit: `tests/NovaTerminal.Tests/OscUxTests.cs` | Parser+UI | Ctrl-click open path is app-level |

---

## 12) Unicode width, graphemes, and font behavior

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| wcwidth-like width | CJK/emoji width | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/WidthTests.cs`, `tests/NovaTerminal.Tests/UnicodeWidthModelV2Tests.cs`; Replay: `tests/NovaTerminal.Tests/Fixtures/Replay/mixed_unicode.rec` | Buffer+Renderer | Deterministic 0/1/2-cell model for combining marks, emoji modifiers, ZWJ emoji, variation selectors, and regional-indicator flags. No Unicode-version pin or full UAX #11 conformance table is documented yet. |
| Combining marks | Grapheme clusters | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/GraphemeAttachmentTests.cs`, `tests/NovaTerminal.Tests/SurrogateTests.cs`, `tests/NovaTerminal.Tests/UnicodeWidthModelV2Tests.cs`, `tests/NovaTerminal.Tests/ScrollAndWrapCorrectnessTests.cs` | Buffer+Renderer | Combining/variation attachment is covered for common terminal cases, including pending-wrap boundaries. Full extended-grapheme-cluster conformance is not claimed. |
| ZWJ emoji sequences | | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/GraphemeAttachmentTests.cs`, `tests/NovaTerminal.Tests/WidthTests.cs`, `tests/NovaTerminal.Tests/UnicodeWidthModelV2Tests.cs` | Buffer+Renderer | Chunked ZWJ families, emoji modifiers, VS15/VS16, and chunked regional-indicator flag pairs are covered. Remaining gaps: cursor-addressing CSI remains cell-oriented, and width-changing selectors that arrive after a base glyph already placed at the last column are not guaranteed to retroactively reflow. |

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
