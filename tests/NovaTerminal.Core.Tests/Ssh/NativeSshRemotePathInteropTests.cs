using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSshRemotePathInteropTests
{
    [Fact]
    public void SerializeRemotePathListRequest_UsesGeneratedMetadataAndCamelCasePayload()
    {
        NativeSshConnectionOptions connectionOptions = new()
        {
            Host = "example.com",
            User = "nova",
            Port = 2222,
            Password = "secret",
            KnownHostsFilePath = @"C:\known-hosts.json",
            JumpHost = new SshJumpHop
            {
                Host = "jump.internal",
                User = "jumper",
                Port = 2200
            }
        };

        string json = NativeSshInterop.SerializeRemotePathListRequestForTests(connectionOptions, "/mnt/media");

        Assert.Contains("\"connection\":", json, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"/mnt/media\"", json, StringComparison.Ordinal);
        Assert.Contains("\"knownHostsFilePath\":\"C:\\\\known-hosts.json\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeRemotePathListResponse_UsesGeneratedMetadata()
    {
        const string json = """
        {"entries":[{"name":"movies","fullPath":"/mnt/media/movies","isDirectory":true}]}
        """;

        IReadOnlyList<NativeRemotePathEntry> entries = NativeSshInterop.DeserializeRemotePathListResponseForTests(json);

        Assert.Single(entries);
        Assert.Equal("movies", entries[0].Name);
        Assert.Equal("/mnt/media/movies", entries[0].FullPath);
        Assert.True(entries[0].IsDirectory);
        Assert.Null(entries[0].ModifiedAtUtc);
    }

    [Fact]
    public void DeserializeRemotePathListResponse_MapsAndNormalizesModifiedAtUtcMetadata()
    {
        const string json = """
        {"entries":[{"name":"movies","fullPath":"/mnt/media/movies","isDirectory":true,"modifiedAtUtc":"2026-05-04T20:15:00"},{"name":"logs","fullPath":"/mnt/media/logs","isDirectory":true,"modifiedAtUtc":null}]}
        """;

        IReadOnlyList<NativeRemotePathEntry> entries = NativeSshInterop.DeserializeRemotePathListResponseForTests(json);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new DateTime(2026, 5, 4, 20, 15, 0, DateTimeKind.Utc), entries[0].ModifiedAtUtc);
        Assert.Equal(DateTimeKind.Utc, entries[0].ModifiedAtUtc!.Value.Kind);
        Assert.Null(entries[1].ModifiedAtUtc);
    }
}
