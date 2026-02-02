using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Core
{
    public class RustPtySession : ITerminalSession
    {
        private IntPtr _ptyState;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _readTask;
        private Task? _processTask;

        // Bounded queue for back-pressure - prevents OOM on high-throughput output
        private readonly BlockingCollection<string> _outputQueue = new BlockingCollection<string>(boundedCapacity: 100);

        // UTF-8 decoder with state - handles partial multi-byte sequences across reads
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();

        public event Action<string>? OnOutputReceived;
        public event Action<int>? OnExit;

        // DllImport definitions
        private static class Native
        {
            const string LibName = "rusty_pty";

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr pty_create(string cmd, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_read(IntPtr state, byte[] buffer, int len);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_write(IntPtr state, byte[] buffer, int len);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void pty_resize(IntPtr state, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_get_pid(IntPtr state);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void pty_close(IntPtr state);
        }

        public RustPtySession(string shellCommand, int cols = 120, int rows = 30)
        {
            // Initial size, default to a larger 120x30 to avoid "tiny terminal" syndrome for apps like mc
            Console.WriteLine($"[RustPtySession] Creating session for '{shellCommand}' at {cols}x{rows}");
            _ptyState = Native.pty_create(shellCommand, (ushort)cols, (ushort)rows);

            if (_ptyState == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create Rust PTY session.");
            }

            // Start reading and processing in background
            _readTask = Task.Run(ReadLoop);
            _processTask = Task.Run(ProcessLoop);
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];
            char[] charBuffer = new char[4096]; // For decoded characters

            while (!_cts.Token.IsCancellationRequested && _ptyState != IntPtr.Zero)
            {
                int read = Native.pty_read(_ptyState, buffer, buffer.Length);
                if (read > 0)
                {
                    // Use the stateful decoder - it will hold incomplete multi-byte sequences
                    // until more bytes arrive, preventing U+FFFD replacement characters
                    int charCount = _utf8Decoder.GetChars(buffer, 0, read, charBuffer, 0);
                    if (charCount > 0)
                    {
                        string text = new string(charBuffer, 0, charCount);

                        // Bounded add with timeout - provides back-pressure to PTY
                        if (!_outputQueue.TryAdd(text, 50, _cts.Token))
                        {
                            Console.WriteLine("[RustPtySession] Output queue full, dropping data");
                        }
                    }
                }
                else if (read == 0) // EOF
                {
                    Console.WriteLine("[RustPtySession] EOF received.");
                    break;
                }
                else // Error
                {
                    // Reset decoder state on error to prevent corruption
                    _utf8Decoder.Reset();
                    Thread.Sleep(50);
                }
            }
            _outputQueue.CompleteAdding();
        }

        private void ProcessLoop()
        {
            try
            {
                foreach (var text in _outputQueue.GetConsumingEnumerable(_cts.Token))
                {
                    OnOutputReceived?.Invoke(text);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            OnExit?.Invoke(0);
        }

        public void SendInput(string input)
        {
            if (_ptyState == IntPtr.Zero) return;
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            Native.pty_write(_ptyState, bytes, bytes.Length);
        }

        public void Resize(int cols, int rows)
        {
            if (_ptyState == IntPtr.Zero || cols <= 0 || rows <= 0) return;
            Console.WriteLine($"[RustPtySession] Resizing to {cols}x{rows}");
            Native.pty_resize(_ptyState, (ushort)cols, (ushort)rows);
        }

        public void Dispose()
        {
            if (_ptyState != IntPtr.Zero)
            {
                _cts.Cancel();
                _outputQueue.CompleteAdding();
                Native.pty_close(_ptyState);
                _ptyState = IntPtr.Zero;
            }
        }
    }
}
