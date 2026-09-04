# Bundled fonts: JetBrains Mono NL default + symbols-only Nerd Font fallback

_Date: 2026-09-04 · Supersedes the single-font arrangement in
`2026-04-29-bundled-cascadia-mono-pl-design.md` (which stays accurate about why
bundling at all, and about the asset/loader mechanics reused here)._

## Problem

`Cascadia Mono PL` shipped as the one bundled font and the default. It covers
ASCII, box drawing and powerline glyphs — verified directly:

| Font | ASCII | Box drawing (U+2502) | Powerline (U+E0B0, U+E0A0) | Icons (U+F09B, U+E5FF) |
|---|---|---|---|---|
| Cascadia Mono PL (bundled) | yes | yes | yes | **no** |
| JetBrains Mono NL | yes | yes | yes | **no** |
| Symbols Nerd Font Mono | **no** | no | yes | yes |

So the gap a fresh install has is the **icon set** — the dev, file-type and brand
glyphs that prompts like starship and oh-my-posh draw. Powerline arrows already
worked; that was never the missing piece.

## What other terminals do

Checked against the actual contents of their repositories rather than their docs:

| Terminal | Bundles | Icon strategy |
|---|---|---|
| **Ghostty** (`src/font/res`) | `JetBrainsMonoNerdFont-{Regular,Bold,Italic,BoldItalic}.ttf`, ~2.23 MB each (~8.9 MB), plus an unpatched `JetBrainsMonoNoNF-Regular.ttf` | fully patched font per style |
| **WezTerm** (`assets/fonts`) | JetBrains Mono unpatched (18 faces, ~270 KB each) plus `SymbolsNerdFontMono-Regular.ttf` (2,278 KB) | **symbols-only font as fallback** |
| **Alacritty** | nothing | system fonts |
| **Windows Terminal** | nothing in-repo (0 `.ttf`, 0 `.otf`) | Cascadia installed with the product |

Two of the four bundle a font at all, and both chose JetBrains Mono.

## Decision

Follow WezTerm. Bundle three fonts:

- **`JetBrains Mono NL`** (204 KB) — new default terminal face. NL is the
  no-ligatures cut, which suits a terminal grid.
- **`Cascadia Mono PL`** (360 KB) — kept. It stops being the default but stays
  bundled, because every `settings.json` written before this change names it;
  dropping it would silently move those users to another face.
- **`Symbols Nerd Font Mono`** (2,549 KB) — icon glyphs, loaded as a *fallback*.

Total added: **2,753 KB**.

### Why the symbols font rather than a patched face

Cost is near-identical — a patched `JetBrainsMonoNLNerdFontMono-Regular.ttf` is
2,432 KB against 204 + 2,549 = 2,753 KB here — but what it buys differs:

- Icons work under **whichever face the user picks**, including Cascadia or a
  system font, not only while the patched font is selected.
- The icon cost is paid **once**, not per face and per weight. This is what makes
  Ghostty's arrangement cost 8.9 MB across four styles.
- NovaTerminal already has the mechanism: `TerminalView.FallbackChainNames` feeds
  a chain the draw operation consults for glyphs the primary face lacks. The
  symbols font is simply the first entry, and the only one guaranteed present.

### Constraint the symbols font imposes

It has **no ASCII**. If it were ever selected as a primary face the terminal would
render nothing, so it is fallback-only by construction:
`BundledFontCatalog.SelectableFamilies` excludes it, which keeps it out of both
the Avalonia family mappings (where Avalonia's own fallback could otherwise reach
it and blank out UI text) and the settings font picker. A test asserts both, and
another asserts it still contains no ASCII — if a future font swap broke that
pairing, icon glyphs would silently become notdef boxes.

## Consequences

- **The settings picker now offers every selectable bundled family**, not just the
  default. Without that change, Cascadia Mono PL would have shipped in the binary
  and been unpickable, since it is not installed system-wide on most machines.
- **Existing users keep their font.** A stored `"FontFamily": "Cascadia Mono PL"`
  still resolves to a bundled asset. Only fresh installs get JetBrains Mono NL.
- **Bold and italic are unchanged, and remain synthesized.** Bundling real Bold
  and Italic faces was considered and rejected as dead weight: the renderer
  synthesizes italic with `canvas.Skew(-0.22f, 0f)` and **ignores bold entirely** —
  `RenderCellSnapshot.IsBold` is populated and nothing in `TerminalDrawOperation`
  or `TerminalView` reads it, which is consistent with the ⚠ Partial marking on
  the Basic SGR row of `docs/vt_coverage_matrix.md`. Real weights need per-run font
  selection in the renderer first; that work would also make SGR 1 visible for the
  first time and is tracked separately.
- **Licensing.** Both additions are OFL 1.1, with their license texts in
  `Assets/Fonts/LICENSES/`, matching what Cascadia already does.

## Provenance

Downloaded from upstream releases rather than copied off a developer machine:

- `JetBrainsMonoNL-Regular.ttf` — JetBrains/JetBrainsMono **v2.304**
- `SymbolsNerdFontMono-Regular.ttf` — ryanoasis/nerd-fonts **v3.5.1**
  (`NerdFontsSymbolsOnly`)
