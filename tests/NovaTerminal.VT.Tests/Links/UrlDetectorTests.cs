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
