NovaTerminal vs Ghostty --- refreshed status
------------------------------------------

### The short version

-   **Core VT correctness**: NovaTerminal has closed most of the high-impact gaps (parser recovery, scroll/margins, alt-screen, input modes, tabs).
-   **Unicode behavior**: now competitive thanks to a shared width model across storage + renderer.
-   **Verification story**: NovaTerminal is now ahead with an auditable matrix + CI validation.
-   **Remaining gaps**: some sequence families still **⚠ Partial** (e.g., full directional cursor family, OSC 1337 coverage breadth, deeper Unicode conformance tables).

* * * * *

### Where NovaTerminal clearly improved

**1) Parser robustness (PR1)**

-   Deterministic recovery from malformed ESC/CSI/OSC.
-   No more "print junk on error" class of bugs.
-   Evidence-backed and fuzz/regression covered.

**2) Scrolling, margins, wrap (PR3)**

-   Correct DECSTBM handling (including invalid-region preservation).
-   IND/RI scoped to active margins.
-   Right-edge + wide glyph + combining behavior fixed.

**3) Alternate screen (PR4)**

-   Clean separation of main vs alt live state.
-   Correct semantics for `?47`, `?1047`, `?1049`.
-   Deterministic shell → app → shell transitions.

**4) Input modes (PR5)**

-   Unified encoder for keys, paste, focus, mouse.
-   Bracketed paste (`?2004`), focus (`?1004`), app cursor (`?1`) behave consistently.
-   Modern mouse (SGR 1006) solid.

**5) Cursor positioning (PR2)**

-   Correct defaults (omitted/0 → 1) for CUP/HVP/HPA/VPA/HPR/VPR.
-   Consistent clamping + DECOM interactions.

**6) Tabs (PR6)**

-   Real tab-stop model (8-col defaults).
-   HT/CHT/CBT/HTS/TBC implemented.
-   Deterministic reset + resize behavior.

**7) Unicode width model (PR7)**

-   **Single shared classifier** for buffer + renderer (no drift).
-   Correct handling for ZWJ families, emoji modifiers, RI flags, VS15/VS16.
-   Backspace operates on grapheme start, not continuation cells.

**8) Conformance tooling (PR8 + PR8.1)**

-   Machine-readable JSON report with SHA and row-level metadata.
-   CI validation rules (no unsupported "✅").
-   **0 errors / 0 warnings** after evidence tightening.

* * * * *

### Where Ghostty still likely leads

**Breadth and maturity**

-   Wider long-tail coverage of obscure CSI/OSC variants.
-   More exhaustive Unicode conformance (pinned tables, edge scripts).

**Deep compatibility polish**

-   Fewer corner-case deviations across mixed sequences and legacy apps.
-   Likely stronger coverage for rarely used modes and historical quirks.

**Ecosystem surface**

-   Embeddable core (`libghostty`) and broader integration story.

* * * * *

### Where NovaTerminal now leads

**1) Auditable correctness**

-   Every "✅ Supported" has **traceable evidence** (tests/replays/paths).
-   Violations are caught automatically in CI.

**2) Deterministic validation**

-   Replay + snapshot model + matrix → **provable behavior**, not claims.

**3) Storage/renderer alignment**

-   Shared width model removes a classic class of terminal bugs.

**4) Engineering clarity**

-   Explicit deviations documented per row.
-   Easier to reason about, maintain, and extend.

* * * * *

### Honest remaining gaps (from the matrix)

-   Some sequence families still **⚠ Partial** (e.g., full CUU/CUD/CUF/CUB coverage).
-   **OSC 1337**: parser support exists, but automated evidence breadth is limited.
-   Unicode:
    -   No pinned UAX #11 / #29 tables yet.
    -   No guaranteed retroactive reflow for late width-changing selectors.
-   Legacy mouse modes beyond SGR 1006 remain partial.

* * * * *

### Bottom line

-   **Ghostty**: broader, mature, battle-tested compatibility.
-   **NovaTerminal**: now *structurally correct*, test-driven, and **provably verifiable**.

That's a credible position---and a strong foundation to surpass on correctness over time.