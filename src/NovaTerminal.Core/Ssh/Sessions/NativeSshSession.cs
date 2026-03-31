using System.Text;
using NovaTerminal.Core.Replay;
using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;

namespace NovaTerminal.Core.Ssh.Sessions;

public sealed class NativeSshSession : ITerminalSession
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(25);

    private readonly INativeSshInterop _interop;
    private readonly CancellationTokenSource _pollCts = new();
    private readonly Task _pollTask;
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private readonly Action<string> _log;

    private ReplayWriter? _recorder;
    private TerminalBuffer? _buffer;
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
        INativeSshInterop? interop = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.JumpHops.Count != 0 || profile.Forwards.Count != 0)
        {
            throw new NotSupportedException("Native SSH backend does not support jump hops or port forwards yet.");
        }

        _ = diagnosticsLevel;
        _ = extraArgs;
        _cols = cols;
        _rows = rows;
        _log = log ?? Console.WriteLine;
        _interop = interop ?? new NativeSshInterop();
        _sessionHandle = _interop.Connect(NativeSshConnectionOptions.FromProfile(profile, cols, rows));
        _isRunning = 1;
        ShellArguments = $"{profile.User}@{profile.Host}:{profile.Port}";
        _pollTask = Task.Run(PollLoopAsync);
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
                        EmitPromptUnsupported(nextEvent.Kind);
                        return;
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

    private void EmitPromptUnsupported(NativeSshEventKind kind)
    {
        _log($"[NativeSshSession] Prompt event '{kind}' requires Task 4 interaction handling.");
        OnOutputReceived?.Invoke("Native SSH prompt handling is not implemented yet." + Environment.NewLine);
        TryNotifyExit(-1);
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
