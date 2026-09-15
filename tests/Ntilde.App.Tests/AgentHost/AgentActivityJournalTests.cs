using System;
using System.Linq;
using NovaTerminal.AgentHost;

namespace NovaTerminal.AppTests.AgentHost;

public class AgentActivityJournalTests
{
    [Fact]
    public void Record_appends_newest_first_and_raises_event()
    {
        var clock = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var journal = new AgentActivityJournal(() => clock);
        int raised = 0;
        journal.EntryAdded += _ => raised++;

        var pane = Guid.NewGuid();
        journal.Record("sendInput", pane, "Profile", "ok");
        journal.Record("sendInput", pane, "Profile", "actDisabled");

        Assert.Equal(2, raised);
        var entries = journal.Snapshot();
        Assert.Equal("actDisabled", entries[0].Outcome); // newest first
        Assert.Equal("ok", entries[1].Outcome);
        Assert.Equal(pane, entries[0].PaneId);
        Assert.Equal(clock, entries[0].TimestampUtc);
    }

    [Fact]
    public void Ring_is_bounded_to_capacity()
    {
        var journal = new AgentActivityJournal();
        for (int i = 0; i < AgentActivityJournal.Capacity + 50; i++)
        {
            journal.Record("sendInput", null, i.ToString(), "ok");
        }

        Assert.Equal(AgentActivityJournal.Capacity, journal.Count);
        var entries = journal.Snapshot();
        Assert.Equal(AgentActivityJournal.Capacity, entries.Count);
        // Newest entry is the last recorded; oldest survivor is entry #50.
        Assert.Equal((AgentActivityJournal.Capacity + 49).ToString(), entries[0].Target);
        Assert.Equal("50", entries[^1].Target);
    }
}
