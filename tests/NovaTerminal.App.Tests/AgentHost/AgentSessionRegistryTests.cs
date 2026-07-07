using System;
using System.Linq;
using NovaTerminal.AgentHost;
using NovaTerminal.VT;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Pure unit tests for the agent-session registry (PR2 of milestone A1,
/// docs/plans/2026-07-07-agent-host-a1-observe-design.md). No Avalonia
/// involvement: registrations are built directly against a TerminalBuffer,
/// exactly as TerminalPane.SetupCommon does.
/// </summary>
public class AgentSessionRegistryTests
{
    private static AgentSessionRegistration MakeRegistration(
        Guid? paneId = null,
        TerminalBuffer? buffer = null,
        string title = "title",
        string profileName = "Profile",
        string kind = "local",
        bool isActive = false)
    {
        return new AgentSessionRegistration
        {
            PaneId = paneId ?? Guid.NewGuid(),
            Buffer = buffer ?? new TerminalBuffer(80, 24),
            TitleProvider = () => title,
            ProfileNameProvider = () => profileName,
            KindProvider = () => kind,
            IsActiveProvider = () => isActive,
        };
    }

    [Fact]
    public void Register_then_list_exposes_the_session()
    {
        var registry = new AgentSessionRegistry();
        var id = Guid.NewGuid();
        Assert.True(registry.Register(MakeRegistration(id, new TerminalBuffer(120, 30), title: "vim", kind: "ssh", isActive: true)));

        var sessions = registry.ListSessions();

        var session = Assert.Single(sessions);
        Assert.Equal(id, session.PaneId);
        Assert.Equal("vim", session.Title);
        Assert.Equal("ssh", session.Kind);
        Assert.Equal(30, session.Rows);
        Assert.Equal(120, session.Cols);
        Assert.True(session.IsActive);
    }

    [Fact]
    public void Duplicate_pane_id_is_rejected_and_original_kept()
    {
        var registry = new AgentSessionRegistry();
        var id = Guid.NewGuid();
        Assert.True(registry.Register(MakeRegistration(id, title: "first")));
        Assert.False(registry.Register(MakeRegistration(id, title: "second")));

        Assert.Equal(1, registry.Count);
        Assert.Equal("first", registry.ListSessions().Single().Title);
    }

    [Fact]
    public void Unregister_removes_the_session_and_is_idempotent()
    {
        var registry = new AgentSessionRegistry();
        var id = Guid.NewGuid();
        registry.Register(MakeRegistration(id));

        Assert.True(registry.Unregister(id));
        Assert.False(registry.Unregister(id));
        Assert.Empty(registry.ListSessions());
    }

    [Fact]
    public void Providers_are_read_live_not_captured_at_registration()
    {
        var registry = new AgentSessionRegistry();
        var id = Guid.NewGuid();
        var title = "before";
        var active = false;
        registry.Register(new AgentSessionRegistration
        {
            PaneId = id,
            Buffer = new TerminalBuffer(80, 24),
            TitleProvider = () => title,
            ProfileNameProvider = () => "Profile",
            KindProvider = () => "local",
            IsActiveProvider = () => active,
        });

        title = "after";
        active = true;

        var session = registry.ListSessions().Single();
        Assert.Equal("after", session.Title);
        Assert.True(session.IsActive);
    }

    [Fact]
    public void Rekey_moves_the_registration_to_the_new_id()
    {
        // Session restore assigns persisted PaneIds after construction; the
        // TerminalPane.PaneId setter re-keys so the entry stays addressable.
        var registry = new AgentSessionRegistry();
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        registry.Register(MakeRegistration(oldId));

        Assert.True(registry.Rekey(oldId, newId));

        Assert.False(registry.TryGet(oldId, out _));
        Assert.True(registry.TryGet(newId, out var registration));
        Assert.Equal(newId, registration.PaneId);
        Assert.Equal(newId, registry.ListSessions().Single().PaneId);
    }

    [Fact]
    public void Rekey_of_unknown_id_is_a_noop()
    {
        var registry = new AgentSessionRegistry();
        Assert.False(registry.Rekey(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void SetTabAssociation_updates_known_panes_only()
    {
        var registry = new AgentSessionRegistry();
        var paneId = Guid.NewGuid();
        var tabId = Guid.NewGuid();
        registry.Register(MakeRegistration(paneId));

        Assert.True(registry.SetTabAssociation(paneId, tabId));
        Assert.False(registry.SetTabAssociation(Guid.NewGuid(), tabId));

        Assert.Equal(tabId, registry.ListSessions().Single().TabId);
    }

    [Fact]
    public void ListSessions_is_ordered_by_pane_id_for_determinism()
    {
        var registry = new AgentSessionRegistry();
        for (var i = 0; i < 8; i++)
        {
            registry.Register(MakeRegistration());
        }

        var ids = registry.ListSessions().Select(s => s.PaneId).ToArray();
        Assert.Equal(ids.OrderBy(g => g).ToArray(), ids);
    }
}
