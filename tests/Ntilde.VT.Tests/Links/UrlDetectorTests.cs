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

    [Fact]
    public void Longest_match_wins_when_two_rules_share_a_start()
    {
        // Two rules match at index 0 with different lengths; dedup must keep the longer span.
        var rules = new[]
        {
            new LinkRule("short", new System.Text.RegularExpressions.Regex("foo"), t => "S:" + t),
            new LinkRule("long", new System.Text.RegularExpressions.Regex("foobar"), t => "L:" + t),
        };
        var s = Assert.Single(new UrlDetector(rules).Detect("foobar"));
        Assert.Equal("L:foobar", s.Uri);
        Assert.Equal(0, s.StartChar);
        Assert.Equal(6, s.EndChar);
    }
}
