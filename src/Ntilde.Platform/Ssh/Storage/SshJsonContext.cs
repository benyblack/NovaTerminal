using System.Collections.Generic;
using System.Text.Json.Serialization;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Platform.Ssh.Storage;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SshStoreDocument))]
[JsonSerializable(typeof(SshProfileStoreSnapshot))]
[JsonSerializable(typeof(SshBackendKind))]
[JsonSerializable(typeof(SshProfile))]
[JsonSerializable(typeof(List<SshProfile>))]
[JsonSerializable(typeof(SshJumpHop))]
[JsonSerializable(typeof(List<SshJumpHop>))]
[JsonSerializable(typeof(PortForward))]
[JsonSerializable(typeof(List<PortForward>))]
[JsonSerializable(typeof(SshMuxOptions))]
[JsonSerializable(typeof(KnownHostEntry))]
[JsonSerializable(typeof(List<KnownHostEntry>))]
internal partial class SshJsonContext : JsonSerializerContext
{
}
