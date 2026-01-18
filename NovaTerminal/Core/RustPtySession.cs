using System;
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

            // Start reading in background
            _readTask = Task.Run(ReadLoop);
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
                        OnOutputReceived?.Invoke(text);
                    }
                }
                else if (read == 0) // EOF
                {
                    Console.WriteLine("[RustPtySession] EOF received.");
                    break;
                }
                else // Error
                {
                    // Simple backoff on error
                    Thread.Sleep(50);
                }
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
                Native.pty_close(_ptyState);
                _ptyState = IntPtr.Zero;
            }
        }
    }
}
