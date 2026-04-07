using System.Collections.Concurrent;
using System.Text;
using NovaTerminal.Core.Ssh.Interactions;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Sessions;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSshSessionInteractionTests
{
    [Fact]
    public async Task PromptWaitsForInteractionResponseBeforePollingFurtherEvents()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(new NativeSshEvent(
            NativeSshEventKind.HostKeyPrompt,
            Encoding.UTF8.GetBytes("""{"host":"srv","port":22,"algorithm":"ssh-ed25519","fingerprint":"SHA256:test"}"""),
            flags: NativeSshEventFlags.Json));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("ready\n")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed());

        var handler = new PendingInteractionHandler();
        var outputs = new List<string>();
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop, interactionHandler: handler);
        session.OnOutputReceived += outputs.Add;
        session.OnExit += code => exit.TrySetResult(code);

        await handler.WaitForRequestAsync();
        await Task.Delay(100);

        Assert.Equal(1, interop.PollCallCount);
        Assert.Empty(outputs);
        Assert.Empty(interop.Submissions);

        handler.Complete(SshInteractionResponse.AcceptHostKey());

        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("""{"accept":true}""", interop.Submissions.Single().PayloadJson);
        Assert.Contains("ready", outputs.Single());
    }

    [Fact]
    public async Task PromptCancellationSubmitsDeterministicRejectPayload()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(new NativeSshEvent(
            NativeSshEventKind.HostKeyPrompt,
            Encoding.UTF8.GetBytes("""{"host":"srv","port":22,"algorithm":"ssh-ed25519","fingerprint":"SHA256:test"}"""),
            flags: NativeSshEventFlags.Json));

        var handler = new PendingInteractionHandler();
        using var session = new NativeSshSession(CreateProfile(), interop: interop, interactionHandler: handler);

        await handler.WaitForRequestAsync();
        handler.Complete(SshInteractionResponse.Cancel());
        await WaitUntilAsync(() => interop.Submissions.Count > 0);

        var submission = interop.Submissions.Single();
        Assert.Equal(NativeSshResponseKind.HostKeyDecision, submission.Kind);
        Assert.Equal("""{"accept":false}""", submission.PayloadJson);
    }

    [Fact]
    public async Task PasswordPromptCarriesNativeProfileContext()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(new NativeSshEvent(
            NativeSshEventKind.PasswordPrompt,
            Encoding.UTF8.GetBytes("""{"prompt":"Password:"}"""),
            flags: NativeSshEventFlags.Json));
        interop.Enqueue(new NativeSshEvent(
            NativeSshEventKind.PasswordPrompt,
            Encoding.UTF8.GetBytes("""{"prompt":"Password:"}"""),
            flags: NativeSshEventFlags.Json));

        var requests = new List<SshInteractionRequest>();
        var handler = new RecordingInteractionHandler(requests);
        var profile = CreateProfile();

        using var session = new NativeSshSession(profile, interop: interop, interactionHandler: handler);

        await WaitUntilAsync(() => requests.Count >= 2);

        Assert.Equal(profile.Id, requests[0].ProfileId);
        Assert.Equal(profile.Name, requests[0].ProfileName);
        Assert.Equal(profile.User, requests[0].ProfileUser);
        Assert.Equal(profile.Host, requests[0].ProfileHost);
        Assert.True(requests[0].RememberPasswordInVault);
        Assert.True(requests[0].AllowVaultPasswordReuse);

        Assert.True(requests[1].RememberPasswordInVault);
        Assert.False(requests[1].AllowVaultPasswordReuse);
    }

    private static SshProfile CreateProfile()
    {
        return new SshProfile
        {
            Id = Guid.Parse("6892b728-776c-4cb0-8e31-5875f69e8086"),
            BackendKind = SshBackendKind.Native,
            Host = "native.example",
            User = "nova",
            Port = 22
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    private sealed class PendingInteractionHandler : ISshInteractionHandler
    {
        private readonly TaskCompletionSource<SshInteractionRequest> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SshInteractionResponse> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            _request.TrySetResult(request);
            return _response.Task.WaitAsync(cancellationToken);
        }

        public Task<SshInteractionRequest> WaitForRequestAsync() => _request.Task;

        public void Complete(SshInteractionResponse response)
        {
            _response.TrySetResult(response);
        }
    }

    private sealed class RecordingInteractionHandler : ISshInteractionHandler
    {
        private readonly List<SshInteractionRequest> _requests;

        public RecordingInteractionHandler(List<SshInteractionRequest> requests)
        {
            _requests = requests;
        }

        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return Task.FromResult(SshInteractionResponse.Cancel());
        }
    }

    private sealed class FakeNativeSshInterop : INativeSshInterop
    {
        private readonly ConcurrentQueue<NativeSshEvent> _events = new();

        public int PollCallCount { get; private set; }
        public List<(NativeSshResponseKind Kind, string PayloadJson)> Submissions { get; } = new();

        public IntPtr Connect(NativeSshConnectionOptions options) => new(1);

        public NativeSshEvent? PollEvent(IntPtr sessionHandle)
        {
            PollCallCount++;
            return _events.TryDequeue(out NativeSshEvent? nextEvent)
                ? nextEvent
                : null;
        }

        public void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data)
        {
        }

        public void Resize(IntPtr sessionHandle, int cols, int rows)
        {
        }

        public int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options) => 1;

        public void WriteChannel(IntPtr sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
        }

        public void SendChannelEof(IntPtr sessionHandle, int channelId)
        {
        }

        public void CloseChannel(IntPtr sessionHandle, int channelId)
        {
        }

        public void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
            Submissions.Add((responseKind, Encoding.UTF8.GetString(data)));
        }

        public void Close(IntPtr sessionHandle)
        {
        }

        public void Enqueue(NativeSshEvent nextEvent)
        {
            _events.Enqueue(nextEvent);
        }
    }
}
