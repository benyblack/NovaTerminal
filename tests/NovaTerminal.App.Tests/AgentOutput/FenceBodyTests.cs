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
    [InlineData("+++counter;", "NtGreen")]
    [InlineData("---x;", "NtRed")]
    [InlineData("+++", "Secondary")]
    [InlineData("---", "Secondary")]
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
