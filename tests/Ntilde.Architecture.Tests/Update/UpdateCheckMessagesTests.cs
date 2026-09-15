using System;
using NovaTerminal.Update;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Same home as <see cref="UpdateCoordinatorTests"/> for the same reason: this project is in the
/// gating CI loop that <c>ci.yml</c> and <c>release.yml</c> run, while <c>App.Tests</c> is not.
/// These pin the wording contract shared by the palette's toasts and the About window's inline
/// result - both render through <see cref="UpdateCheckMessages"/>, so a regression here blocks
/// the build before either surface can drift from the other.
/// </summary>
public class UpdateCheckMessagesTests
{
    [Fact]
    public void Update_ready_names_the_staged_version_and_offers_a_restart()
    {
        var message = UpdateCheckMessages.OutcomeMessage(UpdateCheckOutcome.UpdateReady, "0.8.0");

        Assert.Equal("Update ready", UpdateCheckMessages.OutcomeTitle(UpdateCheckOutcome.UpdateReady));
        Assert.Contains("0.8.0", message);
        Assert.True(UpdateCheckMessages.OutcomeOffersRestart(UpdateCheckOutcome.UpdateReady));
    }

    [Fact]
    public void Update_ready_tolerates_a_missing_staged_version()
    {
        // Outcome's contract guarantees a non-blank version, but the message is rendered from the
        // coordinator's StagedVersion passed through callers that may hand back null; it must stay
        // presentable rather than emit "NovaTerminal  is downloaded...".
        var message = UpdateCheckMessages.OutcomeMessage(UpdateCheckOutcome.UpdateReady, null);

        Assert.DoesNotContain("  ", message);
    }

    [Fact]
    public void Unsupported_points_at_the_releases_page_and_offers_no_restart()
    {
        var message = UpdateCheckMessages.OutcomeMessage(UpdateCheckOutcome.Unsupported, null);

        Assert.Contains("releases", message);
        Assert.False(UpdateCheckMessages.OutcomeOffersRestart(UpdateCheckOutcome.Unsupported));
    }

    [Fact]
    public void The_plain_answers_keep_their_words_and_no_restart()
    {
        Assert.Equal("Up to date", UpdateCheckMessages.OutcomeTitle(UpdateCheckOutcome.UpToDate));
        Assert.Equal("You are running the newest version.",
            UpdateCheckMessages.OutcomeMessage(UpdateCheckOutcome.UpToDate, null));
        Assert.False(UpdateCheckMessages.OutcomeOffersRestart(UpdateCheckOutcome.UpToDate));

        Assert.Equal("Update check failed", UpdateCheckMessages.OutcomeTitle(UpdateCheckOutcome.Failed));
        Assert.Contains("GitHub", UpdateCheckMessages.OutcomeMessage(UpdateCheckOutcome.Failed, null));
        Assert.False(UpdateCheckMessages.OutcomeOffersRestart(UpdateCheckOutcome.Failed));
    }

    [Fact]
    public void Every_outcome_has_a_title_and_a_message()
    {
        foreach (UpdateCheckOutcome outcome in Enum.GetValues<UpdateCheckOutcome>())
        {
            Assert.False(string.IsNullOrWhiteSpace(UpdateCheckMessages.OutcomeTitle(outcome)), outcome.ToString());
            Assert.False(string.IsNullOrWhiteSpace(UpdateCheckMessages.OutcomeMessage(outcome, "9.9.9")), outcome.ToString());
        }
    }

    [Fact]
    public void The_in_flight_and_broken_coordinator_answers_are_presentable()
    {
        Assert.False(string.IsNullOrWhiteSpace(UpdateCheckMessages.AlreadyRunningTitle));
        Assert.False(string.IsNullOrWhiteSpace(UpdateCheckMessages.AlreadyRunningMessage));
        Assert.False(string.IsNullOrWhiteSpace(UpdateCheckMessages.CoordinatorUnavailableTitle));
        Assert.False(string.IsNullOrWhiteSpace(UpdateCheckMessages.CoordinatorUnavailableMessage));
    }
}
