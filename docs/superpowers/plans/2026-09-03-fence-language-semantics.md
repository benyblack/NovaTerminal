# Fenced-Block Language Semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Agent Output panel act on a fenced block's info string — render ` ```markdown ` as a nested document behind one panel switch, color ` ```diff ` by line marker, and leave every other language exactly as it renders today.

**Architecture:** `BuildCodeBlock` gains a resolver lookup keyed on the fence's first info token. A resolver hit returns an `IFenceBody` that produces the block *body only*; the border, header, language label and Copy button stay in `BuildCodeBlock`, so Copy keeps yielding raw source for every block. The markdown handler recurses back into the renderer's existing `AppendBlocks` through a delegate, capped at depth 1. A mutable render-pass object carries the panel's switch state down and a "did we transform anything" tally back up.

**Tech Stack:** C# / .NET 10, Avalonia, Markdig 1.3.2 (already referenced), xUnit. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-09-03-fence-language-semantics-design.md`

## Global Constraints

- **Build and test only via the wrappers.** `scripts/build.sh <args>` (Git Bash) or `scripts/build.ps1 <args>`. A raw `dotnet build` hangs when stdout is captured. See `CLAUDE.md`.
- **No new package references.** Markdig is already present; nothing else may be added.
- **Unrecognized info strings must be byte-identical to today.** Any change to an existing expectation in `MarkdownRendererTests` is a bug in this work, not a test to update. The one permitted mechanical edit is adding `.Root` at the 14 sites that call `MarkdownRenderer.Build`.
- **Copy always yields raw source**, in every switch position and for every handler.
- **Agent output is untrusted text.** No new HTML path, no unbounded recursion, no unbounded per-line work.
- **Nesting depth cap is 1.** `MaxFenceDepth = 1`.
- **Theme brushes resolve per build** via `Find(anchor, "Nt*", fallback)`, degrading to a fixed fallback when the resource is absent.
- **This branch is stacked on #378.** Do not rebase onto `main` until #378 merges.
- Close any running NovaTerminal launched from this worktree before building; it locks `NovaTerminal.exe` and the build fails with MSB3027.

---

### Task 1: Extract `MarkdownTheme` into its own file

Behaviour-neutral refactor. It exists because Tasks 3 and 4 put handlers in separate files, and those handlers need the theme type — which is currently a `private sealed class` nested inside `MarkdownRenderer`.

**Files:**
- Create: `src/NovaTerminal.App/AgentOutput/MarkdownTheme.cs`
- Modify: `src/NovaTerminal.App/AgentOutput/MarkdownRenderer.cs` (delete the nested `Theme` class at lines 643-674; retarget every `Theme` reference)
- Test: no new test. Verification is the existing suite staying green — that is the correct check for an extraction that must change no behavior.

**Interfaces:**
- Consumes: nothing.
- Produces: `internal sealed class MarkdownTheme` with `IBrush Foreground, Secondary, CodeBackground, PanelBackground, Hairline, Accent` (all `required ... { get; init; }`) and `internal static MarkdownTheme Resolve(StyledElement anchor)`.

- [ ] **Step 1: Create the new file**

```csharp
using Avalonia;
using Avalonia.Media;

namespace NovaTerminal.AgentOutput;

/// <summary>Resolved brush set for one render pass.</summary>
/// <remarks>
/// Extracted from <see cref="MarkdownRenderer"/> so fence-body handlers in sibling files can
/// take it. Resolution is unchanged: prefer the app's <c>Nt*</c> theme brushes, fall back to the
/// fixed palette the pane's other hand-styled surfaces hard-code.
/// </remarks>
internal sealed class MarkdownTheme
{
    private static readonly IBrush FallbackForeground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF6, 0xFA));
    private static readonly IBrush FallbackSecondary = new SolidColorBrush(Color.FromRgb(0x96, 0xA0, 0xAE));
    private static readonly IBrush FallbackCodeBackground = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C));
    private static readonly IBrush FallbackPanel = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x21));
    private static readonly IBrush FallbackHairline = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x35));
    private static readonly IBrush FallbackAccent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8B, 0xD8));

    internal required IBrush Foreground { get; init; }

    internal required IBrush Secondary { get; init; }

    internal required IBrush CodeBackground { get; init; }

    internal required IBrush PanelBackground { get; init; }

    internal required IBrush Hairline { get; init; }

    internal required IBrush Accent { get; init; }

    internal static MarkdownTheme Resolve(StyledElement anchor)
    {
        return new MarkdownTheme
        {
            Foreground = Find(anchor, "NtFg", FallbackForeground),
            Secondary = Find(anchor, "NtFg3", FallbackSecondary),
            CodeBackground = Find(anchor, "NtPanelAlt", FallbackCodeBackground),
            PanelBackground = Find(anchor, "NtPanel", FallbackPanel),
            Hairline = Find(anchor, "NtHairline", FallbackHairline),
            Accent = Find(anchor, "NtBlue", FallbackAccent),
        };
    }

    private static IBrush Find(StyledElement anchor, string key, IBrush fallback)
    {
        // Control themes put brushes in as object values; anything that is not a brush
        // (an unexpected override) degrades to the fixed fallback rather than crashing.
        return anchor.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : fallback;
    }
}
```

- [ ] **Step 2: Delete the nested class and its now-duplicated fallback fields**

In `MarkdownRenderer.cs`, delete the `private sealed class Theme { ... }` block (lines 643-674) and the six `Fallback*` fields (lines 58-63) — they moved into `MarkdownTheme`.

- [ ] **Step 3: Retarget every `Theme` reference**

Replace the type name `Theme` with `MarkdownTheme` throughout `MarkdownRenderer.cs`. There are references in `Build` (the `var theme = Theme.Resolve(...)` local) and in the parameter lists of `AppendBlocks`, `BuildHeading`, `BuildParagraph`, `BuildCodeBlock`, `BuildList`, `BuildQuote`, `BuildTable` and the inline builders.

```bash
grep -n "Theme" src/NovaTerminal.App/AgentOutput/MarkdownRenderer.cs
```

Expected after the edit: every hit reads `MarkdownTheme`, and none of them is a declaration.

- [ ] **Step 4: Build and run the full AgentOutput suite**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentOutput"`
Expected: PASS, 86 tests, 0 failed. A refactor that changes a count or an expectation has changed behavior and must be corrected, not accepted.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/AgentOutput/MarkdownTheme.cs src/NovaTerminal.App/AgentOutput/MarkdownRenderer.cs
git commit -m "refactor(agent-output): extract MarkdownTheme from the renderer

Fence-body handlers land in sibling files and need the theme type, which
was a private nested class. Pure move: same Resolve, same Find, same
fallbacks, no behavior change."
```

---

### Task 2: `MarkdownRenderResult` and the render pass

Threads two things through the walk that later tasks need: the panel's switch state going down, and a "did any handler transform a block" tally coming back up. No handler exists yet, so `HasTransformBlock` is always false at the end of this task — that is the expected state and the test asserts it.

**Files:**
- Create: `src/NovaTerminal.App/AgentOutput/MarkdownRenderPass.cs`
- Modify: `src/NovaTerminal.App/AgentOutput/MarkdownRenderer.cs` (`Build` signature and return, `AppendBlocks`, `BuildCodeBlock`, `BuildList`, `BuildQuote`, `BuildTable`)
- Modify: `src/NovaTerminal.App/AgentOutput/AgentOutputPanel.axaml.cs:105`
- Test: `tests/NovaTerminal.App.Tests/AgentOutput/MarkdownRendererTests.cs`

**Interfaces:**
- Consumes: `MarkdownTheme` (Task 1).
- Produces:
  - `public sealed record MarkdownRenderResult(Control Root, bool HasTransformBlock)`
  - `internal sealed class MarkdownRenderPass` with `internal required bool RenderFencedMarkdown { get; init; }` and `internal bool HasTransformBlock { get; set; }`
  - `public static MarkdownRenderResult Build(string markdown, StyledElement resourceAnchor, Action<string>? onCopyText = null, Action<string>? onOpenLink = null, bool renderFencedMarkdown = true)`
  - `AppendBlocks(IList<Control> target, IEnumerable<Block> blocks, MarkdownTheme theme, Action<string>? onCopyText, Action<string>? onOpenLink, MarkdownRenderPass pass, int depth)` — and the same two trailing parameters `(MarkdownRenderPass pass, int depth)` appended to `BuildCodeBlock`, `BuildList`, `BuildQuote`, `BuildTable`.

- [ ] **Step 1: Write the failing test**

Add to `MarkdownRendererTests.cs`:

```csharp
[Fact]
public void Build_ReportsNoTransformBlock_ForOrdinaryMarkdown()
{
    MarkdownRenderResult result = MarkdownRenderer.Build("# Title\n\nbody\n", Anchor);

    Assert.IsType<StackPanel>(result.Root);
    Assert.False(result.HasTransformBlock);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Build_ReportsNoTransformBlock"`
Expected: FAIL to compile — `MarkdownRenderResult` does not exist and `Build` returns `Control`.

- [ ] **Step 3: Create the render pass**

```csharp
namespace NovaTerminal.AgentOutput;

/// <summary>Mutable state for one render pass of <see cref="MarkdownRenderer.Build"/>.</summary>
/// <remarks>
/// Two jobs, both of which have to cross the recursive block walk. <see cref="RenderFencedMarkdown"/>
/// travels down: it is the panel's switch, and a fence handler that transforms content has to
/// honour it. <see cref="HasTransformBlock"/> travels up: the panel shows its switch only when
/// the render actually produced something the switch governs, so a response with no such block
/// carries no pointless control.
/// </remarks>
internal sealed class MarkdownRenderPass
{
    internal required bool RenderFencedMarkdown { get; init; }

    /// <summary>Set by any handler whose <c>IsTransform</c> is true, at any depth.</summary>
    internal bool HasTransformBlock { get; set; }
}
```

- [ ] **Step 4: Change `Build` to return a result and open the pass**

Replace the body of `Build` (`MarkdownRenderer.cs:71-83`):

```csharp
public static MarkdownRenderResult Build(
    string markdown,
    StyledElement resourceAnchor,
    Action<string>? onCopyText = null,
    Action<string>? onOpenLink = null,
    bool renderFencedMarkdown = true)
{
    MarkdownDocument document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
    var theme = MarkdownTheme.Resolve(resourceAnchor);
    var pass = new MarkdownRenderPass { RenderFencedMarkdown = renderFencedMarkdown };

    var root = new StackPanel { Spacing = 2 };
    AppendBlocks(root.Children, document, theme, onCopyText, onOpenLink, pass, depth: 0);
    return new MarkdownRenderResult(root, pass.HasTransformBlock);
}
```

And declare the result record at the top of the file, immediately above `public static class MarkdownRenderer`:

```csharp
/// <summary>One render's output: the tree, and whether it contains a switch-governed block.</summary>
public sealed record MarkdownRenderResult(Control Root, bool HasTransformBlock);
```

- [ ] **Step 5: Append `(pass, depth)` to the five recursive methods**

Add `MarkdownRenderPass pass, int depth` as the last two parameters of `AppendBlocks`, `BuildCodeBlock`, `BuildList`, `BuildQuote` and `BuildTable`, and pass them through at every call site. The three inner `AppendBlocks` calls recurse at the *same* depth — only a fence handler increments it:

- `MarkdownRenderer.cs:282` (in `BuildList`) → `AppendBlocks(itemContent.Children, listItem, theme, onCopyText, onOpenLink, pass, depth);`
- `MarkdownRenderer.cs:299` (in `BuildQuote`) → `AppendBlocks(content.Children, quote, theme, onCopyText, onOpenLink, pass, depth);`
- `MarkdownRenderer.cs:358` (in `BuildTable`) → `AppendBlocks(cellContent.Children, cell, theme, onCopyText, onOpenLink, pass, depth);`

In `AppendBlocks`, the two code-block arms become:

```csharp
case FencedCodeBlock fenced:
    target.Add(BuildCodeBlock(GetLinesText(fenced), fenced.Info.ToString(), theme, onCopyText, pass, depth));
    break;

case CodeBlock code:
    target.Add(BuildCodeBlock(GetLinesText(code), null, theme, onCopyText, pass, depth));
    break;
```

`BuildHeading` and `BuildParagraph` are untouched: they contain inlines, not blocks, and build no code block.

- [ ] **Step 6: Update the one production call site**

`AgentOutputPanel.axaml.cs:105` currently assigns `Control rendered = MarkdownRenderer.Build(...)`. Change to:

```csharp
MarkdownRenderResult rendered = MarkdownRenderer.Build(
    markdown,
    this,
    onCopyText: text => _ = CopyToClipboardAsync(text),
    onOpenLink: url => _ = OpenLinkAsync(url));
MarkdownHost.Children.Add(rendered.Root);
```

- [ ] **Step 7: Add `.Root` at the 14 existing test call sites**

Every `(StackPanel)MarkdownRenderer.Build(...)` in `MarkdownRendererTests.cs` becomes `(StackPanel)MarkdownRenderer.Build(...).Root`. This is the only permitted edit to those tests; no expectation changes.

```bash
grep -c "MarkdownRenderer.Build" tests/NovaTerminal.App.Tests/AgentOutput/MarkdownRendererTests.cs
```

Expected: 15 (the 14 pre-existing plus the new test from Step 1).

- [ ] **Step 8: Run the suite**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentOutput"`
Expected: PASS, 87 tests, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src/NovaTerminal.App/AgentOutput/ tests/NovaTerminal.App.Tests/AgentOutput/MarkdownRendererTests.cs
git commit -m "refactor(agent-output): thread a render pass through the markdown walk

Build returns MarkdownRenderResult instead of a bare Control, and a
MarkdownRenderPass carries the panel's fence switch down the recursive
walk and a has-transform tally back up. No handler consumes either yet,
so HasTransformBlock is still always false."
```

---

### Task 3: The fence seam and the `diff` handler

The diff handler goes first because it is the simpler of the two — no recursion, no switch participation — so it proves the seam before the markdown handler leans on it.

**Files:**
- Create: `src/NovaTerminal.App/AgentOutput/Fences/IFenceBody.cs`
- Create: `src/NovaTerminal.App/AgentOutput/Fences/FenceBodyResolver.cs`
- Create: `src/NovaTerminal.App/AgentOutput/Fences/DiffFenceBody.cs`
- Modify: `src/NovaTerminal.App/AgentOutput/MarkdownTheme.cs` (three brushes)
- Modify: `src/NovaTerminal.App/AgentOutput/MarkdownRenderer.cs` (`BuildCodeBlock` consults the resolver)
- Test: `tests/NovaTerminal.App.Tests/AgentOutput/FenceBodyTests.cs`

**Interfaces:**
- Consumes: `MarkdownTheme` (Task 1), `MarkdownRenderPass` (Task 2).
- Produces:
  - `internal delegate Control NestedMarkdownRenderer(string markdown, int depth);`
  - `internal sealed record FenceContext(int Depth, bool RenderFencedMarkdown, NestedMarkdownRenderer RenderNested, Action<string>? OnCopyText);`
  - `internal interface IFenceBody { bool IsTransform { get; } Control Build(string code, MarkdownTheme theme, FenceContext context); }`
  - `internal static class FenceBodyResolver` with `internal static IFenceBody? Resolve(string? info)` and `internal static string NormalizeInfo(string? info)`
  - `MarkdownTheme` gains `IBrush Added, Removed, Hunk`
  - `internal const int MarkdownRenderer.MaxFenceDepth = 1`

- [ ] **Step 1: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/AgentOutput/FenceBodyTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using NovaTerminal.AgentOutput;
using NovaTerminal.AgentOutput.Fences;
using Xunit;

namespace NovaTerminal.Tests.AgentOutput;

/// <summary>
/// The fence-body seam: which info strings resolve, and what each handler makes of a body.
/// </summary>
public sealed class FenceBodyTests
{
    private static readonly Border Anchor = new();

    [Theory]
    [InlineData("markdown")]
    [InlineData("md")]
    [InlineData("MARKDOWN")]
    [InlineData("Md")]
    [InlineData("markdown title=\"README\"")]
    [InlineData("  markdown  ")]
    public void MarkdownAliases_Resolve(string info)
        => Assert.NotNull(FenceBodyResolver.Resolve(info));

    [Theory]
    [InlineData("diff")]
    [InlineData("patch")]
    [InlineData("DIFF")]
    public void DiffAliases_Resolve(string info)
        => Assert.NotNull(FenceBodyResolver.Resolve(info));

    [Theory]
    [InlineData("csharp")]
    [InlineData("bash")]
    [InlineData("json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnrecognizedInfo_DoesNotResolve(string? info)
        => Assert.Null(FenceBodyResolver.Resolve(info));

    [Fact]
    public void DiffHandler_IsNotATransform()
    {
        IFenceBody body = Assert.IsType<DiffFenceBody>(FenceBodyResolver.Resolve("diff"));
        Assert.False(body.IsTransform);
    }

    [Theory]
    [InlineData("+added line", "NtGreen")]
    [InlineData("-removed line", "NtRed")]
    [InlineData("@@ -1,2 +1,3 @@", "NtYellow")]
    [InlineData("+++ b/file.cs", "Secondary")]
    [InlineData("--- a/file.cs", "Secondary")]
    [InlineData("diff --git a/x b/x", "Secondary")]
    [InlineData("index 1234567..89abcde 100644", "Secondary")]
    [InlineData(" context line", "Foreground")]
    public void DiffHandler_ColorsLinesByMarker(string line, string expectedRole)
    {
        var theme = MarkdownThemeProbe.WithDistinctBrushes();
        IFenceBody body = FenceBodyResolver.Resolve("diff")!;

        Control control = body.Build(line, theme, Context(theme));

        var text = Assert.IsType<SelectableTextBlock>(control);
        Run run = text.Inlines!.OfType<Run>().Single();
        Assert.Same(MarkdownThemeProbe.BrushFor(theme, expectedRole), run.Foreground);
    }

    [Fact]
    public void DiffHandler_EmitsOneRunPerLine()
    {
        var theme = MarkdownThemeProbe.WithDistinctBrushes();
        IFenceBody body = FenceBodyResolver.Resolve("diff")!;

        Control control = body.Build("+one\n-two\n three", theme, Context(theme));

        var text = Assert.IsType<SelectableTextBlock>(control);
        Assert.Equal(3, text.Inlines!.OfType<Run>().Count());
    }

    private static FenceContext Context(MarkdownTheme theme)
        => new(Depth: 0, RenderFencedMarkdown: true, RenderNested: (_, _) => new Border(), OnCopyText: null);
}
```

The `MarkdownThemeProbe` helper keeps brush identity assertions readable. Create it in the same file, below the test class:

```csharp
/// <summary>
/// Builds a theme whose brushes are all distinct instances, so a test can assert which role a
/// run was painted with by reference rather than by colour value.
/// </summary>
internal static class MarkdownThemeProbe
{
    private static readonly Dictionary<string, IBrush> Roles = new()
    {
        ["Foreground"] = new SolidColorBrush(Color.FromRgb(1, 1, 1)),
        ["Secondary"] = new SolidColorBrush(Color.FromRgb(2, 2, 2)),
        ["NtGreen"] = new SolidColorBrush(Color.FromRgb(3, 3, 3)),
        ["NtRed"] = new SolidColorBrush(Color.FromRgb(4, 4, 4)),
        ["NtYellow"] = new SolidColorBrush(Color.FromRgb(5, 5, 5)),
    };

    internal static IBrush BrushFor(MarkdownTheme theme, string role) => role switch
    {
        "Foreground" => theme.Foreground,
        "Secondary" => theme.Secondary,
        "NtGreen" => theme.Added,
        "NtRed" => theme.Removed,
        "NtYellow" => theme.Hunk,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unknown role"),
    };

    internal static MarkdownTheme WithDistinctBrushes() => new()
    {
        Foreground = Roles["Foreground"],
        Secondary = Roles["Secondary"],
        CodeBackground = new SolidColorBrush(Color.FromRgb(6, 6, 6)),
        PanelBackground = new SolidColorBrush(Color.FromRgb(7, 7, 7)),
        Hairline = new SolidColorBrush(Color.FromRgb(8, 8, 8)),
        Accent = new SolidColorBrush(Color.FromRgb(9, 9, 9)),
        Added = Roles["NtGreen"],
        Removed = Roles["NtRed"],
        Hunk = Roles["NtYellow"],
    };
}
```

`MarkdownTheme` and `MarkdownRenderPass` are `internal`, and `NovaTerminal.App.csproj:510` already grants `InternalsVisibleTo` to `NovaTerminal.App.Tests`, so no visibility change is needed.

- [ ] **Step 2: Run tests to verify they fail**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~FenceBodyTests"`
Expected: FAIL to compile — `NovaTerminal.AgentOutput.Fences` does not exist.

- [ ] **Step 3: Add the three theme brushes**

In `MarkdownTheme.cs`, add the fallbacks and properties, and resolve them:

```csharp
    private static readonly IBrush FallbackAdded = new SolidColorBrush(Color.FromRgb(0x6F, 0xBF, 0x73));
    private static readonly IBrush FallbackRemoved = new SolidColorBrush(Color.FromRgb(0xD9, 0x6C, 0x6C));
    private static readonly IBrush FallbackHunk = new SolidColorBrush(Color.FromRgb(0xD6, 0xB0, 0x5C));
```

```csharp
    /// <summary>Diff addition lines.</summary>
    internal required IBrush Added { get; init; }

    /// <summary>Diff removal lines.</summary>
    internal required IBrush Removed { get; init; }

    /// <summary>Diff hunk headers.</summary>
    internal required IBrush Hunk { get; init; }
```

```csharp
            Added = Find(anchor, "NtGreen", FallbackAdded),
            Removed = Find(anchor, "NtRed", FallbackRemoved),
            Hunk = Find(anchor, "NtYellow", FallbackHunk),
```

- [ ] **Step 4: Create the interface and context**

`src/NovaTerminal.App/AgentOutput/Fences/IFenceBody.cs`:

```csharp
using System;
using Avalonia.Controls;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>Renders a nested markdown document at the given depth.</summary>
internal delegate Control NestedMarkdownRenderer(string markdown, int depth);

/// <summary>What a handler is allowed to know about the render it sits inside.</summary>
internal sealed record FenceContext(
    int Depth,
    bool RenderFencedMarkdown,
    NestedMarkdownRenderer RenderNested,
    Action<string>? OnCopyText);

/// <summary>
/// Produces the body of one fenced code block, chosen by the fence's info string.
/// </summary>
/// <remarks>
/// A handler owns the <b>body only</b>. The border, header row, language label and Copy button
/// stay with the renderer, which keeps two properties that would otherwise erode one handler at
/// a time: every code block looks like every other one, and Copy always yields the raw source
/// no matter what is on screen.
/// </remarks>
internal interface IFenceBody
{
    /// <summary>
    /// True when this handler replaces the source with something else, and so participates in
    /// the panel's rendered/source switch. A restyle hides nothing and is not a transform.
    /// </summary>
    bool IsTransform { get; }

    Control Build(string code, MarkdownTheme theme, FenceContext context);
}
```

- [ ] **Step 5: Create the resolver**

`src/NovaTerminal.App/AgentOutput/Fences/FenceBodyResolver.cs`:

```csharp
using System;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>Maps a fence info string to a body handler, or to null for "leave it alone".</summary>
/// <remarks>
/// A closed switch on purpose. Nothing outside this assembly registers a handler, so a
/// registration mechanism would be machinery with no consumer.
/// </remarks>
internal static class FenceBodyResolver
{
    private static readonly MarkdownFenceBody Markdown = new();
    private static readonly DiffFenceBody Diff = new();

    internal static IFenceBody? Resolve(string? info) => NormalizeInfo(info) switch
    {
        "markdown" or "md" => Markdown,
        "diff" or "patch" => Diff,
        _ => null,
    };

    /// <summary>
    /// The first whitespace-delimited token, lowercased invariantly.
    /// </summary>
    /// <remarks>
    /// The first token rather than the whole string, so <c>markdown title="README"</c> still
    /// resolves. Splitting here rather than trusting Markdig's own Info/Arguments division keeps
    /// the match independent of how the parser chooses to divide them.
    /// </remarks>
    internal static string NormalizeInfo(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = info.AsSpan().Trim();
        int end = span.IndexOfAny(' ', '\t');
        if (end >= 0)
        {
            span = span[..end];
        }

        return span.ToString().ToLowerInvariant();
    }
}
```

- [ ] **Step 6: Create the diff handler**

`src/NovaTerminal.App/AgentOutput/Fences/DiffFenceBody.cs`:

```csharp
using System;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>Colors a unified diff by each line's leading marker.</summary>
/// <remarks>
/// A restyle, not a transform: the text is unchanged and nothing is hidden, so there is nothing
/// for the panel's switch to recover and <see cref="IsTransform"/> is false.
/// </remarks>
internal sealed class DiffFenceBody : IFenceBody
{
    private const string MonospaceFontFamily = "Cascadia Mono PL, Consolas, Menlo, monospace";

    public bool IsTransform => false;

    public Control Build(string code, MarkdownTheme theme, FenceContext context)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontFamily = new FontFamily(MonospaceFontFamily),
            Foreground = theme.Foreground,
        };

        string[] lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            text.Inlines?.Add(new Run
            {
                Text = i == lines.Length - 1 ? line : line + "\n",
                Foreground = BrushFor(line, theme),
            });
        }

        return text;
    }

    /// <summary>
    /// Order is load-bearing: the three-character file headers are tested before the
    /// one-character markers, or <c>+++ b/file</c> reads as an addition.
    /// </summary>
    private static IBrush BrushFor(string line, MarkdownTheme theme)
    {
        if (line.StartsWith("+++", StringComparison.Ordinal) ||
            line.StartsWith("---", StringComparison.Ordinal) ||
            line.StartsWith("diff --git", StringComparison.Ordinal) ||
            line.StartsWith("index ", StringComparison.Ordinal))
        {
            return theme.Secondary;
        }

        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return theme.Hunk;
        }

        if (line.StartsWith("+", StringComparison.Ordinal))
        {
            return theme.Added;
        }

        if (line.StartsWith("-", StringComparison.Ordinal))
        {
            return theme.Removed;
        }

        return theme.Foreground;
    }
}
```

- [ ] **Step 7: Create a placeholder markdown handler so the resolver compiles**

Task 4 fills this in. For now it must resolve and behave exactly like the unhandled path, so no observable change ships in this task:

```csharp
using Avalonia.Controls;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>Renders a markdown fence as a nested document. Filled in by Task 4.</summary>
internal sealed class MarkdownFenceBody : IFenceBody
{
    public bool IsTransform => true;

    public Control Build(string code, MarkdownTheme theme, FenceContext context)
        => context.RenderNested(code, context.Depth + 1);
}
```

- [ ] **Step 8: Consult the resolver from `BuildCodeBlock`**

In `MarkdownRenderer.cs`, add the depth constant beside `MonospaceFontFamily`:

```csharp
    /// <summary>
    /// Deepest nesting a fence handler may render into. Agent output is untrusted and this tree
    /// is rebuilt on the streaming debounce, so a fence inside a rendered fence stays source.
    /// </summary>
    internal const int MaxFenceDepth = 1;
```

Then, inside `BuildCodeBlock`, replace the single-`Run` body construction with a resolver lookup. The chrome below it is untouched:

```csharp
    IFenceBody? handler = depth < MaxFenceDepth ? FenceBodyResolver.Resolve(language) : null;
    Control body;
    if (handler is null)
    {
        var codeText = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontFamily = new FontFamily(MonospaceFontFamily),
            Foreground = theme.Foreground,
        };
        codeText.Inlines?.Add(new Run { Text = code });
        body = codeText;
    }
    else
    {
        if (handler.IsTransform)
        {
            pass.HasTransformBlock = true;
        }

        body = handler.Build(
            code,
            theme,
            new FenceContext(
                depth,
                pass.RenderFencedMarkdown,
                (nested, nestedDepth) => BuildNested(nested, theme, onCopyText, pass, nestedDepth),
                onCopyText));
    }
```

Use `body` where the old `codeText` was used, in the inner content `Border`.

Add the nested-render helper next to `AppendBlocks`:

```csharp
    /// <summary>Renders a fence's body as markdown, one level deeper in the same pass.</summary>
    private static Control BuildNested(
        string markdown,
        MarkdownTheme theme,
        Action<string>? onCopyText,
        MarkdownRenderPass pass,
        int depth)
    {
        MarkdownDocument document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var panel = new StackPanel { Spacing = 2 };
        AppendBlocks(panel.Children, document, theme, onCopyText, onOpenLink: null, pass, depth);
        return panel;
    }
```

Add `using NovaTerminal.AgentOutput.Fences;` to the file's usings.

- [ ] **Step 9: Run the fence tests**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~FenceBodyTests"`
Expected: PASS, 21 tests, 0 failed.

- [ ] **Step 10: Run the whole AgentOutput suite for regressions**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentOutput"`
Expected: PASS. `FencedCodeBlock_RendersItsText_WithACopyButton` uses ` ```csharp `, which must still resolve to null and render identically.

- [ ] **Step 11: Commit**

```bash
git add src/NovaTerminal.App/AgentOutput/ tests/NovaTerminal.App.Tests/AgentOutput/FenceBodyTests.cs
git commit -m "feat(agent-output): add the fence-body seam and a diff handler

BuildCodeBlock now asks FenceBodyResolver what a fence's info string
means; a null answer keeps today's flat Run exactly as it was. The diff
handler colors lines by leading marker, testing the three-character file
headers before the one-character ones so '+++ b/file' is not an addition.

Block chrome stays with the renderer, so Copy still yields raw source
for every block regardless of handler."
```

---

### Task 4: The `markdown` handler and the depth cap

**Files:**
- Modify: `src/NovaTerminal.App/AgentOutput/Fences/MarkdownFenceBody.cs`
- Test: `tests/NovaTerminal.App.Tests/AgentOutput/MarkdownRendererTests.cs`

**Interfaces:**
- Consumes: `IFenceBody`, `FenceContext`, `NestedMarkdownRenderer` (Task 3); `MarkdownRenderer.MaxFenceDepth` (Task 3); `MarkdownRenderResult` (Task 2).
- Produces: no new public surface.

- [ ] **Step 1: Write the failing tests**

Add to `MarkdownRendererTests.cs`:

```csharp
[Fact]
public void MarkdownFence_RendersNestedBlocks_NotSource()
{
    MarkdownRenderResult result = MarkdownRenderer.Build("```markdown\n# Nested Title\n```\n", Anchor);

    // A rendered heading is a TextBlock with the heading's own size, not a monospace code run.
    TextBlock? heading = TextBlocks((StackPanel)result.Root)
        .FirstOrDefault(b => TextOf(b).Contains("Nested Title", StringComparison.Ordinal));
    Assert.NotNull(heading);
    Assert.True(heading!.FontSize > 12, "a rendered heading is larger than code text");
    Assert.True(result.HasTransformBlock);
}

[Fact]
public void MarkdownFence_WithSwitchOff_RendersSource_ButStillReportsTransform()
{
    MarkdownRenderResult result = MarkdownRenderer.Build(
        "```markdown\n# Nested Title\n```\n",
        Anchor,
        renderFencedMarkdown: false);

    TextBlock? source = TextBlocks((StackPanel)result.Root)
        .FirstOrDefault(b => TextOf(b).Contains("# Nested Title", StringComparison.Ordinal));
    Assert.NotNull(source);

    // The switch must stay visible, or the choice is not reversible.
    Assert.True(result.HasTransformBlock);
}

[Fact]
public void MarkdownFence_NestedInsideAnother_RendersTheInnerOneAsSource()
{
    const string md = "````markdown\n# Outer\n\n```markdown\n# Inner\n```\n````\n";

    MarkdownRenderResult result = MarkdownRenderer.Build(md, Anchor);

    // Outer renders: its heading is a real heading.
    TextBlock? outer = TextBlocks((StackPanel)result.Root)
        .FirstOrDefault(b => TextOf(b).Contains("Outer", StringComparison.Ordinal));
    Assert.NotNull(outer);
    Assert.True(outer!.FontSize > 12);

    // Inner does not: its hash survives as literal text at the depth cap.
    TextBlock? inner = TextBlocks((StackPanel)result.Root)
        .FirstOrDefault(b => TextOf(b).Contains("# Inner", StringComparison.Ordinal));
    Assert.NotNull(inner);
}

[Fact]
public void MarkdownFence_KeepsCopyYieldingRawSource()
{
    string? copied = null;
    MarkdownRenderResult result = MarkdownRenderer.Build(
        "```markdown\n# Nested Title\n```\n",
        Anchor,
        onCopyText: text => copied = text);

    Button copy = Descendants((StackPanel)result.Root).OfType<Button>().First(b => b.Content as string == "Copy");
    copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    Assert.Equal("# Nested Title\n", copied);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~MarkdownFence"`
Expected: FAIL. The switch-off test fails because the Task 3 placeholder ignores `RenderFencedMarkdown` and always renders nested.

- [ ] **Step 3: Implement the handler**

Replace `MarkdownFenceBody.cs` in full:

```csharp
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>
/// Renders a <c>markdown</c> / <c>md</c> fence as a nested document rather than as source.
/// </summary>
/// <remarks>
/// <para>
/// Recursion goes back through the renderer's own block walk rather than reimplementing it, so
/// every block type the panel already supports - headings, tables, task lists, links with their
/// scheme allowlist - works inside a fence with no duplicated rendering logic.
/// </para>
/// <para>
/// The switch is honoured here rather than at the resolver, because a handler that renders
/// source must still report itself as a transform: that is what keeps the panel's switch on
/// screen, and a switch that hid itself when flipped would be a one-way door.
/// </para>
/// </remarks>
internal sealed class MarkdownFenceBody : IFenceBody
{
    private const string MonospaceFontFamily = "Cascadia Mono PL, Consolas, Menlo, monospace";

    public bool IsTransform => true;

    public Control Build(string code, MarkdownTheme theme, FenceContext context)
    {
        if (!context.RenderFencedMarkdown)
        {
            return BuildSource(code, theme);
        }

        return context.RenderNested(code, context.Depth + 1);
    }

    /// <summary>The unhandled path's body, reproduced for the switch's source position.</summary>
    private static Control BuildSource(string code, MarkdownTheme theme)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontFamily = new FontFamily(MonospaceFontFamily),
            Foreground = theme.Foreground,
        };
        text.Inlines?.Add(new Run { Text = code });
        return text;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~MarkdownFence"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Run the whole AgentOutput suite**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentOutput"`
Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/AgentOutput/Fences/MarkdownFenceBody.cs tests/NovaTerminal.App.Tests/AgentOutput/MarkdownRendererTests.cs
git commit -m "feat(agent-output): render markdown fences as nested documents

A markdown/md fence recurses back through the renderer's own block walk,
so every block type the panel supports works inside a fence for free.
Capped at depth 1: a fence inside a rendered fence stays source.

With the switch off the handler renders source but still reports itself
as a transform, which is what keeps the switch on screen - a switch that
vanished when flipped would be a one-way door."
```

---

### Task 5: The panel switch

**Files:**
- Modify: `src/NovaTerminal.App/AgentOutput/AgentOutputViewModel.cs`
- Modify: `src/NovaTerminal.App/AgentOutput/AgentOutputPanel.axaml` (header row)
- Modify: `src/NovaTerminal.App/AgentOutput/AgentOutputPanel.axaml.cs` (pass the flag, gate visibility, handle the click)
- Test: `tests/NovaTerminal.App.Tests/AgentOutput/AgentOutputViewModelTests.cs`, `tests/NovaTerminal.App.Tests/AgentOutput/AgentOutputPanelTests.cs`

**Interfaces:**
- Consumes: `MarkdownRenderResult.HasTransformBlock` (Task 2), `renderFencedMarkdown` parameter on `Build` (Task 2).
- Produces: `AgentOutputViewModel.RenderFencedMarkdown` (`bool`, default `true`, raises `PropertyChanged`).

- [ ] **Step 1: Write the failing view-model test**

Add to `AgentOutputViewModelTests.cs`:

```csharp
[Fact]
public void RenderFencedMarkdown_DefaultsToTrue()
{
    var vm = new AgentOutputViewModel();

    Assert.True(vm.RenderFencedMarkdown);
}

[Fact]
public void RenderFencedMarkdown_RaisesPropertyChanged_OnlyWhenItChanges()
{
    var vm = new AgentOutputViewModel();

    var names = Changed(vm, () => vm.RenderFencedMarkdown = false);
    Assert.Contains(nameof(AgentOutputViewModel.RenderFencedMarkdown), names);

    var again = Changed(vm, () => vm.RenderFencedMarkdown = false);
    Assert.DoesNotContain(nameof(AgentOutputViewModel.RenderFencedMarkdown), again);
}
```

`Changed` is the existing helper in that file.

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~RenderFencedMarkdown"`
Expected: FAIL to compile — the property does not exist.

- [ ] **Step 3: Add the property**

In `AgentOutputViewModel.cs`, add the backing field beside the others and the property following the file's established shape:

```csharp
    private bool _renderFencedMarkdown = true;
```

```csharp
    /// <summary>
    /// Render <c>```markdown</c> fences as documents rather than as source.
    /// </summary>
    /// <remarks>
    /// Panel-level rather than per-block, and deliberately so: the panel rebuilds its whole
    /// control tree on every <see cref="MarkdownText"/> change, which while streaming is every
    /// few hundred milliseconds. State living in a block's own control would reset on every
    /// tick, and keying it by block ordinal would reattach a choice to the wrong block when a
    /// new block arrives earlier in the stream. One field survives all of that. Per-pane
    /// runtime state; not persisted.
    /// </remarks>
    public bool RenderFencedMarkdown
    {
        get => _renderFencedMarkdown;
        set
        {
            if (_renderFencedMarkdown == value)
            {
                return;
            }

            _renderFencedMarkdown = value;
            OnPropertyChanged(nameof(RenderFencedMarkdown));
        }
    }
```

- [ ] **Step 4: Run the view-model test to verify it passes**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~RenderFencedMarkdown"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Write the failing panel test**

Add to `AgentOutputPanelTests.cs`. Three details of that file's pattern are load-bearing: `[AvaloniaFact]` rather than `[Fact]` (the panel is a XAML `UserControl` and `InitializeComponent` needs the headless application up — a plain `[Fact]` throws), the `CreatePanel(out var viewModel)` helper, and `FindControl<T>("Name")` rather than walking the visual tree. Add `using Avalonia.Controls.Primitives;` for `ToggleButton`.

```csharp
[AvaloniaFact]
public void FenceSwitch_IsHidden_WhenTheResponseHasNoMarkdownFence()
{
    var panel = CreatePanel(out var viewModel);

    viewModel.SetUpdate("# Just a heading\n\nno fences here\n", isStreaming: false);

    Assert.False(panel.FindControl<ToggleButton>("BtnRenderFences").IsVisible);
}

[AvaloniaFact]
public void FenceSwitch_IsVisible_WhenTheResponseHasAMarkdownFence()
{
    var panel = CreatePanel(out var viewModel);

    viewModel.SetUpdate("```markdown\n# Nested\n```\n", isStreaming: false);

    Assert.True(panel.FindControl<ToggleButton>("BtnRenderFences").IsVisible);
}

[AvaloniaFact]
public void FenceSwitch_UncheckedThenNewContent_KeepsRenderingSource()
{
    var panel = CreatePanel(out var viewModel);
    viewModel.SetUpdate("```markdown\n# First\n```\n", isStreaming: false);

    ToggleButton toggle = panel.FindControl<ToggleButton>("BtnRenderFences");
    toggle.IsChecked = false;
    toggle.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));

    // The choice lives on the view model, so the next content update must not undo it - that is
    // the whole reason the switch is panel-level rather than per-block.
    viewModel.SetUpdate("```markdown\n# Second\n```\n", isStreaming: false);

    Assert.False(viewModel.RenderFencedMarkdown);
    Assert.Contains(
        "# Second",
        panel.FindControl<StackPanel>("MarkdownHost").Children
            .OfType<Control>()
            .SelectMany(TextOf),
        StringComparer.Ordinal);
}
```

The third test needs `using Avalonia.Interactivity;` and a small text-collecting helper; if `AgentOutputPanelTests.cs` has no equivalent, add one mirroring `MarkdownRendererTests.TextOf`:

```csharp
private static IEnumerable<string> TextOf(Control control)
{
    if (control is TextBlock block && block.Text is { Length: > 0 } text)
    {
        yield return text;
    }

    if (control is Panel panel)
    {
        foreach (Control child in panel.Children)
        {
            foreach (string nested in TextOf(child))
            {
                yield return nested;
            }
        }
    }

    if (control is Border { Child: Control inner })
    {
        foreach (string nested in TextOf(inner))
        {
            yield return nested;
        }
    }

    if (control is ContentControl { Content: Control content })
    {
        foreach (string nested in TextOf(content))
        {
            yield return nested;
        }
    }
}
```

- [ ] **Step 6: Run test to verify it fails**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~FenceSwitch"`
Expected: FAIL — no control named `BtnRenderFences`.

- [ ] **Step 7: Add the switch to the header**

In `AgentOutputPanel.axaml`, widen the header grid's columns from `*,Auto,Auto` to `*,Auto,Auto,Auto`, insert the toggle at column 1, and move Copy and Close to columns 2 and 3:

```xml
            <Grid ColumnDefinitions="*,Auto,Auto,Auto" ColumnSpacing="6" Margin="10,8,10,6">
```

```xml
                <ToggleButton x:Name="BtnRenderFences"
                              Grid.Column="1"
                              Content="md"
                              FontSize="11"
                              Padding="7,2"
                              IsChecked="True"
                              IsVisible="False"
                              VerticalAlignment="Top"
                              ToolTip.Tip="Render ```markdown blocks as formatted documents"
                              Click="OnRenderFencesClick"/>
```

Change `BtnCopyAll` to `Grid.Column="2"` and `BtnClose` to `Grid.Column="3"`.

- [ ] **Step 8: Wire the switch in the code-behind**

In `AgentOutputPanel.axaml.cs`, pass the flag and gate the toggle's visibility on the render result:

```csharp
        MarkdownRenderResult rendered = MarkdownRenderer.Build(
            markdown,
            this,
            onCopyText: text => _ = CopyToClipboardAsync(text),
            onOpenLink: url => _ = OpenLinkAsync(url),
            renderFencedMarkdown: _viewModel?.RenderFencedMarkdown ?? true);
        MarkdownHost.Children.Add(rendered.Root);

        // A switch that governs nothing is clutter, so it appears only for a response that
        // actually contains a block it governs.
        BtnRenderFences.IsVisible = rendered.HasTransformBlock;
```

Add the click handler beside `OnCopyAllClick`:

```csharp
    private void OnRenderFencesClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.RenderFencedMarkdown = BtnRenderFences.IsChecked ?? true;
        Render();
    }
```

Extend the property filter at `AgentOutputPanel.axaml.cs:74` so a programmatic change also re-renders:

```csharp
        if (e.PropertyName is nameof(AgentOutputViewModel.MarkdownText)
            or nameof(AgentOutputViewModel.HasContent)
            or nameof(AgentOutputViewModel.RenderFencedMarkdown))
```

- [ ] **Step 9: Run the panel tests to verify they pass**

Run: `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~FenceSwitch"`
Expected: PASS, 3 tests.

- [ ] **Step 10: Run every affected suite**

Run each and expect 0 failed:

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentOutput"
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Controls|FullyQualifiedName~TerminalPane" --blame-hang-timeout 5m
scripts/build.sh test tests/NovaTerminal.VT.Tests
scripts/build.sh test tests/NovaTerminal.Architecture.Tests
```

- [ ] **Step 11: Verify in the running app**

Build and launch from this worktree, then in a pane run a command whose output contains a ` ```markdown ` fence and one containing a ` ```diff ` fence. Confirm: the fence renders as a document, the `md` toggle appears in the panel header, unchecking it shows source, Copy yields raw source in both positions, and a response with no fence shows no toggle.

```bash
scripts/build.sh build src/NovaTerminal.App
```

Launch with an absolute path via `Start-Process`; a `cd`-prefixed background launch has been observed to exit 127 without starting. Close the app before the next build — it locks `NovaTerminal.exe`.

- [ ] **Step 12: Commit**

```bash
git add src/NovaTerminal.App/AgentOutput/ tests/NovaTerminal.App.Tests/AgentOutput/
git commit -m "feat(agent-output): add the panel-level fence rendering switch

One bool on the view model, surfaced as an 'md' toggle in the panel
header and shown only when the current response actually contains a
block it governs.

Panel-level rather than per-block because the panel rebuilds its whole
control tree on every text change: per-block state would reset every
streaming tick, and keying it by block ordinal would reattach a choice
to the wrong block when a block arrives earlier in the stream."
```

---

## Self-review

**Spec coverage.** §1 the seam → Task 3 Steps 4-5, 8. §2 info-string matching → Task 3 Step 5, tested Step 1. §3 markdown handler and depth cap → Task 4, cap constant in Task 3 Step 8. §4 diff handler → Task 3 Steps 3, 6, with the header-before-marker ordering tested. §5 panel switch → Task 5. §6 render result and switch visibility → Task 2 (type), Task 5 Step 8 (gating). §7 targeted refactor → Task 1 (theme extraction), Task 3 (`Fences/` directory). Edge cases: empty body → the unhandled path via `NormalizeInfo` returning empty; casing and aliases → Task 3 Step 1 theory data; malformed diff → the `Foreground` default, tested by the context-line case. Testing section → covered across Tasks 2-5. No spec requirement is without a task.

**Three gaps found and closed while reviewing.** The spec's testing list includes "Copy yields raw source in both switch positions" with no task — added as Task 4 Step 1's fourth test. The spec also asks that the choice "survives a subsequent `MarkdownText` update" and nothing verified it — added as Task 5 Step 5's third panel test, which is the one that actually justifies the switch being panel-level. And the first draft of those panel tests used `[Fact]`; `AgentOutputPanelTests` is `[AvaloniaFact]` throughout because the panel is a XAML `UserControl` whose `InitializeComponent` needs the headless application, so a plain `[Fact]` would have thrown rather than failed usefully.

**Type consistency.** `MarkdownTheme` (Task 1) is the parameter type in `IFenceBody.Build` (Task 3) and both handlers. `MarkdownRenderPass.HasTransformBlock` (Task 2) is what Task 3 Step 8 sets and what `MarkdownRenderResult.HasTransformBlock` (Task 2) carries out to Task 5 Step 8. `renderFencedMarkdown` is the `Build` parameter (Task 2), `MarkdownRenderPass.RenderFencedMarkdown` (Task 2), `FenceContext.RenderFencedMarkdown` (Task 3) and `AgentOutputViewModel.RenderFencedMarkdown` (Task 5) — one concept, one name at every layer. `MaxFenceDepth` is declared once (Task 3 Step 8) and read once (same step).

**One risk the executor should know.** Task 3 Step 8 rewrites the body-construction half of `BuildCodeBlock` while leaving its chrome alone. If #378 takes further review changes to that method, this is where the conflict lands.
