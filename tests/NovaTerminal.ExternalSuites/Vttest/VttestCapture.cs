using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Core;

namespace NovaTerminal.ExternalSuites.Vttest
{
    public sealed class VttestCapture : ITerminalSession, IAsyncDisposable
    {
        private readonly RecWriter _recWriter;
        private IntPtr _ptyState;
        private readonly CancellationTokenSource _cts = new();
        private Task? _readTask;

        public Guid Id { get; } = Guid.NewGuid();
        public string ShellCommand { get; }
        public bool IsRecording => false;

        public event Action<string>? OnOutputReceived;
        public event Action<int>? OnExit;

        private static class Native
        {
            const string LibName = "rusty_pty";

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr pty_spawn(string cmd, string? args, string? cwd, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_read(IntPtr state, byte[] buffer, int len);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_write(IntPtr state, byte[] buffer, int len);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void pty_close(IntPtr state);
        }

        public VttestCapture(string cmd, string args, ushort cols, ushort rows, RecWriter recWriter)
        {
            _recWriter = recWriter;
            ShellCommand = cmd;
            _ptyState = Native.pty_spawn(cmd, args, null, cols, rows);

            if (_ptyState == IntPtr.Zero)
                throw new InvalidOperationException("Failed to spawn PTY for vttest.");

            _readTask = Task.Run(ReadLoop);
        }

        private async Task ReadLoop()
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            int exitCode = 0;

            try
            {
                while (!_cts.Token.IsCancellationRequested && _ptyState != IntPtr.Zero)
                {
                    int read = Native.pty_read(_ptyState, buffer, buffer.Length);

                    if (read > 0)
                    {
                        var data = buffer.AsSpan(0, read);

                        _recWriter.WriteData(data);

                        // Signal activity only (content irrelevant)
                        OnOutputReceived?.Invoke(string.Empty);
                    }
                    else if (read == 0)
                    {
                        break; // EOF
                    }
                    else
                    {
                        // Likely would-block; avoid busy spin.
                        await Task.Delay(10, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                exitCode = 1;
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                OnExit?.Invoke(exitCode);
            }
        }

        // UI interface method (string-based)
        public void SendInput(string input)
        {
            if (_ptyState == IntPtr.Zero) return;
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            Native.pty_write(_ptyState, bytes, bytes.Length);
        }

        // External harness method (byte-exact)
        public void SendInputBytes(ReadOnlySpan<byte> bytes)
        {
            if (_ptyState == IntPtr.Zero || bytes.IsEmpty) return;
            byte[] tmp = bytes.ToArray();
            Native.pty_write(_ptyState, tmp, tmp.Length);
        }

        public void Resize(int cols, int rows) { }
        public void StartRecording(string filePath) { }
        public void StopRecording() { }
        public void AttachBuffer(TerminalBuffer buffer) { }
        public void TakeSnapshot() { }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();

            // Close PTY FIRST to unblock any blocking reads
            if (_ptyState != IntPtr.Zero)
            {
                Native.pty_close(_ptyState);
                _ptyState = IntPtr.Zero;
            }

            if (_readTask != null)
                await _readTask.ConfigureAwait(false);

            _cts.Dispose();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
