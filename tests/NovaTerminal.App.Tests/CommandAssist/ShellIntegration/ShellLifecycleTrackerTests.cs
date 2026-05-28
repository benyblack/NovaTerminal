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
