using NovaTerminal.Core.Ssh.Native;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSshFailureClassifierTests
{
    [Theory]
    [InlineData("connection timed out", NativeSshFailureKind.Timeout)]
    [InlineData("SSH authentication failed", NativeSshFailureKind.Authentication)]
    [InlineData("Host key changed for server", NativeSshFailureKind.HostKeyMismatch)]
    [InlineData("ChannelOpenFailure(ConnectFailed)", NativeSshFailureKind.ChannelOpen)]
    [InlineData("Failed to bind local forward 127.0.0.1:8080", NativeSshFailureKind.ForwardBind)]
    [InlineData("remote disconnect: connection lost", NativeSshFailureKind.RemoteDisconnect)]
    public void Classify_KnownMessages_ReturnsStableBucket(string message, NativeSshFailureKind expected)
    {
        NativeSshFailure result = NativeSshFailureClassifier.Classify(message);

        Assert.Equal(expected, result.Kind);
        Assert.Equal(message, result.Message);
    }

    [Fact]
    public void Classify_UnknownMessage_ReturnsUnknownBucket()
    {
        NativeSshFailure result = NativeSshFailureClassifier.Classify("unhandled native ssh issue");

        Assert.Equal(NativeSshFailureKind.Unknown, result.Kind);
    }
}
