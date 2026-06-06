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
