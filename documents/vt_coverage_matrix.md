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
| C0 controls (BEL, BS, HT, LF, CR) | Basic control chars | ⚠ Partial | Replay: `tests/Replays/...` | `Core/AnsiParser.cs`, `Core/TerminalBuffer.cs` | (fill) |
| C1 via 7-bit ESC (ESC @.._) | “7-bit C1” translation | ❌ Not supported | — | `Core/AnsiParser.cs` | (fill) |
| 8-bit C1 bytes (0x80–0x9F) | If supported, must be explicit | ❌ Not supported | — | `Core/AnsiParser.cs` | (fill) |
| String terminators (ST = ESC \\, BEL) | OSC/APC termination rules | ⚠ Partial | Unit/Replay | `Core/AnsiParser.cs` | (fill) |
| Unknown sequence handling | Ignore/print/strict? | ⚠ Partial | Unit | `Core/AnsiParser.cs` | (fill) |
| Error recovery on malformed sequences | Robustness | ⚠ Partial | Fuzz/Unit | `Core/AnsiParser.cs` | (fill) |

---

## 2) Cursor movement & positioning (CSI)

| Feature / CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| CUU/CUD/CUF/CUB (A/B/C/D) | Cursor up/down/forward/back | ✅ Supported | Replay: `...` | Parser+Buffer | |
| CUP / HVP (H/f) | Positioning, default params | ⚠ Partial | VTTEST: cursor scenario | Parser+Buffer | Default param edge cases |
| CHA/CPL/CNL (G/F/E) | Horizontal absolute / prev/next line | ⚠ Partial | Replay | Parser+Buffer | |
| CHT (I) | Cursor forward tabulation | ❌ Not supported | — | Parser+Buffer | |
| CBT (Z) | Cursor backward tabulation | ❌ Not supported | — | Parser+Buffer | |
| VPA/HPA (d/G) | Absolute row/col | ⚠ Partial | Unit | Parser+Buffer | |
| HPR/VPR (a/e) | Relative row/col | ⚠ Partial | Unit | Parser+Buffer | |

---

## 3) Erase & insert/delete (CSI)

| Feature / CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| ED (J) | 0/1/2 erase display | ✅ Supported | Replay | Parser+Buffer | |
| EL (K) | 0/1/2 erase line | ✅ Supported | Replay | Parser+Buffer | |
| ICH ( @ ) | Insert chars | ❌ Not supported | — | Buffer | |
| DCH (P) | Delete chars | ❌ Not supported | — | Buffer | |
| IL (L) / DL (M) | Insert/delete lines | ⚠ Partial | Replay | Buffer | Scroll region interactions |
| ECH (X) | Erase chars | ❌ Not supported | — | Buffer | |

---

## 4) Scrolling, margins, and origin mode

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| DECSTBM (CSI t;b r) | Set top/bottom margins | ⚠ Partial | VTTEST: scroll scenario | Parser+Buffer | |
| IND (ESC D) / RI (ESC M) | Index / Reverse index | ⚠ Partial | Replay | Parser+Buffer | |
| DECOM (origin mode) | Cursor relative to margins | ❌ Not supported | — | Buffer | |
| Wraparound DECAWM | Auto wrap | ⚠ Partial | Replay | Buffer | Wide glyph edge cases |
| Smooth scroll | Not required for correctness | 🚫 Won’t support | — | — | Renderer concern |

---

## 5) Screen buffers & modes (DEC private modes)

| Mode | CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---|---:|---|---|---|
| Alternate screen | ?1049 / ?47 / ?1047 | Switch + save/restore cursor | ⚠ Partial | Replay: `AlternateScreenTests` | Buffer | |
| Show cursor | ?25 | | ✅ Supported | Unit/Replay | Buffer+Renderer | |
| Application cursor keys | ?1 | Impacts input mapping | ❌ Not supported | — | Input layer | |
| Bracketed paste | ?2004 | Input feature | ⚠ Partial | Unit | Input layer | |
| Mouse reporting | ?1000/1002/1003/1006 etc | | ⚠ Partial | Manual/Unit | Input layer | |

---

## 6) SGR attributes & colors

| Feature | CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| Basic SGR (0,1,2,4,7,22,24,27) | | Bold/dim/underline/reverse | ⚠ Partial | VTTEST: sgr scenario | Parser+Buffer | |
| 8/16 colors | 30–37/90–97, 40–47/100–107 | | ⚠ Partial | Replay | Parser+Buffer | |
| 256-color | 38;5;N / 48;5;N | | ⚠ Partial | Replay | Parser+Buffer | |
| Truecolor | 38;2;r;g;b / 48;2;r;g;b | | ⚠ Partial | Replay | Parser+Buffer | |
| Underline styles | 4:1.. | xterm | ❌ Not supported | — | Buffer | |

---

## 7) Tabs

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| HT (tab) movement | | ⚠ Partial | Unit | Buffer | |
| Tab stops set/clear | ESC H, CSI g | ❌ Not supported | — | Buffer | |

---

## 8) OSC sequences

| OSC | Purpose | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| OSC 0/2 | Set title | ⚠ Partial | Manual/Unit | App/UI | |
| OSC 7 | CWD reporting | ❌ Not supported | — | App/UI | |
| OSC 52 | Clipboard | ❌ Not supported | — | App/UI | |
| OSC 8 | Hyperlinks | ❌ Not supported | — | Renderer/UI | |
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
| Hyperlinks | OSC 8 | ❌ Not supported | — | UI | |

---

## 12) Unicode width, graphemes, and font behavior

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| wcwidth-like width | CJK/emoji width | ⚠ Partial | Replay | Buffer | |
| Combining marks | Grapheme clusters | ❌ Not supported | — | Buffer | |
| ZWJ emoji sequences | | ❌ Not supported | — | Buffer | |

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
