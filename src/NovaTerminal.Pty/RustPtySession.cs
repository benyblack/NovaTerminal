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
        private int _exitNotified;
        private int _isExited;
        private int? _exitCode;

        // Bounded queue for back-pressure - prevents OOM on high-throughput output
        private readonly BlockingCollection<string> _outputQueue = new BlockingCollection<string>(boundedCapacity: 100);

        // UTF-8 decoder with state - handles partial multi-byte sequences across reads
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();

        public event Action<string>? OnOutputReceived;
        public event Action<int>? OnExit;
        public bool IsProcessRunning => Volatile.Read(ref _isExited) == 0 && _ptyState != IntPtr.Zero;
        public int? ExitCode => _exitCode;

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

        public bool HasActiveChildProcesses
        {
            get
            {
                if (_ptyState == IntPtr.Zero) return false;
                int pid = Native.pty_get_pid(_ptyState);
                if (pid <= 0) return false;
                return HasChildProcesses(pid, ShellCommand);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll")]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hHandle);

        private static bool HasChildProcesses(int parentPid, string shellCommand)
        {
            if (OperatingSystem.IsWindows())
            {
                bool isWslShell = !string.IsNullOrEmpty(shellCommand) && shellCommand.Contains("wsl", StringComparison.OrdinalIgnoreCase);

                IntPtr snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return false;

                try
                {
                    PROCESSENTRY32 pe32 = new PROCESSENTRY32();
                    pe32.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

                    if (Process32First(snapshot, ref pe32))
                    {
                        do
                        {
                            if (pe32.th32ParentProcessID == (uint)parentPid)
                            {
                                if (!pe32.szExeFile.Contains("conhost", StringComparison.OrdinalIgnoreCase) &&
                                    !pe32.szExeFile.Contains("OpenConsole", StringComparison.OrdinalIgnoreCase) &&
                                    !pe32.szExeFile.Contains("wslhost", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (isWslShell && pe32.szExeFile.Contains("wsl", StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }
                                    return true;
                                }
                            }
                        } while (Process32Next(snapshot, ref pe32));
                    }
                }
                finally
                {
                    CloseHandle(snapshot);
                }
                return false;
            }
            else
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "pgrep",
                        Arguments = $"-P {parentPid}",
                        RedirectStandardOutput = true,
                        UseShellExecute = false
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit(100);
                        string output = proc.StandardOutput.ReadToEnd();
                        return !string.IsNullOrWhiteSpace(output);
                    }
                }
                catch { }
                return false;
            }
        }

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

        private NovaTerminal.Core.Replay.ReplayWriter? _recorder;
        private TerminalBuffer? _buffer;

        public bool IsRecording => _recorder != null;

        public void StartRecording(string filePath)
        {
            if (_recorder != null) return; // Already recording
            var recorder = new NovaTerminal.Core.Replay.ReplayWriter(filePath, _cols, _rows, ShellCommand);
            try
            {
                recorder.RecordMarker("START");
                if (_buffer != null)
                {
                    recorder.RecordSnapshot(_buffer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RustPtySession] Recording start marker/snapshot failed: {ex.Message}");
            }

            _recorder = recorder;
            Console.WriteLine($"[RustPtySession] Recording started to: {filePath}");
        }

        public void StopRecording()
        {
            var recorder = _recorder;
            if (recorder == null) return;

            _recorder = null;
            try
            {
                recorder.RecordMarker("END");
                if (_buffer != null)
                {
                    recorder.RecordSnapshot(_buffer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RustPtySession] Recording stop marker/snapshot failed: {ex.Message}");
            }

            try
            {
                recorder.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RustPtySession] Recorder dispose failed: {ex.Message}");
            }

            Console.WriteLine("[RustPtySession] Recording stopped.");
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
                        try
                        {
                            // Block when the queue is full so we apply back-pressure instead of dropping output.
                            _outputQueue.Add(text, _cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (InvalidOperationException)
                        {
                            break;
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
            if (!_outputQueue.IsAddingCompleted)
            {
                _outputQueue.CompleteAdding();
            }
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
            TryNotifyExit(0);
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
            if (_recorder != null)
            {
                try
                {
                    StopRecording();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RustPtySession] StopRecording during dispose failed: {ex.Message}");
                }
            }

            if (_ptyState != IntPtr.Zero)
            {
                _cts.Cancel();
                if (!_outputQueue.IsAddingCompleted)
                {
                    _outputQueue.CompleteAdding();
                }
                Native.pty_close(_ptyState);
                _ptyState = IntPtr.Zero;
                TryNotifyExit(0);
            }
        }

        private void TryNotifyExit(int code)
        {
            if (Interlocked.Exchange(ref _exitNotified, 1) != 0) return;

            _exitCode = code;
            Volatile.Write(ref _isExited, 1);
            OnExit?.Invoke(code);
        }
    }
}
