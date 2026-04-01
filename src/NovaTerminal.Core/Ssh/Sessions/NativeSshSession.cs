using System.Text;
using NovaTerminal.Core.Replay;
using NovaTerminal.Core.Ssh.Interactions;
using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;

namespace NovaTerminal.Core.Ssh.Sessions;

public sealed class NativeSshSession : ITerminalSession
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(25);

    private readonly INativeSshInterop _interop;
    private readonly ISshInteractionHandler? _interactionHandler;
    private readonly NativeJumpHostConnector _jumpHostConnector = new();
    private readonly CancellationTokenSource _pollCts = new();
    private readonly Task _pollTask;
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private readonly Action<string> _log;
    private readonly NativeSshMetrics _metrics = new();
    private readonly Guid _profileId;
    private readonly string _profileName;
    private readonly string _profileUser;
    private readonly string _profileHost;
    private readonly bool _rememberPasswordInVault;
    private bool _allowVaultPasswordReuse;

    private ReplayWriter? _recorder;
    private TerminalBuffer? _buffer;
    private NativePortForwardSession? _portForwardSession;
    private IntPtr _sessionHandle;
    private int _cols;
    private int _rows;
    private int _isRunning;
    private int _exitNotified;
    private int? _exitCode;

    public NativeSshSession(
        SshProfile profile,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        IReadOnlyList<string>? extraArgs = null,
        Action<string>? log = null,
        INativeSshInterop? interop = null,
        ISshInteractionHandler? interactionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Forwards.Any(forward => forward.Kind != PortForwardKind.Local))
        {
            throw new NotSupportedException("Native SSH backend currently supports local port forwards only.");
        }

        _ = diagnosticsLevel;
        _ = extraArgs;
        _cols = cols;
        _rows = rows;
        _log = log ?? Console.WriteLine;
        _interop = interop ?? new NativeSshInterop();
        _interactionHandler = interactionHandler;
        _profileId = profile.Id;
        _profileName = profile.Name;
        _profileUser = profile.User;
        _profileHost = profile.Host;
        _rememberPasswordInVault = profile.RememberPasswordInVault;
        _allowVaultPasswordReuse = profile.RememberPasswordInVault;
        JumpHostConnectPlan connectPlan = JumpHostConnectPlan.Create(profile);
        NativeSshConnectionOptions connectionOptions = _jumpHostConnector.CreateConnectionOptions(connectPlan, profile, cols, rows);
        _log($"[NativeSshSession] backend=native path={_jumpHostConnector.DescribePath(connectPlan)} target={connectionOptions.User}@{connectionOptions.Host}:{connectionOptions.Port}");
        _sessionHandle = _interop.Connect(connectionOptions);

        try
        {
            if (profile.Forwards.Count != 0)
            {
                _portForwardSession = new NativePortForwardSession(_sessionHandle, profile.Forwards, _interop, _log);
                foreach (PortForward forward in profile.Forwards)
                {
                    _metrics.RecordForwardSetup(forward.ToString());
                }
            }

            _isRunning = 1;
            ShellArguments = $"{profile.User}@{profile.Host}:{profile.Port}";
            _pollTask = Task.Run(PollLoopAsync);
        }
        catch
        {
            CloseNativeHandle();
            _pollCts.Dispose();
            throw;
        }
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand => "native-ssh";
    public string? ShellArguments { get; }
    public bool IsProcessRunning => Volatile.Read(ref _isRunning) == 1;
    public bool HasActiveChildProcesses => false;
    public int? ExitCode => _exitCode;
    public bool IsRecording => _recorder != null;

    public event Action<string>? OnOutputReceived;
    public event Action<int>? OnExit;

    public void SendInput(string input)
    {
        if (_sessionHandle == IntPtr.Zero || string.IsNullOrEmpty(input))
        {
            return;
        }

        _recorder?.RecordInput(input);
        _interop.Write(_sessionHandle, Encoding.UTF8.GetBytes(input));
    }

    public void Resize(int cols, int rows)
    {
        if (_sessionHandle == IntPtr.Zero || cols <= 0 || rows <= 0)
        {
            return;
        }

        _cols = cols;
        _rows = rows;
        _interop.Resize(_sessionHandle, cols, rows);
        _recorder?.RecordResize(cols, rows);
    }

    public void StartRecording(string filePath)
    {
        if (_recorder != null)
        {
            return;
        }

        var recorder = new ReplayWriter(filePath, _cols, _rows, ShellCommand);
        recorder.RecordMarker("START");
        if (_buffer != null)
        {
            recorder.RecordSnapshot(_buffer);
        }

        _recorder = recorder;
    }

    public void StopRecording()
    {
        var recorder = _recorder;
        if (recorder == null)
        {
            return;
        }

        _recorder = null;
        recorder.RecordMarker("END");
        if (_buffer != null)
        {
            recorder.RecordSnapshot(_buffer);
        }

        recorder.Dispose();
    }

    public void AttachBuffer(TerminalBuffer buffer)
    {
        _buffer = buffer;
    }

    public void TakeSnapshot()
    {
        if (_buffer != null)
        {
            _recorder?.RecordSnapshot(_buffer);
        }
    }

    public void Dispose()
    {
        StopRecording();
        _pollCts.Cancel();
        _metrics.MarkDisconnected("Disposed");
        _portForwardSession?.Dispose();
        CloseNativeHandle();
        try
        {
            _pollTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is TaskCanceledException or OperationCanceledException))
        {
        }
        finally
        {
            _pollCts.Dispose();
        }
    }

    private async Task PollLoopAsync()
    {
        try
        {
            while (!_pollCts.IsCancellationRequested)
            {
                NativeSshEvent? nextEvent = _interop.PollEvent(_sessionHandle);
                if (nextEvent == null)
                {
                    await Task.Delay(PollDelay, _pollCts.Token).ConfigureAwait(false);
                    continue;
                }

                switch (nextEvent.Kind)
                {
                    case NativeSshEventKind.Connected:
                        _metrics.MarkConnected();
                        break;
                    case NativeSshEventKind.Data:
                        EmitOutput(nextEvent.Payload);
                        break;
                    case NativeSshEventKind.ForwardChannelData:
                    case NativeSshEventKind.ForwardChannelEof:
                    case NativeSshEventKind.ForwardChannelClosed:
                        _portForwardSession?.HandleEvent(nextEvent);
                        break;
                    case NativeSshEventKind.ExitStatus:
                        TryNotifyExit(nextEvent.StatusCode);
                        break;
                    case NativeSshEventKind.Error:
                        EmitErrorAndExit(nextEvent);
                        return;
                    case NativeSshEventKind.HostKeyPrompt:
                    case NativeSshEventKind.PasswordPrompt:
                    case NativeSshEventKind.PassphrasePrompt:
                    case NativeSshEventKind.KeyboardInteractivePrompt:
                        await HandleInteractionAsync(nextEvent).ConfigureAwait(false);
                        break;
                    case NativeSshEventKind.Closed:
                        _metrics.MarkDisconnected("Closed");
                        TryNotifyExit(_exitCode ?? 0);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_pollCts.IsCancellationRequested)
            {
                NativeSshFailure failure = NativeSshFailureClassifier.Classify(ex.Message);
                _metrics.MarkDisconnected(failure.Kind.ToString());
                _log($"[NativeSshSession] Poll loop failed: {ex.Message}");
                _log($"[NativeSshSession] failure={failure.Kind}");
                OnOutputReceived?.Invoke($"Native SSH session failed: {ex.Message}{Environment.NewLine}");
                TryNotifyExit(-1);
            }
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            CloseNativeHandle();
        }
    }

    private void EmitOutput(byte[] payload)
    {
        _metrics.MarkFirstOutput();
        _recorder?.RecordChunk(payload, payload.Length);

        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(payload.Length)];
        int charCount = _utf8Decoder.GetChars(payload, 0, payload.Length, chars, 0, flush: false);
        if (charCount > 0)
        {
            OnOutputReceived?.Invoke(new string(chars, 0, charCount));
        }
    }

    private void EmitErrorAndExit(NativeSshEvent nextEvent)
    {
        string message = nextEvent.Payload.Length > 0
            ? Encoding.UTF8.GetString(nextEvent.Payload)
            : "Native SSH error";
        NativeSshFailure failure = NativeSshFailureClassifier.Classify(message);
        _metrics.MarkDisconnected(failure.Kind.ToString());
        _log($"[NativeSshSession] failure={failure.Kind}");

        if (nextEvent.Payload.Length > 0)
        {
            OnOutputReceived?.Invoke($"{message}{Environment.NewLine}");
        }

        TryNotifyExit(nextEvent.StatusCode == 0 ? -1 : nextEvent.StatusCode);
    }

    private async Task HandleInteractionAsync(NativeSshEvent nextEvent)
    {
        if (nextEvent.Kind == NativeSshEventKind.HostKeyPrompt)
        {
            _metrics.MarkHostKeyPromptStarted();
        }
        else
        {
            _metrics.MarkAuthenticationPromptStarted();
        }

        SshInteractionRequest request = WithProfileContext(NativeSshInteractionJson.ParseRequest(nextEvent.Kind, nextEvent.Payload));
        SshInteractionResponse response = _interactionHandler == null
            ? SshInteractionResponse.Cancel()
            : await _interactionHandler.HandleAsync(request, _pollCts.Token).ConfigureAwait(false);

        NativeSshResponseKind responseKind = nextEvent.Kind switch
        {
            NativeSshEventKind.HostKeyPrompt => NativeSshResponseKind.HostKeyDecision,
            NativeSshEventKind.PasswordPrompt => NativeSshResponseKind.Password,
            NativeSshEventKind.PassphrasePrompt => NativeSshResponseKind.Passphrase,
            NativeSshEventKind.KeyboardInteractivePrompt => NativeSshResponseKind.KeyboardInteractive,
            _ => throw new InvalidOperationException($"Unsupported interaction event '{nextEvent.Kind}'.")
        };

        byte[] payload = NativeSshInteractionJson.BuildResponsePayload(responseKind, response);
        _interop.SubmitResponse(_sessionHandle, responseKind, payload);

        if (nextEvent.Kind == NativeSshEventKind.HostKeyPrompt)
        {
            _metrics.MarkHostKeyPromptCompleted();
        }
        else
        {
            _metrics.MarkAuthenticationPromptCompleted();
        }
    }

    private SshInteractionRequest WithProfileContext(SshInteractionRequest request)
    {
        if (_profileId == Guid.Empty)
        {
            return request;
        }

        SshInteractionRequest requestWithContext = new()
        {
            Kind = request.Kind,
            ProfileId = _profileId,
            ProfileName = _profileName,
            ProfileUser = _profileUser,
            ProfileHost = _profileHost,
            AllowVaultPasswordReuse = request.Kind == SshInteractionKind.Password && _allowVaultPasswordReuse,
            RememberPasswordInVault = _rememberPasswordInVault,
            Host = request.Host,
            Port = request.Port,
            Algorithm = request.Algorithm,
            Fingerprint = request.Fingerprint,
            Prompt = request.Prompt,
            Name = request.Name,
            Instructions = request.Instructions,
            KeyboardPrompts = request.KeyboardPrompts
        };

        if (request.Kind == SshInteractionKind.Password)
        {
            _allowVaultPasswordReuse = false;
        }

        return requestWithContext;
    }

    private void TryNotifyExit(int exitCode)
    {
        _exitCode ??= exitCode;
        if (Interlocked.Exchange(ref _exitNotified, 1) == 0)
        {
            OnExit?.Invoke(_exitCode.Value);
        }
    }

    private void CloseNativeHandle()
    {
        IntPtr handle = Interlocked.Exchange(ref _sessionHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _interop.Close(handle);
        }
    }

}
