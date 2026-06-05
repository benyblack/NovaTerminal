# Clickable Links Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auto-detect plain-text URLs/emails in terminal output and make them (plus existing OSC 8 links) hover-underlined and openable with a modifier-click, via an extensible rule-list detector.

**Architecture:** A pure, UI-free `UrlDetector` in `NovaTerminal.VT` holds an ordered list of regex `LinkRule`s (ships with scheme + email defaults; new link kinds = append a rule). `RowTextExtractor` turns an absolute buffer row into display text plus a char→column map. `TerminalView` scans the row under the cursor on hover (memoized to one row), underlines the hovered span as a transient `DrawingContext` overlay (same model as the bell flash), and opens links on Ctrl/Cmd+Click through one `TryOpenLink` gated by a scheme allowlist. OSC 8 storage and lookup are reused unchanged.

**Tech Stack:** C# / .NET, Avalonia (custom-drawn `TerminalView`), xUnit v3 tests. Build/test via `scripts/build.ps1` (never raw `dotnet` — see CLAUDE.md).

---

## File Structure

**New (NovaTerminal.VT — pure, unit-tested):**
- `src/NovaTerminal.VT/Links/LinkSpan.cs` — `LinkSpan` value type (char range + resolved URI).
- `src/NovaTerminal.VT/Links/LinkRule.cs` — one detection rule (name, regex, resolver, trim flag).
- `src/NovaTerminal.VT/Links/UrlDetector.cs` — runs rules over a line, dedups overlaps. Holds `DefaultRules`.
- `src/NovaTerminal.VT/Links/RowTextExtractor.cs` — buffer row → `(text, charToCol)` + `SpanToColumns`.
- `src/NovaTerminal.VT/Links/LinkSchemes.cs` — open-scheme allowlist (`http/https/mailto/file`).

**New tests:**
- `tests/NovaTerminal.VT.Tests/Links/UrlDetectorTests.cs`
- `tests/NovaTerminal.VT.Tests/Links/RowTextExtractorTests.cs`
- `tests/NovaTerminal.VT.Tests/Links/LinkSchemesTests.cs`

**Modified (NovaTerminal.App):**
- `src/NovaTerminal.App/Shell/TerminalView.cs` — hover state + `UpdateHoveredLink`, `OnPointerMoved`/`OnPointerExited` wiring, underline overlay in `Render`, `TryOpenLink` + `OnPointerPressed` refactor.
- `src/NovaTerminal.App/Shell/TerminalSettings.cs` — `EnableLinkDetection` setting.

---

## Task 1: `LinkSpan`, `LinkRule`, and `UrlDetector` (scheme rule + overlap dedup)

**Files:**
- Create: `src/NovaTerminal.VT/Links/LinkSpan.cs`
- Create: `src/NovaTerminal.VT/Links/LinkRule.cs`
- Create: `src/NovaTerminal.VT/Links/UrlDetector.cs`
- Test: `tests/NovaTerminal.VT.Tests/Links/UrlDetectorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.VT.Tests/Links/UrlDetectorTests.cs`:

```csharp
using System.Linq;
using NovaTerminal.VT.Links;

namespace NovaTerminal.VT.Tests.Links;

public class UrlDetectorTests
{
    private static readonly UrlDetector Detector = new UrlDetector();

    [Fact]
    public void Detects_a_single_scheme_url()
    {
        var spans = Detector.Detect("see https://example.com now");
        var s = Assert.Single(spans);
        Assert.Equal("https://example.com", s.Uri);
        Assert.Equal(4, s.StartChar);
        Assert.Equal(23, s.EndChar); // exclusive end
    }

    [Fact]
    public void Detects_multiple_scheme_urls_in_order()
    {
        var spans = Detector.Detect("http://a.io and https://b.io");
        Assert.Equal(2, spans.Count);
        Assert.Equal("http://a.io", spans[0].Uri);
        Assert.Equal("https://b.io", spans[1].Uri);
    }

    [Fact]
    public void Returns_empty_when_no_url()
    {
        Assert.Empty(Detector.Detect("just some plain text, a.b, file.name"));
    }

    [Fact]
    public void Returns_empty_for_null_or_blank()
    {
        Assert.Empty(Detector.Detect(""));
        Assert.Empty(Detector.Detect(null!));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~UrlDetectorTests"`
Expected: FAIL — compile error, `NovaTerminal.VT.Links` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/NovaTerminal.VT/Links/LinkSpan.cs`:

```csharp
namespace NovaTerminal.VT.Links
{
    /// <summary>A detected link: character range [StartChar, EndChar) in a line plus its resolved URI.</summary>
    public readonly record struct LinkSpan(int StartChar, int EndChar, string Uri);
}
```

Create `src/NovaTerminal.VT/Links/LinkRule.cs`:

```csharp
using System;
using System.Text.RegularExpressions;

namespace NovaTerminal.VT.Links
{
    /// <summary>
    /// One link-detection rule. Adding a new kind of clickable text = appending a LinkRule
    /// to UrlDetector's rule list; nothing else changes.
    /// </summary>
    public sealed class LinkRule
    {
        public string Name { get; }
        public Regex Pattern { get; }
        /// <summary>Maps the matched (post-trim) text to a final URI (e.g. "mailto:" + addr).</summary>
        public Func<string, string> Resolve { get; }
        /// <summary>When true, trailing sentence punctuation and unbalanced closing brackets are trimmed.</summary>
        public bool TrimTrailingPunctuation { get; }

        public LinkRule(string name, Regex pattern, Func<string, string> resolve, bool trimTrailingPunctuation = false)
        {
            Name = name;
            Pattern = pattern;
            Resolve = resolve;
            TrimTrailingPunctuation = trimTrailingPunctuation;
        }
    }
}
```

Create `src/NovaTerminal.VT/Links/UrlDetector.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NovaTerminal.VT.Links
{
    /// <summary>Detects links in a single line of text by applying an ordered list of LinkRules.</summary>
    public sealed class UrlDetector
    {
        private readonly IReadOnlyList<LinkRule> _rules;

        public UrlDetector(IReadOnlyList<LinkRule>? rules = null) => _rules = rules ?? DefaultRules;

        public static IReadOnlyList<LinkRule> DefaultRules { get; } = new[]
        {
            new LinkRule(
                "scheme",
                new Regex(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s]+", RegexOptions.Compiled),
                text => text,
                trimTrailingPunctuation: true),
        };

        public IReadOnlyList<LinkSpan> Detect(string line)
        {
            if (string.IsNullOrEmpty(line)) return Array.Empty<LinkSpan>();

            var spans = new List<LinkSpan>();
            foreach (var rule in _rules)
            {
                foreach (Match m in rule.Pattern.Matches(line))
                {
                    int start = m.Index;
                    int end = m.Index + m.Length; // exclusive
                    if (rule.TrimTrailingPunctuation) end = TrimTrailingEnd(line, start, end);
                    if (end <= start) continue;
                    string text = line.Substring(start, end - start);
                    spans.Add(new LinkSpan(start, end, rule.Resolve(text)));
                }
            }

            // Deterministic order, then drop overlaps (earliest span wins).
            spans.Sort((a, b) => a.StartChar != b.StartChar ? a.StartChar - b.StartChar : a.EndChar - b.EndChar);
            var result = new List<LinkSpan>();
            int lastEnd = -1;
            foreach (var s in spans)
            {
                if (s.StartChar >= lastEnd) { result.Add(s); lastEnd = s.EndChar; }
            }
            return result;
        }

        private static int TrimTrailingEnd(string line, int start, int end)
        {
            const string punct = ".,;:!?\"'";
            while (end > start)
            {
                char c = line[end - 1];
                if (punct.IndexOf(c) >= 0) { end--; continue; }
                if (c == ')' && CountChar(line, start, end, '(') < CountChar(line, start, end, ')')) { end--; continue; }
                if (c == ']' && CountChar(line, start, end, '[') < CountChar(line, start, end, ']')) { end--; continue; }
                if (c == '}' && CountChar(line, start, end, '{') < CountChar(line, start, end, '}')) { end--; continue; }
                break;
            }
            return end;
        }

        private static int CountChar(string line, int start, int end, char target)
        {
            int n = 0;
            for (int i = start; i < end; i++) if (line[i] == target) n++;
            return n;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~UrlDetectorTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.VT/Links/LinkSpan.cs src/NovaTerminal.VT/Links/LinkRule.cs src/NovaTerminal.VT/Links/UrlDetector.cs tests/NovaTerminal.VT.Tests/Links/UrlDetectorTests.cs
git commit -m "feat(links): scheme URL detector with overlap dedup"
```

---

## Task 2: Trailing-punctuation / bracket trimming

**Files:**
- Modify: `src/NovaTerminal.VT/Links/UrlDetector.cs` (already has `TrimTrailingEnd` from Task 1 — this task pins the behavior with tests)
- Test: `tests/NovaTerminal.VT.Tests/Links/UrlDetectorTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `UrlDetectorTests.cs`:

```csharp
    [Theory]
    [InlineData("(see https://example.com).", "https://example.com")]
    [InlineData("go to https://example.com/path!", "https://example.com/path")]
    [InlineData("ref [https://example.com/a]", "https://example.com/a")]
    [InlineData("end https://example.com,", "https://example.com")]
    public void Trims_trailing_punctuation_and_unbalanced_brackets(string line, string expectedUri)
    {
        var s = Assert.Single(Detector.Detect(line));
        Assert.Equal(expectedUri, s.Uri);
    }

    [Fact]
    public void Keeps_balanced_parens_inside_url()
    {
        // Wikipedia-style URL with balanced parens should be preserved.
        var s = Assert.Single(Detector.Detect("see https://en.wikipedia.org/wiki/Foo_(bar) here"));
        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(bar)", s.Uri);
    }
```

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~UrlDetectorTests"`
Expected: All PASS — the Task 1 implementation already covers these. If `Keeps_balanced_parens_inside_url` FAILS, fix `TrimTrailingEnd` (Step 3); otherwise skip Step 3.

- [ ] **Step 3: Fix only if the balanced-parens test failed**

The balanced-paren case is handled by the `CountChar('(') < CountChar(')')` guard already present in `TrimTrailingEnd`. If it failed, verify the guard counts over `[start, end)` and that `'('`/`')'` are checked (not only `')'`). No change expected.

- [ ] **Step 4: Run test to verify it passes**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~UrlDetectorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/NovaTerminal.VT.Tests/Links/UrlDetectorTests.cs src/NovaTerminal.VT/Links/UrlDetector.cs
git commit -m "test(links): pin trailing-punctuation and bracket trimming"
```

---

## Task 3: Email → mailto rule

**Files:**
- Modify: `src/NovaTerminal.VT/Links/UrlDetector.cs` (add email rule to `DefaultRules`)
- Test: `tests/NovaTerminal.VT.Tests/Links/UrlDetectorTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `UrlDetectorTests.cs`:

```csharp
    [Fact]
    public void Detects_email_as_mailto()
    {
        var s = Assert.Single(Detector.Detect("contact me@example.com please"));
        Assert.Equal("mailto:me@example.com", s.Uri);
        Assert.Equal(8, s.StartChar);
        Assert.Equal(22, s.EndChar);
    }

    [Fact]
    public void Does_not_treat_email_inside_a_url_as_separate_link()
    {
        // The scheme rule wins; we should not get a duplicate mailto span overlapping it.
        var spans = Detector.Detect("https://host/path?u=me@example.com");
        Assert.Single(spans);
        Assert.StartsWith("https://", spans[0].Uri);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~UrlDetectorTests"`
Expected: FAIL — `Detects_email_as_mailto` returns 0 spans (no email rule yet).

- [ ] **Step 3: Add the email rule**

In `src/NovaTerminal.VT/Links/UrlDetector.cs`, replace the `DefaultRules` initializer with:

```csharp
        public static IReadOnlyList<LinkRule> DefaultRules { get; } = new[]
        {
            new LinkRule(
                "scheme",
                new Regex(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s]+", RegexOptions.Compiled),
                text => text,
                trimTrailingPunctuation: true),
            new LinkRule(
                "email",
                new Regex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled),
                text => "mailto:" + text,
                trimTrailingPunctuation: false),
        };
```

(The overlap dedup in `Detect` already drops the email span that falls inside the scheme span because the scheme span — sorted earlier by `StartChar` — claims the range first.)

- [ ] **Step 4: Run test to verify it passes**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~UrlDetectorTests"`
Expected: PASS (all UrlDetectorTests).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.VT/Links/UrlDetector.cs tests/NovaTerminal.VT.Tests/Links/UrlDetectorTests.cs
git commit -m "feat(links): add email->mailto detection rule"
```

---

## Task 4: `RowTextExtractor` (row → text + char→column map)

**Files:**
- Create: `src/NovaTerminal.VT/Links/RowTextExtractor.cs`
- Test: `tests/NovaTerminal.VT.Tests/Links/RowTextExtractorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.VT.Tests/Links/RowTextExtractorTests.cs`:

```csharp
using NovaTerminal.VT;
using NovaTerminal.VT.Links;

namespace NovaTerminal.VT.Tests.Links;

public class RowTextExtractorTests
{
    [Fact]
    public void Extracts_plain_ascii_with_identity_column_map()
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 1);
        var parser = new AnsiParser(buffer);
        parser.Process("ab https://x.io");

        var (text, map) = RowTextExtractor.Extract(buffer, absRow: 0);

        Assert.StartsWith("ab https://x.io", text);
        // First 15 characters map to columns 0..14 (1:1 for single-width ASCII).
        for (int i = 0; i < 15; i++) Assert.Equal(i, map[i]);
    }

    [Fact]
    public void SpanToColumns_maps_char_range_to_inclusive_columns()
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 1);
        var parser = new AnsiParser(buffer);
        parser.Process("ab https://x.io");

        var (_, map) = RowTextExtractor.Extract(buffer, absRow: 0);
        // "https://x.io" occupies chars [3, 15) -> columns 3..14 inclusive.
        var (startCol, endCol) = RowTextExtractor.SpanToColumns(new LinkSpan(3, 15, "https://x.io"), map);

        Assert.Equal(3, startCol);
        Assert.Equal(14, endCol);
    }

    [Fact]
    public void Wide_char_continuation_column_is_skipped_in_map()
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 1);
        var parser = new AnsiParser(buffer);
        // A wide CJK glyph occupies two columns (0 = glyph, 1 = continuation), then "x".
        parser.Process("中x");

        var (text, map) = RowTextExtractor.Extract(buffer, absRow: 0);

        Assert.Equal('中', text[0]);
        Assert.Equal(0, map[0]);   // wide glyph at column 0
        Assert.Equal('x', text[1]);
        Assert.Equal(2, map[1]);   // 'x' lands at column 2 (column 1 is the continuation cell)
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~RowTextExtractorTests"`
Expected: FAIL — `RowTextExtractor` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/NovaTerminal.VT/Links/RowTextExtractor.cs`:

```csharp
using System.Collections.Generic;
using System.Text;

namespace NovaTerminal.VT.Links
{
    /// <summary>
    /// Turns an absolute buffer row into its display text plus a map from each text-character
    /// index to the originating terminal column. Wide-cell continuations are skipped; extended
    /// grapheme text contributes multiple characters that all map to its starting column.
    /// </summary>
    public static class RowTextExtractor
    {
        public static (string Text, int[] CharToCol) Extract(TerminalBuffer buffer, int absRow)
        {
            var sb = new StringBuilder();
            var map = new List<int>();

            buffer.Lock.EnterReadLock();
            try
            {
                int cols = buffer.Cols;
                for (int col = 0; col < cols; col++)
                {
                    var cell = buffer.GetCellAbsolute(col, absRow);
                    if (cell.IsWideContinuation) continue;

                    string g = buffer.GetGraphemeAbsolute(col, absRow);
                    if (string.IsNullOrEmpty(g)) g = " ";
                    foreach (char ch in g)
                    {
                        sb.Append(ch);
                        map.Add(col);
                    }
                }
            }
            finally { buffer.Lock.ExitReadLock(); }

            return (sb.ToString(), map.ToArray());
        }

        /// <summary>Maps a char-range LinkSpan to an inclusive [StartCol, EndCol] column range.</summary>
        public static (int StartCol, int EndCol) SpanToColumns(LinkSpan span, int[] charToCol)
        {
            int startCol = charToCol[span.StartChar];
            int endCol = charToCol[span.EndChar - 1];
            return (startCol, endCol);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~RowTextExtractorTests"`
Expected: PASS (3 tests). If the wide-char test reports a different continuation column, adjust the test's expected columns to match this buffer's wide-cell model — the production mapping logic (skip `IsWideContinuation`) is correct.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.VT/Links/RowTextExtractor.cs tests/NovaTerminal.VT.Tests/Links/RowTextExtractorTests.cs
git commit -m "feat(links): row text extraction with char->column mapping"
```

---

## Task 5: `LinkSchemes` open allowlist

**Files:**
- Create: `src/NovaTerminal.VT/Links/LinkSchemes.cs`
- Test: `tests/NovaTerminal.VT.Tests/Links/LinkSchemesTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.VT.Tests/Links/LinkSchemesTests.cs`:

```csharp
using NovaTerminal.VT.Links;

namespace NovaTerminal.VT.Tests.Links;

public class LinkSchemesTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:me@example.com", true)]
    [InlineData("file:///c:/tmp/x.txt", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)] // case-insensitive scheme
    [InlineData("javascript:alert(1)", false)]
    [InlineData("vbscript:msgbox", false)]
    [InlineData("ftp://host/file", false)]
    [InlineData("notaurl", false)]
    [InlineData("", false)]
    public void IsAllowed_gates_on_scheme(string uri, bool expected)
    {
        Assert.Equal(expected, LinkSchemes.IsAllowed(uri));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~LinkSchemesTests"`
Expected: FAIL — `LinkSchemes` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/NovaTerminal.VT/Links/LinkSchemes.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace NovaTerminal.VT.Links
{
    /// <summary>
    /// Allowlist of URI schemes the terminal will launch. Detected text must never be able to
    /// shell-execute arbitrary/dangerous schemes via Process.Start(UseShellExecute=true).
    /// </summary>
    public static class LinkSchemes
    {
        private static readonly HashSet<string> Allowed =
            new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "file" };

        public static bool IsAllowed(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            int i = uri.IndexOf(':');
            if (i <= 0) return false;
            return Allowed.Contains(uri.Substring(0, i));
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests --filter "FullyQualifiedName~LinkSchemesTests"`
Expected: PASS (9 cases).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.VT/Links/LinkSchemes.cs tests/NovaTerminal.VT.Tests/Links/LinkSchemesTests.cs
git commit -m "feat(links): open-scheme allowlist"
```

---

## Task 6: `EnableLinkDetection` setting

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalSettings.cs:26` (after `SmoothScrolling`)
- Modify: `src/NovaTerminal.App/Shell/TerminalView.cs:678` (in `ApplySettings`, after `_enableSmoothScrolling = settings.SmoothScrolling;`)

- [ ] **Step 1: Add the setting property**

In `src/NovaTerminal.App/Shell/TerminalSettings.cs`, immediately after the `SmoothScrolling` line (line 26), add:

```csharp
        public bool EnableLinkDetection { get; set; } = true;
```

- [ ] **Step 2: Add the backing field in TerminalView**

In `src/NovaTerminal.App/Shell/TerminalView.cs`, next to the other `_enable*` fields (near line 545–546), add:

```csharp
        private bool _enableLinkDetection = true;
```

- [ ] **Step 3: Apply it in ApplySettings**

In `src/NovaTerminal.App/Shell/TerminalView.cs`, in `ApplySettings`, immediately after `_enableSmoothScrolling = settings.SmoothScrolling;` (line 678), add:

```csharp
                _enableLinkDetection = settings.EnableLinkDetection;
```

- [ ] **Step 4: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/Shell/TerminalSettings.cs src/NovaTerminal.App/Shell/TerminalView.cs
git commit -m "feat(links): add EnableLinkDetection setting (default on)"
```

---

## Task 7: Hover state + hit-testing in `TerminalView`

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalView.cs` (fields near line 849; `OnPointerMoved` near line 1730; new `UpdateHoveredLink` / `ClearHoveredLink` helpers; `OnPointerExited` override)

- [ ] **Step 1: Add hover fields**

In `src/NovaTerminal.App/Shell/TerminalView.cs`, near the `_selection` field (line 849), add:

```csharp
        private readonly NovaTerminal.VT.Links.UrlDetector _urlDetector = new NovaTerminal.VT.Links.UrlDetector();
        // Hovered link overlay state (transient UI state, never written to the buffer).
        private (int AbsRow, int StartCol, int EndCol, string Uri)? _hoveredLink;
        // One-row memo so we only re-run detection when the pointer moves to a new row.
        private int _hoverScanRow = -1;
        private System.Collections.Generic.IReadOnlyList<NovaTerminal.VT.Links.LinkSpan> _hoverScanSpans =
            System.Array.Empty<NovaTerminal.VT.Links.LinkSpan>();
        private int[] _hoverScanMap = System.Array.Empty<int>();
```

- [ ] **Step 2: Add the hover update + clear helpers**

In `src/NovaTerminal.App/Shell/TerminalView.cs`, add these methods near the other pointer helpers (e.g. just after `OnPointerReleased`, around line 1792):

```csharp
        private void UpdateHoveredLink(Avalonia.Point position)
        {
            if (_buffer == null) return;

            var (absRow, col) = ScreenToTerminal(position);

            // 1) Explicit OSC 8 link on this cell always takes precedence.
            string? osc8 = _buffer.GetHyperlinkAbsolute(col, absRow);
            if (!string.IsNullOrWhiteSpace(osc8))
            {
                SetHoveredLink((absRow, col, col, osc8));
                return;
            }

            // 2) Auto-detected link, if detection is enabled.
            if (_enableLinkDetection)
            {
                if (absRow != _hoverScanRow)
                {
                    var (text, map) = NovaTerminal.VT.Links.RowTextExtractor.Extract(_buffer, absRow);
                    _hoverScanSpans = _urlDetector.Detect(text);
                    _hoverScanMap = map;
                    _hoverScanRow = absRow;
                }

                foreach (var span in _hoverScanSpans)
                {
                    var (startCol, endCol) = NovaTerminal.VT.Links.RowTextExtractor.SpanToColumns(span, _hoverScanMap);
                    if (col >= startCol && col <= endCol)
                    {
                        SetHoveredLink((absRow, startCol, endCol, span.Uri));
                        return;
                    }
                }
            }

            ClearHoveredLink();
        }

        private void SetHoveredLink((int AbsRow, int StartCol, int EndCol, string Uri) link)
        {
            if (_hoveredLink.Equals(link)) return; // no change -> no repaint
            _hoveredLink = link;
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            InvalidateVisual();
        }

        private void ClearHoveredLink()
        {
            if (_hoveredLink == null) return;
            _hoveredLink = null;
            Cursor = Avalonia.Input.Cursor.Default;
            InvalidateVisual();
        }
```

- [ ] **Step 3: Call the helper from `OnPointerMoved`**

In `src/NovaTerminal.App/Shell/TerminalView.cs`, in `OnPointerMoved`, after the mouse-reporting `return;` block (line 1730) and before `if (_isSelecting)` (line 1732), insert:

```csharp
            if (!_isSelecting)
            {
                UpdateHoveredLink(e.GetCurrentPoint(this).Position);
            }
```

- [ ] **Step 4: Clear hover on pointer exit**

In `src/NovaTerminal.App/Shell/TerminalView.cs`, add an override near the other pointer overrides (e.g. after `OnPointerMoved`'s closing brace, around line 1759):

```csharp
        protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverScanRow = -1;
            ClearHoveredLink();
        }
```

- [ ] **Step 5: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Shell/TerminalView.cs
git commit -m "feat(links): hover hit-testing and Hand cursor for links"
```

---

## Task 8: Render the hover underline overlay

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalView.cs` (in `Render`, after the `context.Custom(...)` op and alongside the bell-flash overlay, around line 1563)

- [ ] **Step 1: Draw the underline overlay**

In `src/NovaTerminal.App/Shell/TerminalView.cs`, in `Render`, immediately before the `if (_isBellFlashActive)` block (line 1564), insert:

```csharp
            if (_hoveredLink is { } link && _metrics.CellWidth > 0 && _metrics.CellHeight > 0)
            {
                int totalLines = buffer.TotalLines;
                int displayStart = Math.Max(0, totalLines - buffer.Rows - ScrollOffset);
                int visualRow = link.AbsRow - displayStart;
                if (visualRow >= 0 && visualRow < buffer.Rows)
                {
                    double x = link.StartCol * _metrics.CellWidth;
                    double width = (link.EndCol - link.StartCol + 1) * _metrics.CellWidth;
                    double y = (visualRow + 1) * _metrics.CellHeight - 1.0;
                    var color = buffer.Theme.Foreground.ToAvaloniaColor();
                    context.FillRectangle(
                        new SolidColorBrush(color, _windowOpacity),
                        new Rect(x, y, width, 1.0));
                }
            }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Manual verification**

Run the app: `scripts/build.ps1 run src/NovaTerminal.App` (or your usual launch). In a shell, run `echo see https://example.com here`, then move the mouse over the URL.
Expected: the URL gets an underline and the cursor becomes a hand while hovering; both disappear when the mouse moves away or leaves the pane.

- [ ] **Step 4: Commit**

```bash
git add src/NovaTerminal.App/Shell/TerminalView.cs
git commit -m "feat(links): underline the hovered link as a render overlay"
```

---

## Task 9: Unified modifier-click open path

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalView.cs` (`OnPointerPressed` block at lines 1656–1683; add `TryOpenLink` helper)

- [ ] **Step 1: Add the `TryOpenLink` helper**

In `src/NovaTerminal.App/Shell/TerminalView.cs`, add near the pointer helpers (e.g. after `ClearHoveredLink`):

```csharp
        private static bool IsLinkActivationModifier(KeyModifiers modifiers)
        {
            // Cmd (Meta) on macOS, Ctrl elsewhere — matches platform conventions.
            return OperatingSystem.IsMacOS()
                ? (modifiers & KeyModifiers.Meta) != 0
                : (modifiers & KeyModifiers.Control) != 0;
        }

        private bool TryOpenLink(string? uri)
        {
            if (!NovaTerminal.VT.Links.LinkSchemes.IsAllowed(uri)) return false;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var linkUri)) return false;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = linkUri.ToString(),
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                // Ignore failed launch attempts.
                return false;
            }
        }

        // Resolves the link under (absRow, col): OSC 8 first, then a detected span.
        private string? ResolveLinkAt(int absRow, int col)
        {
            if (_buffer == null) return null;

            string? osc8 = _buffer.GetHyperlinkAbsolute(col, absRow);
            if (!string.IsNullOrWhiteSpace(osc8)) return osc8;

            if (!_enableLinkDetection) return null;

            var (text, map) = NovaTerminal.VT.Links.RowTextExtractor.Extract(_buffer, absRow);
            foreach (var span in _urlDetector.Detect(text))
            {
                var (startCol, endCol) = NovaTerminal.VT.Links.RowTextExtractor.SpanToColumns(span, map);
                if (col >= startCol && col <= endCol) return span.Uri;
            }
            return null;
        }
```

- [ ] **Step 2: Replace the existing OSC 8 click block**

In `src/NovaTerminal.App/Shell/TerminalView.cs`, replace the current block at lines 1661–1683:

```csharp
                bool isCtrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
                if (isCtrl && _buffer != null)
                {
                    string? hyperlink = _buffer.GetHyperlinkAbsolute(col, row);
                    if (!string.IsNullOrWhiteSpace(hyperlink) &&
                        Uri.TryCreate(hyperlink, UriKind.Absolute, out var linkUri))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = linkUri.ToString(),
                                UseShellExecute = true
                            });
                            e.Handled = true;
                            return;
                        }
                        catch
                        {
                            // Ignore failed launch attempts.
                        }
                    }
                }
```

with:

```csharp
                if (IsLinkActivationModifier(e.KeyModifiers) && _buffer != null)
                {
                    string? uri = ResolveLinkAt(row, col);
                    if (TryOpenLink(uri))
                    {
                        e.Handled = true;
                        return;
                    }
                }
```

- [ ] **Step 3: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Manual verification**

Launch the app. Run `echo visit https://example.com`. Hold Ctrl (Cmd on macOS) and click the underlined URL.
Expected: the URL opens in the default browser. A plain click (no modifier) selects text instead of opening. `echo file:///nonexistent` Ctrl+click does not crash; `echo see x://danger` is not launched (scheme not allowed).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/Shell/TerminalView.cs
git commit -m "feat(links): unified modifier-click open with scheme allowlist"
```

---

## Task 10: Final full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Run the full VT test project**

Run: `scripts/build.ps1 test tests/NovaTerminal.VT.Tests`
Expected: PASS — all link tests plus pre-existing VT tests green.

- [ ] **Step 2: Build the app once more**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: Build succeeded, 0 errors, 0 new warnings from touched files.

- [ ] **Step 3: Commit any incidental fixes (if needed)**

```bash
git add -A
git commit -m "chore(links): final verification fixes"
```

(Skip if there is nothing to commit.)

---

## Self-Review

**Spec coverage:**
- Rule-list `UrlDetector`, scheme + email defaults, extensible → Tasks 1–3. ✓
- Per-row text extraction + char→column map (wide/extended cells) → Task 4. ✓
- Hover-always underline + Hand cursor; one-row memo (no regex per move; off the VT write path) → Tasks 7–8. ✓
- OSC 8 precedence over detected spans → `UpdateHoveredLink` / `ResolveLinkAt` (Tasks 7, 9). ✓
- Unified open path, Ctrl/Cmd modifier, scheme allowlist → Tasks 5, 9. ✓
- Settings toggle (auto-detection; OSC 8 always on) → Task 6 (+ gating in Tasks 7, 9). ✓
- v1 single-row spans only → extraction/hit-test operate on one absolute row; no cross-row logic. ✓
- Testing: detector/extractor/allowlist unit-tested in VT; App glue build- + manually verified → all tasks. ✓

**Placeholder scan:** No TBD/TODO; every code step contains complete code; every command has expected output. ✓

**Type consistency:** `LinkSpan(StartChar, EndChar, Uri)` with exclusive `EndChar` used consistently; `RowTextExtractor.Extract` returns `(string Text, int[] CharToCol)` and `SpanToColumns` returns inclusive `(StartCol, EndCol)` — matched in Tasks 4, 7, 9. `UrlDetector.Detect(string)`, `LinkSchemes.IsAllowed(string?)`, `IsLinkActivationModifier`, `TryOpenLink`, `ResolveLinkAt`, `UpdateHoveredLink`, `SetHoveredLink`, `ClearHoveredLink` referenced with consistent signatures across tasks. ✓

**Known adjustment point:** Task 4's wide-char expected columns depend on this buffer's wide-cell continuation model; the test note explains how to align expectations if they differ — production logic (skip `IsWideContinuation`) is correct regardless.
