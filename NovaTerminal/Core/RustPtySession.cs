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
        public Guid Id { get; } = Guid.NewGuid();
        private IntPtr _ptyState;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _readTask;
        private Task? _processTask;
        private string? _savedPassword;
        private bool _passwordSent = false;

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
            public static extern IntPtr pty_spawn(string cmd, string? args, string? cwd, ushort cols, ushort rows);

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

        public string ShellCommand { get; }

        private int _cols;
        private int _rows;

        public RustPtySession(string shellCommand, int cols = 120, int rows = 30, string? args = null, string? cwd = null)
        {
            ShellCommand = shellCommand;
            _cols = cols;
            _rows = rows;

            string effectiveShell = shellCommand;
            string combinedArgs = args ?? "";
            string shellLower = shellCommand.ToLowerInvariant();

            if (OperatingSystem.IsWindows())
            {
                if (shellLower.EndsWith("cmd.exe"))
                {
                    effectiveShell = shellCommand;
                    combinedArgs = "/k chcp 65001 " + combinedArgs;
                }
                else if (shellLower.Contains("powershell") || shellLower.Contains("pwsh"))
                {
                    effectiveShell = shellCommand;
                    combinedArgs = "-NoLogo " + combinedArgs;
                }
            }

            Console.WriteLine($"[RustPtySession] Spawning '{effectiveShell}' args='{combinedArgs}' cwd='{cwd}' at {cols}x{rows}");
            _ptyState = Native.pty_spawn(effectiveShell, combinedArgs.Trim(), cwd, (ushort)cols, (ushort)rows);

            if (_ptyState == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create Rust PTY session.");
            }

            // Start reading and processing in background
            _readTask = Task.Run(ReadLoop);
            _processTask = Task.Run(ProcessLoop);

            // POST-LAUNCH INJECTION for PowerShell
            if (shellLower.Contains("powershell") || shellLower.Contains("pwsh"))
            {
                Task.Delay(300).ContinueWith(_ =>
                {
                    try
                    {
                        var sb = new StringBuilder();
                        // 1. Set Encoding cleanly
                        sb.AppendLine("$OutputEncoding = [System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8;");
                        // 2. Clear output (wipes the injected command text)
                        sb.AppendLine("Clear-Host;");
                        // 3. Print Banner
                        sb.AppendLine("Write-Host 'Windows PowerShell';");
                        sb.AppendLine("Write-Host 'Copyright (C) Microsoft Corporation. All rights reserved.';");
                        sb.AppendLine("Write-Host ''");
                        sb.AppendLine("Write-Host 'Install the latest PowerShell for new features and improvements! https://aka.ms/PSWindows';");
                        sb.AppendLine("Write-Host ''");

                        string tempScript = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nova_init_{Guid.NewGuid()}.ps1");
                        System.IO.File.WriteAllText(tempScript, sb.ToString());

                        // Inject the execution command
                        string cleanPath = tempScript.Replace("'", "''");
                        SendInput($"& '{cleanPath}'\r");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RustPtySession] PS Injection Failed: {ex.Message}");
                    }
                });
            }
        }

        public void SetSavedPassword(string password)
        {
            _savedPassword = password;
            _passwordSent = false;
        }

        private NovaTerminal.Core.Replay.PtyRecorder? _recorder;
        private TerminalBuffer? _buffer;

        public void StartRecording(string filePath)
        {
            _recorder = new NovaTerminal.Core.Replay.PtyRecorder(filePath, _cols, _rows, ShellCommand);
            Console.WriteLine($"[RustPtySession] Recording started to: {filePath}");
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

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];
            char[] charBuffer = new char[4096]; // For decoded characters

            while (!_cts.Token.IsCancellationRequested && _ptyState != IntPtr.Zero)
            {
                int read = Native.pty_read(_ptyState, buffer, buffer.Length);
                if (read > 0)
                {
                    // Record raw bytes before any processing
                    _recorder?.RecordChunk(buffer, read);

                    // Use the stateful decoder - it will hold incomplete multi-byte sequences
                    // until more bytes arrive, preventing U+FFFD replacement characters
                    int charCount = _utf8Decoder.GetChars(buffer, 0, read, charBuffer, 0);
                    if (charCount > 0)
                    {
                        string text = new string(charBuffer, 0, charCount);

                        // PASSWORD INJECTION (Mimics sshpass but integrated)
                        if (!string.IsNullOrEmpty(_savedPassword) && !_passwordSent)
                        {
                            // Match common password prompts: "Password:", "password:", "[user]'s password:"
                            if (text.Contains("password:", StringComparison.OrdinalIgnoreCase))
                            {
                                Task.Delay(200).ContinueWith(_ =>
                                {
                                    SendInput(_savedPassword + "\r");
                                    _passwordSent = true;
                                    Console.WriteLine("[RustPtySession] Automated password injected.");
                                });
                            }
                        }

                        // Bounded add with timeout - provides back-pressure to PTY
                        if (!_outputQueue.TryAdd(text, 50, _cts.Token))
                        {
                            // Quietly drop or handle back-pressure without logging every time
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

            _recorder?.RecordInput(input);

            byte[] data = Encoding.UTF8.GetBytes(input);
            Native.pty_write(_ptyState, data, data.Length);
        }

        public void Resize(int cols, int rows)
        {
            if (_ptyState == IntPtr.Zero || cols <= 0 || rows <= 0) return;
            _cols = cols;
            _rows = rows;
            Console.WriteLine($"[RustPtySession] Resizing to {cols}x{rows}");
            Native.pty_resize(_ptyState, (ushort)cols, (ushort)rows);
            _recorder?.RecordResize(cols, rows);
        }

        public void Dispose()
        {
            if (_ptyState != IntPtr.Zero)
            {
                _cts.Cancel();
                _outputQueue.CompleteAdding();
                _recorder?.Dispose();
                Native.pty_close(_ptyState);
                _ptyState = IntPtr.Zero;
            }
        }
    }
}
