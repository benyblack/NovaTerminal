using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The App-side "does full help exist here" probe (V2 Phase 4b, Phase 4 task 3, source (b)).
/// </summary>
/// <remarks>
/// Driven entirely through the internal seam that supplies the two filesystem answers. That is the
/// point of the seam: the production implementation reads <c>PATH</c> and a man tree, neither of
/// which a test can pin down, and the interesting behavior - which command each shell is offered,
/// and when nothing is - is a decision made on top of those two booleans.
/// </remarks>
public sealed class LocalCommandHelpProbeTests
{
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    [InlineData("PowerShell")]
    public void PowerShell_is_offered_Get_Help_without_touching_the_filesystem(string shellKind)
    {
        // Get-Help answers for cmdlets, functions, aliases and executables alike, and a cmdlet is not
        // a file - so a PATH check here would be answering the wrong question.
        var probe = new LocalCommandHelpProbe(
            executableExists: _ => throw new InvalidOperationException("PATH must not be consulted."),
            manPageExists: _ => throw new InvalidOperationException("MANPATH must not be consulted."));

        CommandHelpProbeResult? result = probe.Probe("Get-ChildItem", shellKind);

        Assert.Equal("Get-Help Get-ChildItem", result!.Value.Command);
    }

    [Fact]
    public void A_posix_shell_prefers_the_manual_page()
    {
        var probe = new LocalCommandHelpProbe(executableExists: _ => true, manPageExists: _ => true);

        CommandHelpProbeResult? result = probe.Probe("tar", "bash");

        Assert.Equal("man tar", result!.Value.Command);
    }

    [Fact]
    public void A_posix_shell_falls_back_to_help_when_there_is_no_manual_page()
    {
        // The large population of modern tools that ship no man page.
        var probe = new LocalCommandHelpProbe(executableExists: _ => true, manPageExists: _ => false);

        CommandHelpProbeResult? result = probe.Probe("rg", "zsh");

        Assert.Equal("rg --help", result!.Value.Command);
    }

    [Fact]
    public void Nothing_is_offered_when_the_command_is_not_installed()
    {
        var probe = new LocalCommandHelpProbe(executableExists: _ => false, manPageExists: _ => false);

        Assert.Null(probe.Probe("kubectl", "bash"));
    }

    [Fact]
    public void Cmd_is_offered_the_slash_question_form()
    {
        // Not --help, which most Windows console tools do not understand.
        var probe = new LocalCommandHelpProbe(executableExists: _ => true, manPageExists: _ => true);

        CommandHelpProbeResult? result = probe.Probe("robocopy", "cmd");

        Assert.Equal("robocopy /?", result!.Value.Command);
    }

    [Fact]
    public void Cmd_offers_nothing_when_the_command_is_not_on_PATH()
    {
        var probe = new LocalCommandHelpProbe(executableExists: _ => false, manPageExists: _ => true);

        Assert.Null(probe.Probe("frobnicate", "cmd"));
    }

    [Fact]
    public void An_unknown_shell_is_treated_as_posix()
    {
        // Every SSH session is a session whose shell kind is hardest to know. Offering `man tar`
        // there costs far less than offering nothing.
        var probe = new LocalCommandHelpProbe(executableExists: _ => true, manPageExists: _ => true);

        CommandHelpProbeResult? result = probe.Probe("tar", shellKind: null);

        Assert.Equal("man tar", result!.Value.Command);
    }

    [Fact]
    public void A_two_token_command_probes_the_executable_but_offers_the_whole_thing()
    {
        // There is no git-rebase executable, but `man git-rebase` is real and is a better answer
        // than help for `git`.
        string? probedExecutable = null;
        string? probedManPage = null;
        var probe = new LocalCommandHelpProbe(
            executableExists: name => { probedExecutable = name; return true; },
            manPageExists: name => { probedManPage = name; return false; });

        CommandHelpProbeResult? result = probe.Probe("git rebase", "bash");

        Assert.Equal("git rebase --help", result!.Value.Command);
        Assert.Equal("git", probedExecutable);
        Assert.Equal("git-rebase", probedManPage);
    }

    [Fact]
    public void Results_are_cached_per_token_and_shell()
    {
        int calls = 0;
        var probe = new LocalCommandHelpProbe(
            executableExists: _ => { calls++; return true; },
            manPageExists: _ => false);

        probe.Probe("rg", "bash");
        probe.Probe("rg", "bash");
        probe.Probe("rg", "bash");

        Assert.Equal(1, calls);

        // A different shell is a different question, so it is a different cache entry.
        probe.Probe("rg", "zsh");
        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_blank_token_is_not_probed()
    {
        var probe = new LocalCommandHelpProbe(
            executableExists: _ => throw new InvalidOperationException("Nothing to probe."),
            manPageExists: _ => throw new InvalidOperationException("Nothing to probe."));

        Assert.Null(probe.Probe("   ", "bash"));
    }
}
