# NovaTerminal Memory Analysis

_Generated: 2026-03-05_

---

## 1. Object Sizes

### TerminalCell (struct, `TerminalBuffer.cs` / `TerminalCell.cs`)

| Field | Type | Size |
|-------|------|------|
| `Character` | `char` | 2 B |
| `Flags` | `ushort` | 2 B |
| `Fg` | `uint` | 4 B |
| `Bg` | `uint` | 4 B |
| **Total** | | **12 B** |

`TerminalCell` is a value type laid out sequentially. No hidden padding needed (all fields naturally aligned). ✅

### TerminalRow (class, `TerminalRow.cs`)

| Component | Size |
|-----------|------|
| Object header (sync block + type pointer) | 16 B |
| `Id` (`long`) | 8 B |
| `Cells` array reference | 8 B |
| `IsWrapped` (`bool`) | 1 B (padded to 4 B) |
| `Revision` (`uint`) | 4 B |
| `_extendedText` ref (`Dictionary?`) | 8 B |
| `_hyperlinks` ref (`Dictionary?`) | 8 B |
| **Shell overhead** | **~48 B** |
| `TerminalCell[]` array (cols × 12 B + array header ~24 B) | 24 + cols×12 B |

For a 220-column terminal, one `TerminalRow`:
- 48 B (object) + 24 B (array header) + 220 × 12 = **2,712 B ≈ 2.7 KB**

### RenderCellSnapshot (struct, `RenderSnapshots.cs`)

| Field | Type | Size |
|-------|------|------|
| `Character` | `char` | 2 B |
| `Text` | `string?` (reference) | 8 B |
| `Foreground`, `Background` | `TermColor` × 2 | ~8 B each |
| 12 × bool flags | `bool` × 12 | 12 B |
| `FgIndex`, `BgIndex` | `short` × 2 | 4 B |
| **Total** | | **~42 B** (padded to ~48 B) |

`RenderCellSnapshot` is significantly larger than `TerminalCell` (4× expansion) because it unpacks all the flags into individual bools and includes a managed object reference.

---

## 2. Memory Consumed by Key Structures

### Viewport (e.g., 50 rows × 220 cols)
- 50 `TerminalRow` objects = 50 × 2,712 B ≈ **133 KB**

### Alternate Screen Buffer (same dimensions)
- Always allocated alongside viewport: another **133 KB**

### Scrollback (10,000 lines × 220 cols)
- `CircularBuffer<TerminalRow>` backing array: 10,000 × 8 B (references) = 80 KB
- 10,000 `TerminalRow` objects: 10,000 × 2,712 B ≈ **27.1 MB**

### Main Screen Scrollback (separate `CircularBuffer`)
- Another 10,000-slot `CircularBuffer` allocated at construction even though AltScreen rarely fills it: **~27 MB peak**, same structure

### RenderRowSnapshot[] (per render frame)
- `PooledArray<RenderRowSnapshot>` from `ArrayPool` — reused ✅
- But `RenderRowSnapshot.Cells = new RenderCellSnapshot[cols]` is allocated **per row that changed**
  - Each newly-populated row: 220 × 48 B ≈ **10.5 KB**
  - For a full-screen repaint: 50 × 10.5 KB ≈ **525 KB per frame**

### GlyphAtlas (2 × 1024² RGBA8888 surfaces)
- Each surface: 1024 × 1024 × 4 = **4 MB**
- Two surfaces (alpha + color): **8 MB** pinned in GPU/Skia memory

### GlyphCache `_entries` dictionary
- One `CacheEntry` per unique (text, font, size, skew, scale) combination
- Key string allocated on each `GetOrAdd` call that misses: `$"{text}|{family}|{size}|{skew}|{scale}"`

---

## 3. Allocation Hotspots (GC Pressure Sources)

### 🔴 Critical

| # | Location | Allocation | Frequency |
|---|----------|------------|-----------|
| 1 | `ScrollUpInternal()` | `new TerminalRow(Cols, ...)` — a new heap object per scroll line | Every LF when buffer full |
| 2 | `InsertLines()` / `DeleteLines()` | `new TerminalRow(Cols, ...)` per inserted/deleted line | On every IL/DL escape |
| 3 | `GetRowSnapshot()` / `PopulateRenderCellsFromRow_NoLock()` | `new RenderCellSnapshot[bufferCols]` per dirty row | Every render frame |
| 4 | `WriteGraphemeInternal()` (surrogate pairs) | `new string(new[] { hi, lo })` per surrogate pair | Many times/sec during output |
| 5 | `WriteContentCore()` | `StringInfo.GetTextElementEnumerator(text)` alloc per call | On every VT escape handler dispatch |

### 🟡 Moderate

| # | Location | Allocation | Frequency |
|---|----------|------------|-----------|
| 6 | `GlyphCache.GetOrAdd()` | Interpolated key string `$"{text}|..."` on every cache call | Per unique rendered glyph |
| 7 | `GlyphCache.GetAtlasImages()` | `SKImage` snapshot via `_atlas.GenerateAlphaImage()` | On every glyph cache change |
| 8 | `TerminalRow._extendedText` | Lazy `new Dictionary<int,string>()` per row with extended graphemes | Whenever emoji/CJK written |
| 9 | `FindMatches()` | `new StringBuilder()`, `new List<int>()` per row during search | On each search query |
| 10 | `GetVisibleImagesSnapshot()` | `new List<RenderImageSnapshot>()` per frame | Every render |

---

## 4. Long-Lived Memory Structures

| Structure | Estimated Size | Lifetime |
|-----------|---------------|----------|
| `_scrollback` (main) | up to 27 MB | Session lifetime |
| `_mainScreenScrollback` | up to 27 MB | Session lifetime (even fully unused during AltScreen) |
| `_altScreen` rows | ~133 KB | Session lifetime (always allocated) |
| `GlyphAtlas` two SKSurfaces | 8 MB | Session lifetime (reset on full, not freed) |
| `GlyphCache._entries` dictionary | Variable (unbounded growth within atlas capacity) | Until atlas reset |

---

## 5. Estimated Memory Per Terminal Line

```
Per TerminalRow (220 cols, typical terminal):
  TerminalRow object overhead:  48 B
  TerminalCell[] array header:  24 B
  220 × TerminalCell:        2,640 B
  Extended text dict (empty):    0 B (null)
  Hyperlinks dict (empty):       0 B (null)
  ────────────────────────────────────
  Total:                     2,712 B ≈ 2.7 KB/line
```

```
10,000 scrollback lines:
  TerminalRow objects:        2,712 × 10,000 = 27.1 MB
  CircularBuffer<> backing:       8 × 10,000 =   80 KB
  ──────────────────────────────────────────────────
  Total:                               ≈ 27.2 MB
```

---

## 6. Top 10 Optimisations (Ranked by Impact)

| Rank | Optimisation | Estimated Saving | Complexity |
|------|-------------|-----------------|------------|
| **1** | **Segment/page-based scrollback** — store scrollback as fixed-size pages of `TerminalCell` values (not boxed `TerminalRow` objects). Converts 10,000 heap objects into one flat array per page. | **~25 MB** (eliminates per-row heap overhead + GC enumeration) | Medium |
| **2** | **Pool `TerminalRow` objects** — instead of `new TerminalRow(Cols, …)` on every scroll/insert, return rows to an `ObjectPool<TerminalRow>` and reset in-place. | **~Eliminate scroll GC churn** | Low |
| **3** | **Pool `RenderCellSnapshot[]` arrays** — `GetRowSnapshot` allocates a fresh `RenderCellSnapshot[cols]` per dirty row. Use `ArrayPool<RenderCellSnapshot>.Shared`. | **~500 KB per-frame pressure eliminated** | Low |
| **4** | **Collapse `_mainScreenScrollback` allocation** — `_mainScreenScrollback` is allocated at `MaxHistory = 10,000` slots at construction even though it is only ever used when AltScreen is active (and is never filled to capacity in typical use). Defer allocation or shrink default. | **~27 MB worst-case** | Low |
| **5** | **Intern/pool GlyphCache key strings** — instead of `$"{text}|{family}|…"` allocation per cache lookup, use a `ValueTuple` struct key or pre-allocated key buffer. | **Eliminate key alloc on every rendered glyph** | Low |
| **6** | **Convert `RenderCellSnapshot` fields from bool to packed ushort flags** — 12 individual `bool` fields waste ~10 B each due to alignment. Pack into a `ushort` (same as `TerminalCell.Flags`) to shrink from ~48 B to ~24 B. | **~50% snapshot memory reduction** | Low |
| **7** | **Lazy / sparse alt-screen buffer** — the alt-screen is always pre-allocated (`Rows × new TerminalRow`). Only allocate rows on demand; keep a single blank-row singleton for unwritten rows. | **~100 KB** | Medium |
| **8** | **Reduce `TerminalRow` dictionary pressure** — `_extendedText` and `_hyperlinks` use `Dictionary<int,string>` which has high per-instance overhead (~176 B minimum). Replace with a small sorted array for the common case of ≤ 4 extended cells per row. | **Per-row overhead for emoji-heavy content** | Medium |
| **9** | **GlyphAtlas LRU eviction instead of full reset** — the current strategy clears the whole atlas when it overflows. An LRU shelf eviction would reduce `SKImage.Snapshot()` re-generation (each snapshot = 4 MB GPU→CPU copy). | **Reduce snapshot churn** | High |
| **10** | **StringBuilder pooling in `FindMatches()`** — search allocates a new `StringBuilder` and `List<int>` colMapping per row. Use `ArrayPool` / `StringBuilderPool` to amortize these. | **Low absolute bytes, high frequency** | Low |
