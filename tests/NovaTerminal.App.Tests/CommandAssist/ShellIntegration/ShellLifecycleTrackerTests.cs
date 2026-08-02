using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.Runtime;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class ShellLifecycleTrackerTests
{
    [Fact]
    public void HandleCommandStartedThenFinished_EmitsStructuredEventsWithDuration()
    {
        DateTimeOffset now = new(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);
        var tracker = new ShellLifecycleTracker(() => now);
        var events = new List<ShellIntegrationEvent>();
        tracker.EventObserved += events.Add;

        tracker.HandleWorkingDirectoryChanged("/repo");
        tracker.HandleCommandStarted();
        now = now.AddSeconds(2);
        tracker.HandleCommandFinished(17);

        Assert.Collection(
            events,
            evt =>
            {
                Assert.Equal(ShellIntegrationEventType.WorkingDirectoryChanged, evt.Type);
                Assert.Equal("/repo", evt.WorkingDirectory);
                Assert.Null(evt.ExitCode);
                Assert.Null(evt.Duration);
            },
            evt =>
            {
                Assert.Equal(ShellIntegrationEventType.CommandStarted, evt.Type);
                Assert.Equal("/repo", evt.WorkingDirectory);
                Assert.Null(evt.ExitCode);
                Assert.Null(evt.Duration);
            },
            evt =>
            {
                Assert.Equal(ShellIntegrationEventType.CommandFinished, evt.Type);
                Assert.Equal("/repo", evt.WorkingDirectory);
                Assert.Equal(17, evt.ExitCode);
                Assert.Equal(TimeSpan.FromSeconds(2), evt.Duration);
            });
    }

    [Fact]
    public void HandleCommandStarted_CarriesTheMarkPositionOnTheEmittedEvent()
    {
        var tracker = new ShellLifecycleTracker();
        var events = new List<ShellIntegrationEvent>();
        tracker.EventObserved += events.Add;

        tracker.HandleWorkingDirectoryChanged("/repo");
        tracker.HandleCommandStarted(new ShellMarkPosition(
            Row: 42, Column: 17, AbsoluteRow: 1042, IsAltScreen: false));

        var started = Assert.Single(events, e => e.Type == ShellIntegrationEventType.CommandStarted);
        Assert.NotNull(started.MarkPosition);
        ShellMarkPosition position = started.MarkPosition.Value;
        Assert.Equal(42, position.Row);
        Assert.Equal(17, position.Column);
        Assert.Equal(1042L, position.AbsoluteRow);
        Assert.False(position.IsAltScreen);
        Assert.Equal("/repo", started.WorkingDirectory);
    }

    [Fact]
    public void HandleCommandStarted_WithoutAPosition_EmitsANullMarkPosition()
    {
        // Marks from integrations that predate position capture (and every
        // other event type) simply leave the field null.
        var tracker = new ShellLifecycleTracker();
        var events = new List<ShellIntegrationEvent>();
        tracker.EventObserved += events.Add;

        tracker.HandleCommandStarted();
        tracker.HandlePromptReady();
        tracker.HandleCommandAccepted("git status");

        Assert.All(events, e => Assert.Null(e.MarkPosition));
    }

    [Fact]
    public void HandleCommandAccepted_RestartsTheDurationFallbackClock()
    {
        // OSC 133;B now fires at every prompt, so the clock it starts would
        // bill the user's typing time to the command. C is the real
        // execution-start edge and must win.
        DateTimeOffset now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var tracker = new ShellLifecycleTracker(() => now);
        var events = new List<ShellIntegrationEvent>();
        tracker.EventObserved += events.Add;

        tracker.HandleCommandStarted(new ShellMarkPosition(0, 5, 0, false));
        now = now.AddSeconds(30); // user typing at the prompt
        tracker.HandleCommandAccepted("sleep 1");
        now = now.AddSeconds(1);
        tracker.HandleCommandFinished(exitCode: 0);

        var finished = Assert.Single(events, e => e.Type == ShellIntegrationEventType.CommandFinished);
        Assert.Equal(TimeSpan.FromSeconds(1), finished.Duration);
    }

    [Fact]
    public void HandleCommandFinishedWithoutStart_EmitsFinishedWithoutDuration()
    {
        var tracker = new ShellLifecycleTracker();
        var events = new List<ShellIntegrationEvent>();
        tracker.EventObserved += events.Add;

        tracker.HandleWorkingDirectoryChanged("/repo");
        tracker.HandleCommandFinished(0);

        Assert.Collection(
            events,
            evt => Assert.Equal(ShellIntegrationEventType.WorkingDirectoryChanged, evt.Type),
            evt =>
            {
                Assert.Equal(ShellIntegrationEventType.CommandFinished, evt.Type);
                Assert.Equal("/repo", evt.WorkingDirectory);
                Assert.Equal(0, evt.ExitCode);
                Assert.Null(evt.Duration);
            });
    }

    [Fact]
    public void HandlePromptReadyAndCommandAccepted_EmitsStructuredEventsWithCurrentWorkingDirectory()
    {
        var tracker = new ShellLifecycleTracker();
        var events = new List<ShellIntegrationEvent>();
        tracker.EventObserved += events.Add;

        tracker.HandleWorkingDirectoryChanged("/repo");
        tracker.HandlePromptReady();
        tracker.HandleCommandAccepted("git status");

        Assert.Collection(
            events,
            evt => Assert.Equal(ShellIntegrationEventType.WorkingDirectoryChanged, evt.Type),
            evt =>
            {
                Assert.Equal(ShellIntegrationEventType.PromptReady, evt.Type);
                Assert.Equal("/repo", evt.WorkingDirectory);
            },
            evt =>
            {
                Assert.Equal(ShellIntegrationEventType.CommandAccepted, evt.Type);
                Assert.Equal("/repo", evt.WorkingDirectory);
                Assert.Equal("git status", evt.CommandText);
            });
    }
}
