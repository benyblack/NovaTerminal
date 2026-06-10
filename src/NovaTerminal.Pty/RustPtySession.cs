using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Pty
{
    public class RustPtySession : ITerminalSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        private readonly PtySafeHandle _handle;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        // The read/process loops run on these dedicated threads. Exposed to tests to
        // assert they are background, non-threadpool threads — a leaked session must
        // not consume the threadpool (#81).
        private Thread? _readLoopThread;
        private Thread? _processLoopThread;
        internal Thread? ReadLoopThread => _readLoopThread;
        internal Thread? ProcessLoopThread => _processLoopThread;
        private int _exitNotified;
        private int _isExited;
        private int? _exitCode;

        // Quick first join: if the shell already exited (EOF), the read loop is
        // already unwinding and we never need the (potentially ~1s-spinning) cancel.
        private static readonly TimeSpan QuickJoinTimeout = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(2);
        private int _disposed;

        // Bounded queue for back-pressure - prevents OOM on high-throughput output
        private readonly BlockingCollection<string> _outputQueue = new BlockingCollection<string>(boundedCapacity: 100);

        // UTF-8 decoder with state - handles partial multi-byte sequences across reads
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();

        public event Action<string>? OnOutputReceived;
        public event Action<int>? OnExit;
        public bool IsProcessRunning => Volatile.Read(ref _isExited) == 0 && !_handle.IsClosed && !_handle.IsInvalid;
        public int? ExitCode => _exitCode;

        // DllImport definitions
        private static class Native
        {
            const string LibName = "rusty_pty";

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern PtySafeHandle pty_create(string cmd, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern PtySafeHandle pty_spawn(string cmd, string? args, string? cwd, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern PtySafeHandle pty_spawn_with_envs(string cmd, string? args, string? cwd, ushort cols, ushort rows, string envs);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_read(PtySafeHandle state, byte[] buffer, int len);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_write(PtySafeHandle state, byte[] buffer, int len);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void pty_resize(PtySafeHandle state, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_get_pid(PtySafeHandle state);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void pty_cancel_read(PtySafeHandle state);

            // Raw overload used only by PtySafeHandle.ReleaseHandle().
            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void pty_close(IntPtr state);
        }

        // Owns the *mut PtyState returned by pty_spawn. Passing this to every
        // pty_* P/Invoke makes the marshaller AddRef before / Release after the
        // call, so pty_close (ReleaseHandle) can never run while a pty_read (or
        // any other call) is in flight — closing the #118 use-after-free window.
        internal sealed class PtySafeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public PtySafeHandle() : base(ownsHandle: true) { }

            protected override bool ReleaseHandle()
            {
                Native.pty_close(handle);
                return true;
            }
        }

        public string ShellCommand { get; }
        public string? ShellArguments { get; }

        public bool HasActiveChildProcesses
        {
            get
            {
                if (_handle.IsClosed || _handle.IsInvalid) return false;
                int pid = Native.pty_get_pid(_handle);
                if (pid <= 0) return false;
                return HasChildProcesses(pid, ShellCommand);
            }
        }

        public int? Pid
        {
            get
            {
                if (_handle.IsClosed || _handle.IsInvalid) return null;
                int pid = Native.pty_get_pid(_handle);
                return pid > 0 ? pid : null;
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

        public RustPtySession(
            string shellCommand,
            int cols = 120,
            int rows = 30,
            string? args = null,
            string? cwd = null,
            bool skipPowerShellPostLaunchInit = false,
            IReadOnlyDictionary<string, string>? environmentOverrides = null)
        {
            ShellCommand = shellCommand;
            ShellArguments = args;
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
                    if (!combinedArgs.Contains("-NoLogo", StringComparison.OrdinalIgnoreCase))
                    {
                        combinedArgs = "-NoLogo " + combinedArgs;
                    }
                }
            }

            Console.WriteLine($"[RustPtySession] Spawning '{effectiveShell}' args='{combinedArgs}' cwd='{cwd}' at {cols}x{rows}");
            if (environmentOverrides != null && environmentOverrides.Count > 0)
            {
                // Pack overrides as newline-separated KEY=VALUE pairs. The
                // Rust side splits on '\n' and the first '=' per line.
                var sb = new StringBuilder();
                foreach (var kv in environmentOverrides)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(kv.Key).Append('=').Append(kv.Value);
                }
                _handle = Native.pty_spawn_with_envs(effectiveShell, combinedArgs.Trim(), cwd, (ushort)cols, (ushort)rows, sb.ToString());
            }
            else
            {
                _handle = Native.pty_spawn(effectiveShell, combinedArgs.Trim(), cwd, (ushort)cols, (ushort)rows);
            }

            if (_handle.IsInvalid)
            {
                throw new InvalidOperationException("Failed to create Rust PTY session.");
            }

            // Start reading and processing on DEDICATED background threads, not the
            // threadpool. These loops make blocking native calls (pty_read) and an
            // outright-blocking consuming enumerator; on the threadpool a leaked or
            // slow-to-close session would tie up pool threads and, on low-core CI,
            // starve the test-run completion -> testhost teardown hang (#81). Dedicated
            // IsBackground threads never consume the pool and never block process exit.
            _readLoopThread = new Thread(ReadLoop) { IsBackground = true, Name = $"PtyRead-{Id:N}" };
            _processLoopThread = new Thread(ProcessLoop) { IsBackground = true, Name = $"PtyProcess-{Id:N}" };
            _readLoopThread.Start();
            _processLoopThread.Start();

            // POST-LAUNCH INJECTION for PowerShell
            if (!skipPowerShellPostLaunchInit &&
                (shellLower.Contains("powershell") || shellLower.Contains("pwsh")))
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

        private NovaTerminal.Replay.ReplayWriter? _recorder;

        public bool IsRecording => _recorder != null;

        public void StartRecording(string filePath)
        {
            if (_recorder != null) return; // Already recording
            var recorder = new NovaTerminal.Replay.ReplayWriter(filePath, _cols, _rows, ShellCommand);
            try
            {
                recorder.RecordMarker("START");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RustPtySession] Recording start marker failed: {ex.Message}");
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RustPtySession] Recording stop marker failed: {ex.Message}");
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

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];
            char[] charBuffer = new char[4096]; // For decoded characters

            // This runs on a dedicated thread, so an unhandled exception would crash the
            // whole process (unlike the old Task.Run, whose unobserved exceptions were
            // swallowed). Contain it so a decode/recorder failure can't take down the host.
            try
            {
                while (!_cts.Token.IsCancellationRequested && !_handle.IsInvalid)
                {
                    int read = Native.pty_read(_handle, buffer, buffer.Length);
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
            }
            catch (ObjectDisposedException)
            {
                // _handle was disposed by Dispose() — normal shutdown.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RustPtySession] ReadLoop terminated by unhandled exception: {ex}");
            }
            finally
            {
                // Always signal the consumer so ProcessLoop's GetConsumingEnumerable
                // unblocks and that thread can exit, even if the loop above threw.
                if (!_outputQueue.IsAddingCompleted)
                {
                    _outputQueue.CompleteAdding();
                }
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
            catch (Exception ex)
            {
                // Dedicated thread: OnOutputReceived runs arbitrary subscriber code, and
                // an unhandled exception here would crash the process. Contain + log so a
                // misbehaving subscriber can't take down the host; still notify exit below.
                Console.WriteLine($"[RustPtySession] ProcessLoop terminated by unhandled exception: {ex}");
            }
            TryNotifyExit(0);
        }

        public void SendInput(string input)
        {
            if (_handle.IsClosed || _handle.IsInvalid) return;

            _recorder?.RecordInput(input);

            byte[] data = Encoding.UTF8.GetBytes(input);
            Native.pty_write(_handle, data, data.Length);
        }

        public void Resize(int cols, int rows)
        {
            if (_handle.IsClosed || _handle.IsInvalid || cols <= 0 || rows <= 0) return;
            _cols = cols;
            _rows = rows;
            Console.WriteLine($"[RustPtySession] Resizing to {cols}x{rows}");
            Native.pty_resize(_handle, (ushort)cols, (ushort)rows);
            _recorder?.RecordResize(cols, rows);
        }

        public void Dispose()
        {
            // Idempotent: only the first caller runs teardown.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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

            // 1. Stop the loops re-entering native calls, and let the process loop drain.
            _cts.Cancel();
            if (!_outputQueue.IsAddingCompleted)
            {
                _outputQueue.CompleteAdding();
            }

            // 2. Quick join. If the shell already exited (EOF), the read loop is
            //    already unwinding — no cancel needed, and we avoid pty_cancel_read's
            //    bounded retry spinning when no read is actually blocked.
            bool readExited = _readLoopThread?.Join(QuickJoinTimeout) ?? true;

            // 3. Only if the read is genuinely still blocked: unblock it, then join hard.
            if (!readExited)
            {
                if (!_handle.IsInvalid)
                {
                    try { Native.pty_cancel_read(_handle); }
                    catch (ObjectDisposedException) { /* already gone */ }
                }
                if (!(_readLoopThread?.Join(DisposeJoinTimeout) ?? true))
                {
                    Console.WriteLine("[RustPtySession] ReadLoop did not exit within join timeout.");
                }
            }

            // 4. Join the process loop (it exits once the queue is completed/cancelled).
            if (!(_processLoopThread?.Join(DisposeJoinTimeout) ?? true))
            {
                Console.WriteLine("[RustPtySession] ProcessLoop did not exit within join timeout.");
            }

            // 5. Release the handle. SafeHandle guarantees pty_close runs only once
            //    no pty_* call is in flight, so this is UAF-safe even if a join timed out.
            _handle.Dispose();

            TryNotifyExit(0);
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
