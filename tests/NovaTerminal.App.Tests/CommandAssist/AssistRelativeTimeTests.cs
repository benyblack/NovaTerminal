using NovaTerminal.CommandAssist.Domain;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The relative-time captions that replaced <c>Used 2026-08-04 15:23</c> on history rows.
/// </summary>
public sealed class AssistRelativeTimeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(90, "1m ago")]
    [InlineData(60 * 2, "2m ago")]
    [InlineData(60 * 59, "59m ago")]
    [InlineData(60 * 60, "1h ago")]
    [InlineData(60 * 60 * 5, "5h ago")]
    [InlineData(60 * 60 * 25, "yesterday")]
    [InlineData(60 * 60 * 47, "yesterday")]
    [InlineData(60 * 60 * 49, "2d ago")]
    [InlineData(60 * 60 * 24 * 6, "6d ago")]
    public void Format_RendersTheAgeInTheCoarsestUnitThatStillPlacesIt(int ageSeconds, string expected)
    {
        Assert.Equal(expected, AssistRelativeTime.Format(Now.AddSeconds(-ageSeconds), Now));
    }

    /// <summary>
    /// Past a week the age stops being placeable and a date is what someone would look for.
    /// </summary>
    [Fact]
    public void Format_BeyondAWeek_FallsBackToADate()
    {
        Assert.Equal("2026-07-20", AssistRelativeTime.Format(Now.AddDays(-17), Now));
    }

    /// <summary>
    /// Clock skew against an SSH host is common and is not the user's problem.
    /// </summary>
    [Fact]
    public void Format_WhenTheEntryIsInTheFuture_ReadsAsJustNow()
    {
        Assert.Equal("just now", AssistRelativeTime.Format(Now.AddMinutes(4), Now));
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(60 * 14, true)]
    [InlineData(60 * 16, false)]
    [InlineData(60 * 60 * 24, false)]
    public void IsRecent_MarksOnlyTheCurrentSitting(int ageSeconds, bool expected)
    {
        Assert.Equal(expected, AssistRelativeTime.IsRecent(Now.AddSeconds(-ageSeconds), Now));
    }
}
