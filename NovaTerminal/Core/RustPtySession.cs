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
            string effectiveShell = shellCommand;
            string shellLower = shellCommand.ToLowerInvariant();

            if (shellLower.EndsWith("cmd.exe"))
            {
                // CMD: /k runs command then remains interactive. > nul suppresses "Active code page" output.
                effectiveShell = $"{shellCommand} /k chcp 65001 > nul";
            }
            else if (shellLower.Contains("powershell") || shellLower.Contains("pwsh"))
            {
                // PowerShell:
                // We launch with -NoLogo to start empty.
                // Then we INJECT the init script command via input.
                // The script contains "Clear-Host", which wipes the injected text immediately.
                // This avoids the "Persistent Echo" problem of command-line arguments.
                effectiveShell = $"{shellCommand} -NoLogo";
            }

            Console.WriteLine($"[RustPtySession] Creating session for '{effectiveShell}' at {cols}x{rows}");
            _ptyState = Native.pty_create(effectiveShell, (ushort)cols, (ushort)rows);

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
                Task.Delay(100).ContinueWith(_ =>
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
                        // Use quotes to handle spaces in Temp path although rare
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

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];
            char[] charBuffer = new char[4096]; // For decoded characters

            while (!_cts.Token.IsCancellationRequested && _ptyState != IntPtr.Zero)
            {
                int read = Native.pty_read(_ptyState, buffer, buffer.Length);
                if (read > 0)
                {
                    // RAW BYTES DEBUG
                    string hex = BitConverter.ToString(buffer, 0, read);
                    try { System.IO.File.AppendAllText("pty_bytes.log", $"Read {read}: {hex}\n"); } catch { }

                    // Use the stateful decoder - it will hold incomplete multi-byte sequences
                    // until more bytes arrive, preventing U+FFFD replacement characters
                    int charCount = _utf8Decoder.GetChars(buffer, 0, read, charBuffer, 0);
                    if (charCount > 0)
                    {
                        string text = new string(charBuffer, 0, charCount);

                        // CHAR DEBUG
                        string debug = "";
                        foreach (var c in text) debug += $"{(int)c:X4} ";
                        try { System.IO.File.AppendAllText("pty_chars.log", $"Chars: {debug}\n"); } catch { }

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
