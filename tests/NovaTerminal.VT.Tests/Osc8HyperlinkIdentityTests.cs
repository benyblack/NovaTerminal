using NovaTerminal.VT;
using NovaTerminal.VT.Links;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// #95 gap 2: OSC 8 hyperlink identity.
/// </summary>
/// <remarks>
/// The parser used to keep only the URI — <c>data.Substring(secondSep + 1)</c> — and throw the params
/// field away, so the <c>id</c> that states "these runs are one anchor" never reached the buffer.
///
/// The spec is what these pin, quoting it directly:
///
/// <blockquote>Character cells that have the same target URI and the same nonempty <c>id</c> are always
/// underlined together on mouseover. The same <c>id</c> is only used for connecting character cells whose
/// URIs is also the same.</blockquote>
///
/// So identity is the pair, and neither half alone decides it. Several tests below therefore assert on
/// <see cref="Assert.Same"/> / <see cref="Assert.NotSame"/> rather than on URI equality: a URI-equality
/// implementation passes every "is there a link here" assertion while getting grouping wrong, which is
/// exactly the bug being fixed.
/// </remarks>
public class Osc8HyperlinkIdentityTests
{
    private const string Uri = "https://example.com/a";
    private const string OtherUri = "https://example.com/b";

    private static string Open(string parameters, string uri) => $"\x1b]8;{parameters};{uri}\x1b\\";
    private static string Close() => "\x1b]8;;\x1b\\";

    private static (TerminalBuffer Buffer, AnsiParser Parser) NewTerminal()
    {
        var buffer = new TerminalBuffer(80, 5);
        return (buffer, new AnsiParser(buffer));
    }

    private static Hyperlink? LinkAt(TerminalBuffer buffer, int col)
        => buffer.GetHyperlinkIdentityAbsolute(col, buffer.Scrollback.Count);

    [Fact]
    public void ExplicitId_ConnectsNonContiguousRuns()
    {
        // The spec's motivating case: a single anchor split across two runs, which the producer ties
        // together with an id. Column 0 and column 2 must be one link despite the gap.
        var (buffer, parser) = NewTerminal();

        parser.Process(Open("id=xyz", Uri) + "A" + Close());
        parser.Process(" ");
        parser.Process(Open("id=xyz", Uri) + "B" + Close());

        Hyperlink? first = LinkAt(buffer, 0);
        Hyperlink? second = LinkAt(buffer, 2);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Same(first, second);
        Assert.Null(LinkAt(buffer, 1));
    }

    [Fact]
    public void SameUriDifferentIds_AreDistinctLinks()
    {
        // The discriminating test for this whole change. Two adjacent links to the same target that the
        // producer has explicitly declared separate. Grouping by URI — the only option before identity
        // existed — merges them, and would pass every other assertion in this file.
        var (buffer, parser) = NewTerminal();

        parser.Process(Open("id=one", Uri) + "A" + Close());
        parser.Process(Open("id=two", Uri) + "B" + Close());

        Hyperlink? a = LinkAt(buffer, 0);
        Hyperlink? b = LinkAt(buffer, 1);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Uri, b!.Uri);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void SameIdDifferentUris_AreDistinctLinks()
    {
        // "The same id is only used for connecting character cells whose URIs is also the same."
        // An id is not a global handle; reusing one against a different target must not connect them.
        var (buffer, parser) = NewTerminal();

        parser.Process(Open("id=shared", Uri) + "A" + Close());
        parser.Process(Open("id=shared", OtherUri) + "B" + Close());

        Hyperlink? a = LinkAt(buffer, 0);
        Hyperlink? b = LinkAt(buffer, 1);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        Assert.Equal(Uri, a!.Uri);
        Assert.Equal(OtherUri, b!.Uri);
    }

    [Fact]
    public void CellsWrittenInOneRun_ShareIdentity()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process(Open(string.Empty, Uri) + "AB" + Close());

        Hyperlink? a = LinkAt(buffer, 0);
        Hyperlink? b = LinkAt(buffer, 1);

        Assert.NotNull(a);
        Assert.Same(a, b);
    }

    [Fact]
    public void SeparateRunsWithoutAnId_AreDistinctLinks()
    {
        // With no id the spec permits two heuristics; we take VTE's, which the spec recommends: a fresh
        // identity per OSC 8 open. So two runs to the same URI are two links, and the adjacent-and-equal
        // ambiguity that iTerm2's heuristic has to guess about never arises.
        var (buffer, parser) = NewTerminal();

        parser.Process(Open(string.Empty, Uri) + "A" + Close());
        parser.Process(Open(string.Empty, Uri) + "B" + Close());

        Hyperlink? a = LinkAt(buffer, 0);
        Hyperlink? b = LinkAt(buffer, 1);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Uri, b!.Uri);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void CloseSequence_EndsTheLink()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process(Open("id=xyz", Uri) + "A" + Close() + "B");

        Assert.NotNull(LinkAt(buffer, 0));
        Assert.Null(LinkAt(buffer, 1));
    }

    [Fact]
    public void SwitchingLinkWithoutClosing_IsHonoured()
    {
        // Legal per the spec: OSC 8 just reassigns the attribute, like a colour. No close required.
        var (buffer, parser) = NewTerminal();

        parser.Process(Open("id=one", Uri) + "A" + Open("id=two", OtherUri) + "B");

        Assert.Equal(Uri, LinkAt(buffer, 0)?.Uri);
        Assert.Equal(OtherUri, LinkAt(buffer, 1)?.Uri);
    }

    [Theory]
    // "For hyperlink cells that do not have an id (or have an empty id, these two are interchangeable)".
    [InlineData("id=")]
    [InlineData("")]
    [InlineData("id=:foo=bar")]
    public void EmptyOrAbsentId_BehavesAsNoId(string parameters)
    {
        var (buffer, parser) = NewTerminal();

        parser.Process(Open(parameters, Uri) + "A" + Close());
        parser.Process(Open(parameters, Uri) + "B" + Close());

        Hyperlink? a = LinkAt(buffer, 0);
        Hyperlink? b = LinkAt(buffer, 1);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(a!.Id);
        // No id means no cross-run grouping, so these must not be the same link.
        Assert.NotSame(a, b);
    }

    [Theory]
    // The field exists for future extension, so an unrecognised key must be skipped rather than treated
    // as an error that discards the id sitting next to it.
    [InlineData("foo=bar:id=keep")]
    [InlineData("id=keep:foo=bar")]
    [InlineData("a=1:id=keep:b=2")]
    public void UnknownParameters_DoNotHideTheId(string parameters)
    {
        var (buffer, parser) = NewTerminal();

        parser.Process(Open(parameters, Uri) + "A" + Close());
        parser.Process(Open("id=keep", Uri) + "B" + Close());

        Hyperlink? a = LinkAt(buffer, 0);
        Hyperlink? b = LinkAt(buffer, 1);

        Assert.NotNull(a);
        Assert.Equal("keep", a!.Id);
        // Same (URI, id) reached by two different param spellings is still one link.
        Assert.Same(a, b);
    }

    [Fact]
    public void OverlongUri_IsRefusedRatherThanTruncated()
    {
        // OSC 8 is remote-controlled input. A truncated URI points somewhere other than intended, which
        // is worse than no link, so an over-long target yields no link at all.
        var (buffer, parser) = NewTerminal();
        string huge = "https://example.com/" + new string('x', HyperlinkRegistry.MaxUriLength);

        parser.Process(Open(string.Empty, huge) + "A");

        Assert.Null(LinkAt(buffer, 0));
    }

    [Fact]
    public void OverlongId_DowngradesToNoIdButKeepsTheLink()
    {
        // Losing an over-long id costs grouping across runs; losing the link would cost the user the
        // target. Degrade the cheaper one.
        var (buffer, parser) = NewTerminal();
        string longId = new string('i', HyperlinkRegistry.MaxIdLength + 1);

        parser.Process(Open($"id={longId}", Uri) + "A" + Close());
        parser.Process(Open($"id={longId}", Uri) + "B" + Close());

        Hyperlink? a = LinkAt(buffer, 0);
        Hyperlink? b = LinkAt(buffer, 1);

        Assert.NotNull(a);
        Assert.Equal(Uri, a!.Uri);
        Assert.Null(a.Id);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void IdentitySurvivesScrollback()
    {
        // The side table already survived into paged scrollback (#164 / gap 1). What matters here is that
        // the *identity* survives too, not just a URI: a link that scrolls off screen must still group
        // with its other half on screen.
        var (buffer, parser) = NewTerminal();

        parser.Process(Open("id=xyz", Uri) + "A" + Close() + "\r\n");
        for (int i = 0; i < 12; i++)
        {
            parser.Process($"filler {i}\r\n");
        }

        parser.Process(Open("id=xyz", Uri) + "B" + Close());

        // The second half is wherever the cursor ended up after all that scrolling, not viewport row 0 —
        // reading row 0 lands on a filler line.
        Hyperlink? scrolled = buffer.GetHyperlinkIdentityAbsolute(0, 0);
        Hyperlink? onScreen = buffer.GetHyperlinkIdentityAbsolute(0, buffer.Scrollback.Count + buffer.CursorRow);

        Assert.NotNull(scrolled);
        Assert.NotNull(onScreen);
        Assert.Same(scrolled, onScreen);
    }

    [Fact]
    public void TwoTerminals_DoNotShareLinkIdentities()
    {
        // The registry is per-parser on purpose. If it were static, the same (URI, id) written in two
        // panes would group across them, and hovering one would highlight the other.
        var (bufferA, parserA) = NewTerminal();
        var (bufferB, parserB) = NewTerminal();

        parserA.Process(Open("id=xyz", Uri) + "A" + Close());
        parserB.Process(Open("id=xyz", Uri) + "B" + Close());

        Hyperlink? a = LinkAt(bufferA, 0);
        Hyperlink? b = LinkAt(bufferB, 0);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void UriOnlyAccessor_StillReturnsTheUri()
    {
        // The App layer reads URIs through this shim; the type change must not disturb it.
        var (buffer, parser) = NewTerminal();

        parser.Process(Open("id=xyz", Uri) + "A" + Close());

        Assert.Equal(Uri, buffer.GetHyperlinkAbsolute(0, buffer.Scrollback.Count));
    }
}
