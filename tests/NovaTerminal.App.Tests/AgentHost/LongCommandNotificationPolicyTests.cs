using System;
using NovaTerminal.Shell;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>Rules for the long-command completion toast (A2 PR4).</summary>
public class LongCommandNotificationPolicyTests
{
    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(3600, true)]
    public void Qualifies_exactly_at_the_documented_threshold(int seconds, bool expected)
    {
        Assert.Equal(expected, LongCommandNotificationPolicy.QualifiesAsLong(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(false, true, true, false)]   // disabled: never
    [InlineData(false, false, false, false)] // disabled: never
    [InlineData(true, true, true, false)]    // user is looking right at it
    [InlineData(true, true, false, true)]    // different pane
    [InlineData(true, false, true, true)]    // window in background
    [InlineData(true, false, false, true)]   // background + different pane
    public void Notifies_only_when_enabled_and_not_watching(bool enabled, bool windowActive, bool isCurrentPane, bool expected)
    {
        Assert.Equal(expected, LongCommandNotificationPolicy.ShouldNotify(enabled, windowActive, isCurrentPane));
    }

    [Theory]
    [InlineData(42, "42s")]
    [InlineData(185, "3m 5s")]
    [InlineData(4320, "1h 12m")]
    public void Durations_format_compactly(int seconds, string expected)
    {
        Assert.Equal(expected, LongCommandNotificationPolicy.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Message_includes_command_exit_duration_and_pane()
    {
        var text = LongCommandNotificationPolicy.BuildMessage("cargo build", 0, TimeSpan.FromSeconds(185), "Bash · repo");
        Assert.Equal("cargo build — exit 0 after 3m 5s (Bash · repo)", text);
    }

    [Fact]
    public void Message_degrades_gracefully_without_command_or_exit_code()
    {
        var text = LongCommandNotificationPolicy.BuildMessage(null, null, TimeSpan.FromSeconds(42), "Bash");
        Assert.Equal("Command — exit ? after 42s (Bash)", text);
    }
}
