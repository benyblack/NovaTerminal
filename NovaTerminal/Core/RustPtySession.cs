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

        public RustPtySession(string shellCommand)
        {
            // Initial size 80x24, will be resized by view
            _ptyState = Native.pty_create(shellCommand, 80, 24);

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
            while (!_cts.Token.IsCancellationRequested && _ptyState != IntPtr.Zero)
            {
                int read = Native.pty_read(_ptyState, buffer, buffer.Length);
                if (read > 0)
                {
                    string text = Encoding.UTF8.GetString(buffer, 0, read);
                    OnOutputReceived?.Invoke(text);
                }
                else if (read == 0) // EOF
                {
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
