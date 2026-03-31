using System.Collections.Generic;
using System.Text.Json.Serialization;
using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Storage;

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
internal partial class SshJsonContext : JsonSerializerContext
{
}
