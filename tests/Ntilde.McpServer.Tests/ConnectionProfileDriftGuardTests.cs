using System;
using System.Linq;
using NovaTerminal.McpServer.Tools;
using NovaTerminal.Platform;                 // RemoteShellKind
using NovaTerminal.Platform.Ssh.Models;      // SshProfile, SshJumpHop, PortForward, SshMuxOptions, enums
using NovaTerminal.Platform.Ssh.Storage;     // SshProfileStoreSnapshot

namespace NovaTerminal.McpServer.Tests;

// Guards against drift between the hand-mirrored field/enum knowledge in
// ConnectionProfileTools and the real types in NovaTerminal.Platform. If these fail,
// a profile type changed — update ConnectionProfileTools (fields, enums, schema, rules).
public class ConnectionProfileDriftGuardTests
{
    private static string[] PropNames(Type t) =>
        t.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] Sorted(string[] values) =>
        values.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void ProfileFields_MatchSshProfile() =>
        Assert.Equal(PropNames(typeof(SshProfile)), Sorted(ConnectionProfileTools.ProfileFields));

    [Fact]
    public void JumpHopFields_MatchSshJumpHop() =>
        Assert.Equal(PropNames(typeof(SshJumpHop)), Sorted(ConnectionProfileTools.JumpHopFields));

    [Fact]
    public void PortForwardFields_MatchPortForward() =>
        Assert.Equal(PropNames(typeof(PortForward)), Sorted(ConnectionProfileTools.PortForwardFields));

    [Fact]
    public void MuxFields_MatchSshMuxOptions() =>
        Assert.Equal(PropNames(typeof(SshMuxOptions)), Sorted(ConnectionProfileTools.MuxFields));

    // SshProfileStoreSnapshot is a public proxy for the actually-serialized (internal)
    // SshStoreDocument; both must stay structurally identical ({ SchemaVersion, Profiles }).
    // SshStoreDocument is internal to NovaTerminal.Platform and unreachable here, so we guard
    // DocumentFields against the snapshot. If you add a field to SshStoreDocument, mirror it on
    // SshProfileStoreSnapshot (and in ConnectionProfileTools.DocumentFields) or this guard goes stale.
    [Fact]
    public void DocumentFields_MatchSnapshot() =>
        Assert.Equal(PropNames(typeof(SshProfileStoreSnapshot)), Sorted(ConnectionProfileTools.DocumentFields));

    // Enum name arrays are indexed by integer value; compare to Enum.GetNames (value order).
    [Fact]
    public void BackendKindNames_MatchEnum() =>
        Assert.Equal(Enum.GetNames<SshBackendKind>(), ConnectionProfileTools.BackendKindNames);

    [Fact]
    public void AuthModeNames_MatchEnum() =>
        Assert.Equal(Enum.GetNames<SshAuthMode>(), ConnectionProfileTools.AuthModeNames);

    [Fact]
    public void PortForwardKindNames_MatchEnum() =>
        Assert.Equal(Enum.GetNames<PortForwardKind>(), ConnectionProfileTools.PortForwardKindNames);

    [Fact]
    public void RemoteShellKindNames_MatchEnum() =>
        Assert.Equal(Enum.GetNames<RemoteShellKind>(), ConnectionProfileTools.RemoteShellKindNames);
}
