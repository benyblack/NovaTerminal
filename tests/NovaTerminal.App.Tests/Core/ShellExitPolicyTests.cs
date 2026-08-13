using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// #311: which shell exits close the pane. Pure policy — no window, no pane, no Avalonia,
/// mirroring how <see cref="TabClosePolicyTests"/> covers the sibling pane-close policy.
/// </summary>
public sealed class ShellExitPolicyTests
{
    [Theory]
    // Graceful (the default): a clean exit closes, anything else leaves the pane with a banner.
    [InlineData("Graceful", 0, false, true)]
    [InlineData("Graceful", 1, false, false)]
    [InlineData("Graceful", 255, false, false)]
    // Never: nothing ever closes on its own.
    [InlineData("Never", 0, false, false)]
    [InlineData("Never", 1, false, false)]
    // Always: the exit code stops mattering.
    [InlineData("Always", 0, false, true)]
    [InlineData("Always", 1, false, true)]
    // SSH panes never auto-close, whatever the policy says.
    [InlineData("Graceful", 0, true, false)]
    [InlineData("Always", 0, true, false)]
    [InlineData("Always", 1, true, false)]
    public void PolicyDecidesWhetherTheDyingPaneCloses(string policy, int exitCode, bool isSsh, bool expected)
    {
        Assert.Equal(expected, NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh, exitCode));
    }

    [Theory]
    [InlineData("graceful")]
    [InlineData("  Graceful  ")]
    [InlineData("ALWAYS")]
    public void PolicyMatchingIsCaseAndWhitespaceInsensitive(string policy)
    {
        // "ALWAYS" closes on a non-zero code; the two Graceful spellings do not.
        bool expected = policy.Trim().Equals("ALWAYS", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh: false, exitCode: 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Sometimes")]
    public void UnrecognisedPolicyBehavesAsGraceful(string? policy)
    {
        // A typo in a hand-edited settings file must not silently mean "never tell me anything".
        Assert.True(NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh: false, exitCode: 0));
        Assert.False(NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh: false, exitCode: 1));
    }

    [Fact]
    public void DefaultSettingIsNever()
    {
        // Until #313 lands and the real exit status can be captured, defaulting to Never
        // is conservative: every dead local pane gets the banner with its Enter-to-restart hint.
        Assert.Equal("Never", new TerminalSettings().ShellExitPolicy);
    }
}
