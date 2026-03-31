using System.Text;
using System.Text.Json;
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
    private readonly CancellationTokenSource _pollCts = new();
    private readonly Task _pollTask;
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private readonly Action<string> _log;

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

        if (profile.JumpHops.Count != 0)
        {
            throw new NotSupportedException("Native SSH backend does not support jump hops yet.");
        }

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
        _sessionHandle = _interop.Connect(NativeSshConnectionOptions.FromProfile(profile, cols, rows));

        try
        {
            if (profile.Forwards.Count != 0)
            {
                _portForwardSession = new NativePortForwardSession(_sessionHandle, profile.Forwards, _interop, _log);
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
                _log($"[NativeSshSession] Poll loop failed: {ex.Message}");
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
        if (nextEvent.Payload.Length > 0)
        {
            OnOutputReceived?.Invoke($"{Encoding.UTF8.GetString(nextEvent.Payload)}{Environment.NewLine}");
        }

        TryNotifyExit(nextEvent.StatusCode == 0 ? -1 : nextEvent.StatusCode);
    }

    private async Task HandleInteractionAsync(NativeSshEvent nextEvent)
    {
        SshInteractionRequest request = CreateInteractionRequest(nextEvent);
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

        byte[] payload = BuildInteractionPayload(responseKind, response);
        _interop.SubmitResponse(_sessionHandle, responseKind, payload);
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

    private static SshInteractionRequest CreateInteractionRequest(NativeSshEvent nextEvent)
    {
        return nextEvent.Kind switch
        {
            NativeSshEventKind.HostKeyPrompt => CreateHostKeyRequest(nextEvent.Payload),
            NativeSshEventKind.PasswordPrompt => CreateTextPromptRequest(SshInteractionKind.Password, nextEvent.Payload),
            NativeSshEventKind.PassphrasePrompt => CreateTextPromptRequest(SshInteractionKind.Passphrase, nextEvent.Payload),
            NativeSshEventKind.KeyboardInteractivePrompt => CreateKeyboardRequest(nextEvent.Payload),
            _ => throw new InvalidOperationException($"Unsupported interaction event '{nextEvent.Kind}'.")
        };
    }

    private static SshInteractionRequest CreateHostKeyRequest(byte[] payload)
    {
        HostKeyPromptPayload? model = JsonSerializer.Deserialize<HostKeyPromptPayload>(payload);
        if (model == null)
        {
            throw new InvalidOperationException("Failed to parse host key prompt payload.");
        }

        return new SshInteractionRequest
        {
            Kind = SshInteractionKind.UnknownHostKey,
            Host = model.Host,
            Port = model.Port,
            Algorithm = model.Algorithm,
            Fingerprint = model.Fingerprint
        };
    }

    private static SshInteractionRequest CreateTextPromptRequest(SshInteractionKind kind, byte[] payload)
    {
        TextPromptPayload? model = JsonSerializer.Deserialize<TextPromptPayload>(payload);
        if (model == null)
        {
            throw new InvalidOperationException("Failed to parse authentication prompt payload.");
        }

        return new SshInteractionRequest
        {
            Kind = kind,
            Prompt = model.Prompt
        };
    }

    private static SshInteractionRequest CreateKeyboardRequest(byte[] payload)
    {
        KeyboardInteractivePromptPayload? model = JsonSerializer.Deserialize<KeyboardInteractivePromptPayload>(payload);
        if (model == null)
        {
            throw new InvalidOperationException("Failed to parse keyboard-interactive payload.");
        }

        return new SshInteractionRequest
        {
            Kind = SshInteractionKind.KeyboardInteractive,
            Name = model.Name,
            Instructions = model.Instructions,
            KeyboardPrompts = model.Prompts.Select(prompt => new SshKeyboardPrompt(prompt.Prompt, prompt.Echo)).ToArray()
        };
    }

    private static byte[] BuildInteractionPayload(NativeSshResponseKind responseKind, SshInteractionResponse response)
    {
        object body = responseKind switch
        {
            NativeSshResponseKind.HostKeyDecision => new { accept = response.IsAccepted && !response.IsCanceled },
            NativeSshResponseKind.Password or NativeSshResponseKind.Passphrase => new { text = response.IsCanceled ? string.Empty : response.Secret ?? string.Empty },
            NativeSshResponseKind.KeyboardInteractive => new { responses = response.IsCanceled ? Array.Empty<string>() : response.KeyboardResponses.ToArray() },
            _ => throw new InvalidOperationException($"Unsupported native SSH response kind '{responseKind}'.")
        };

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
    }

    private sealed class HostKeyPromptPayload
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Algorithm { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
    }

    private sealed class TextPromptPayload
    {
        public string Prompt { get; set; } = string.Empty;
    }

    private sealed class KeyboardInteractivePromptPayload
    {
        public string Name { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public List<KeyboardInteractivePromptItem> Prompts { get; set; } = new();
    }

    private sealed class KeyboardInteractivePromptItem
    {
        public string Prompt { get; set; } = string.Empty;
        public bool Echo { get; set; }
    }
}
