using System;
using NovaTerminal.Shell;

public sealed class TabStatusTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Continuous output T0..T0+seconds at 1s intervals (every gap < WorkingWindow).</summary>
    private static TabStatusTracker TrackerWithBurst(int seconds)
    {
        var tracker = new TabStatusTracker();
        for (int s = 0; s <= seconds; s++) tracker.NoteOutput(T0.AddSeconds(s));
        return tracker;
    }

    [Fact]
    public void FreshTracker_IsIdle()
        => Assert.Equal(TabTrackerStatus.Idle, new TabStatusTracker().Evaluate(T0, isSelected: false));

    [Fact]
    public void RecentOutput_IsWorking_EvenWhenSelected()
    {
        var tracker = new TabStatusTracker();
        tracker.NoteOutput(T0);
        Assert.Equal(TabTrackerStatus.Working, tracker.Evaluate(T0.AddSeconds(1), isSelected: true));
    }

    [Fact]
    public void LongBurstGoesQuietWhileUnselected_RaisesAttention()
    {
        var tracker = TrackerWithBurst(6); // burst span 6s >= MinAttentionBurst (5s)
        Assert.Equal(TabTrackerStatus.Attention, tracker.Evaluate(T0.AddSeconds(9), isSelected: false));
    }

    [Fact]
    public void ShortBurstGoesQuiet_StaysIdle() // e.g. restored tab printing its prompt
    {
        var tracker = TrackerWithBurst(0);
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0.AddSeconds(9), isSelected: false));
    }

    [Fact]
    public void LongBurstGoesQuietWhileSelected_StaysIdle() // user was watching; nothing to flag
    {
        var tracker = TrackerWithBurst(6);
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0.AddSeconds(9), isSelected: true));
    }

    [Fact]
    public void Attention_IsSticky_AcrossEvaluations()
    {
        var tracker = TrackerWithBurst(6);
        tracker.Evaluate(T0.AddSeconds(9), isSelected: false);
        Assert.Equal(TabTrackerStatus.Attention, tracker.Evaluate(T0.AddSeconds(60), isSelected: false));
    }

    [Fact]
    public void SelectingTab_ClearsAttention()
    {
        var tracker = TrackerWithBurst(6);
        tracker.Evaluate(T0.AddSeconds(9), isSelected: false);
        tracker.NoteSelected();
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0.AddSeconds(10), isSelected: false));
    }

    [Fact]
    public void EvaluatingAsSelected_AlsoClearsAttention()
    {
        var tracker = TrackerWithBurst(6);
        tracker.Evaluate(T0.AddSeconds(9), isSelected: false);
        tracker.Evaluate(T0.AddSeconds(10), isSelected: true);
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0.AddSeconds(11), isSelected: false));
    }

    [Fact]
    public void Bell_RaisesAttention_WithoutAnyOutput()
    {
        var tracker = new TabStatusTracker();
        tracker.NoteBell();
        Assert.Equal(TabTrackerStatus.Attention, tracker.Evaluate(T0, isSelected: false));
    }

    [Fact]
    public void NewBurstAfterAttention_ShowsWorkingAgain()
    {
        var tracker = TrackerWithBurst(6);
        tracker.Evaluate(T0.AddSeconds(9), isSelected: false); // Attention armed
        tracker.NoteOutput(T0.AddSeconds(20));
        Assert.Equal(TabTrackerStatus.Working, tracker.Evaluate(T0.AddSeconds(21), isSelected: false));
    }
}
