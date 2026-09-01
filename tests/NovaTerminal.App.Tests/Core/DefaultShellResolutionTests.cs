using NovaTerminal.Pty;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Covers the Unix half of <see cref="ShellHelper.GetDefaultShell"/>, which used to answer
/// from a hardcoded zsh/bash/sh preference order and so opened zsh for a bash user whose
/// machine happened to have zsh installed. It now resolves the user's actual login shell:
/// $SHELL first, then the passwd entry, with the old probe kept only as the floor.
/// </summary>
/// <remarks>
/// The chain takes its environment, filesystem and passwd reads as delegates, so these run
/// identically on every platform — nothing here touches the real ones. Windows's half of
/// GetDefaultShell is untouched and covered by the runnable-shell smoke test.
/// </remarks>
public sealed class DefaultShellResolutionTests
{
    private static Func<string, string?> EnvironmentWith(string? value) => _ => value;

    /// <summary>Only <paramref name="existing"/> passes the existence probe.</summary>
    private static Func<string, bool> OnlyExists(string existing) => path => path == existing;

    [Fact]
    public void EnvironmentShell_Wins_WhenItExists()
    {
        // The whole point: a fish user gets fish, not the first entry of a fixed list.
        string shell = ShellHelper.GetUnixDefaultShell(
            EnvironmentWith("/usr/bin/fish"),
            OnlyExists("/usr/bin/fish"),
            () => "/bin/bash");

        Assert.Equal("/usr/bin/fish", shell);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankEnvironmentShell_IsTreatedAsUnset(string? shell)
    {
        Assert.Equal(
            "/bin/bash",
            ShellHelper.GetUnixDefaultShell(EnvironmentWith(shell), OnlyExists("/bin/bash"), () => "/bin/bash"));
    }

    // A $SHELL left pointing at a since-removed shell must fall through, not be handed to
    // the launcher as launchable.
    [Fact]
    public void StaleEnvironmentShell_FallsThroughToThePasswdAnswer()
    {
        string shell = ShellHelper.GetUnixDefaultShell(
            EnvironmentWith("/opt/homebrew/bin/fish"),
            path => path == "/usr/bin/fish",
            () => "/usr/bin/fish");

        Assert.Equal("/usr/bin/fish", shell);
    }

    [Fact]
    public void Passwd_Answers_WhenTheEnvironmentHasNoShell()
    {
        string shell = ShellHelper.GetUnixDefaultShell(
            EnvironmentWith(null),
            OnlyExists("/usr/bin/fish"),
            () => "/usr/bin/fish");

        Assert.Equal("/usr/bin/fish", shell);
    }

    [Fact]
    public void StalePasswdShell_FallsThroughToTheFloorProbe()
    {
        string shell = ShellHelper.GetUnixDefaultShell(
            EnvironmentWith(null),
            OnlyExists("/bin/zsh"),
            () => "/opt/gone-from-disk/shell");

        Assert.Equal("/bin/zsh", shell);
    }

    // The floor is the old behaviour, byte for byte: zsh, bash, then sh.
    [Fact]
    public void NothingAnswersAnywhere_FallsToBinSh()
    {
        string shell = ShellHelper.GetUnixDefaultShell(
            EnvironmentWith(null),
            _ => false,
            () => null);

        Assert.Equal("/bin/sh", shell);
    }

    [Fact]
    public void FloorProbe_KeepsTheOldPreferenceOrder()
    {
        string shell = ShellHelper.GetUnixDefaultShell(
            EnvironmentWith(null),
            path => path == "/bin/bash",
            () => null);

        Assert.Equal("/bin/bash", shell);
    }

    [Fact]
    public void ProductionChain_StillResolvesARunnableShell()
    {
        // Pins that the production delegates stay wired: a real environment, real filesystem.
        Assert.False(string.IsNullOrWhiteSpace(ShellHelper.GetDefaultShell()));
    }

    // --- ParseLoginShellFromPasswd: the pure half of the passwd lookup. ---

    [Fact]
    public void ParseLoginShell_ReturnsTheSeventhField_OfTheMatchingUser()
    {
        string[] lines =
        {
            "root:x:0:0:root:/root:/bin/bash",
            "svc:x:1000:1000:Service:/home/svc:/usr/sbin/nologin",
        };

        Assert.Equal("/usr/sbin/nologin", ShellHelper.ParseLoginShellFromPasswd(lines, "svc"));
    }

    [Fact]
    public void ParseLoginShell_UnknownUser_ReturnsNull()
    {
        string[] lines = { "root:x:0:0:root:/root:/bin/bash" };

        Assert.Null(ShellHelper.ParseLoginShellFromPasswd(lines, "nobody-here"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLoginShell_BlankUsername_ReturnsNull(string? username)
    {
        string[] lines = { "root:x:0:0:root:/root:/bin/bash" };

        Assert.Null(ShellHelper.ParseLoginShellFromPasswd(lines, username));
    }

    // passwd carries historical noise - comments, and NIS entries that are not real users.
    // A line that does not parse must be skipped, never crash the lookup.
    [Theory]
    [InlineData("# a comment")]
    [InlineData("+@somegroup")]
    [InlineData("no-colons-here")]
    [InlineData("")]
    public void ParseLoginShell_MalformedLines_AreSkipped(string noise)
    {
        string[] lines = { noise, "user:x:1000:1000::/home/user:/usr/bin/fish" };

        Assert.Equal("/usr/bin/fish", ShellHelper.ParseLoginShellFromPasswd(lines, "user"));
    }

    // A user line with too few fields is not a usable entry either.
    [Fact]
    public void ParseLoginShell_TruncatedEntry_IsSkipped()
    {
        string[] lines = { "user:x:1000" };

        Assert.Null(ShellHelper.ParseLoginShellFromPasswd(lines, "user"));
    }
}
