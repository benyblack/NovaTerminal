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
    public void UnrecognisedPolicyBehavesAsNever(string? policy)
    {
        // A typo in a hand-edited settings file must not be more destructive than the default,
        // so the fall-through stays at Never even though the default is now Graceful: an
        // unreadable policy keeps the pane rather than closing it.
        Assert.False(NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh: false, exitCode: 0));
        Assert.False(NovaTerminal.MainWindow.ShouldClosePaneOnExit(policy, isSsh: false, exitCode: 1));
    }

    [Fact]
    public void DefaultSettingIsGraceful()
    {
        // The default waited on exit-code fidelity, and #313 (Windows) then #323 (Unix) delivered
        // it: an exit code now really is the child's status rather than an assumed 0, so
        // "cleanly" is something this policy can actually tell. A clean exit closes the pane and
        // anything else keeps it with the Enter-to-restart banner.
        Assert.Equal("Graceful", new TerminalSettings().ShellExitPolicy);
    }
}
