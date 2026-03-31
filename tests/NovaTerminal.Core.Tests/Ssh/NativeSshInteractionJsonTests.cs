using System.Text;
using NovaTerminal.Core.Ssh.Interactions;
using NovaTerminal.Core.Ssh.Native;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSshInteractionJsonTests
{
    [Fact]
    public void ParseRequest_ParsesHostKeyPromptPayload()
    {
        SshInteractionRequest request = NativeSshInteractionJson.ParseRequest(
            NativeSshEventKind.HostKeyPrompt,
            Encoding.UTF8.GetBytes("""{"host":"srv","port":22,"algorithm":"ssh-ed25519","fingerprint":"SHA256:test"}"""));

        Assert.Equal(SshInteractionKind.UnknownHostKey, request.Kind);
        Assert.Equal("srv", request.Host);
        Assert.Equal(22, request.Port);
        Assert.Equal("ssh-ed25519", request.Algorithm);
        Assert.Equal("SHA256:test", request.Fingerprint);
    }

    [Fact]
    public void BuildResponsePayload_SerializesKeyboardInteractiveResponsesDeterministically()
    {
        byte[] payload = NativeSshInteractionJson.BuildResponsePayload(
            NativeSshResponseKind.KeyboardInteractive,
            SshInteractionResponse.FromKeyboardResponses("code", "backup"));

        Assert.Equal("""{"responses":["code","backup"]}""", Encoding.UTF8.GetString(payload));
    }
}
