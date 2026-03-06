using NovaTerminal.CommandAssist.Domain;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class SecretsFilterTests
{
    [Theory]
    [InlineData("gh auth login --password hunter2", "gh auth login --password [REDACTED]")]
    [InlineData("curl https://example.test?token=abc123", "curl https://example.test?token=[REDACTED]")]
    [InlineData("sshpass -p hunter2 ssh user@example.com", "sshpass -p [REDACTED] ssh user@example.com")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: Bearer [REDACTED]")]
    [InlineData("export JWT=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.abc.def", "export JWT=[REDACTED]")]
    [InlineData("Server=db;User Id=app;Password=supersecret;", "Server=db;User Id=app;Password=[REDACTED];")]
    public void Redact_WhenCommandContainsKnownSecretPatterns_RedactsSensitiveSegments(string input, string expected)
    {
        var filter = new SecretsFilter();

        RedactionResult result = filter.Redact(input);

        Assert.True(result.WasRedacted);
        Assert.Equal(expected, result.RedactedText);
    }

    [Fact]
    public void Redact_WhenCommandDoesNotContainSecrets_ReturnsOriginalText()
    {
        var filter = new SecretsFilter();

        RedactionResult result = filter.Redact("git status");

        Assert.False(result.WasRedacted);
        Assert.Equal("git status", result.RedactedText);
    }
}
