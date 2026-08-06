using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

        // A negative pty_read is retried a bounded number of times, then treated as
        // terminal. Previously it retried forever at 20 Hz, so a permanently failing
        // handle left the tab frozen with no error and the loop spinning for the life of
        // the process (#107). 20 attempts x 50 ms tolerates roughly a second of
        // transient failure before giving up.
        //
        // The retry exists because the *return code* still cannot classify the failure:
        // pty_read collapses every error to -1 (null args, read error, poisoned lock, and a
        // panic caught by ffi_guard all return the same value), so a transient condition is
        // indistinguishable from an unrecoverable one from the code alone, and "retry a
        // bounded number of times, then fail" remains the safe reading.
        //
        // What #120 item 3 added is the *reason*: pty_last_error now carries a message, which
        // this loop logs when it gives up, so a frozen tab is no longer unexplained. Turning
        // that message into a retry/no-retry decision would mean matching on error text, which
        // is worse than the bound.
        internal const int MaxConsecutiveReadErrors = 20;
        private static readonly TimeSpan ReadErrorRetryDelay = TimeSpan.FromMilliseconds(50);

        /// Exit code reported when the read loop gives up after repeated failures, to
        /// distinguish it from a clean shell exit (0). Not an OS exit status - the
        /// process may still be alive; the *session* is what has failed.
        internal const int ReadFailureExitCode = -1;

        // PowerShell post-launch init injection. Held so Dispose can observe the task's
        // failures instead of dropping them, and delete the script if the shell never
        // sourced it (the script deletes itself on the happy path). Both are written from
        // the injection task and read by Dispose, hence the volatile access.
        private Task? _powerShellInitTask;
        private volatile string? _powerShellInitScriptPath;

        // Test hook: lets the #107 cleanup guard assert on the exact file this session
        // created, rather than inferring it by diffing %TEMP% (which cannot tell "never
        // created" apart from "created and cleaned up").
        internal string? PowerShellInitScriptPath => _powerShellInitScriptPath;

        // Bounded queue for back-pressure - prevents OOM on high-throughput output
        private readonly BlockingCollection<string> _outputQueue = new BlockingCollection<string>(boundedCapacity: 100);

        // Bounded input queue drained by a dedicated writer thread. The native side does
        // write_all, which can block indefinitely while the foreground program isn't
        // draining stdin (paused pager, `sleep`, full-screen app) — and paste/drop
        // handlers call SendInput from the Avalonia UI thread, so the blocking write must
        // never run on the caller. The bound applies backpressure to pathological floods
        // without unbounded memory growth.
        private readonly BlockingCollection<byte[]> _inputQueue = new BlockingCollection<byte[]>(boundedCapacity: 1024);
        private Thread? _writeLoopThread;

        // UTF-8 decoder with state - handles partial multi-byte sequences across reads
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();

        // Output is buffered until the first subscriber attaches, then replayed.
        // The read/process threads start in the constructor, so a shell's initial
        // prompt can arrive before the UI wires OnOutputReceived; without this,
        // ProcessLoop would dequeue-and-drop that output (blinking cursor, no
        // prompt). Mirrors NativeSshSession's first-subscriber replay.
        // _outputHandlerGate guards the subscriber field and pending buffer.
        // _outputInvocationLock separately serializes the actual handler calls so
        // the first-subscriber replay (subscriber thread) and ProcessLoop
        // (background thread) never invoke the handler — and thus AnsiParser /
        // TerminalBuffer, which are not thread-safe — concurrently, and so
        // replayed output always precedes new output.
        private readonly object _outputHandlerGate = new();
        private readonly object _outputInvocationLock = new();
        private Action<string>? _onOutputReceived;
        private List<string>? _pendingOutputReplay;
        private bool _hasOutputSubscriberEver;

        public event Action<string>? OnOutputReceived
        {
            add
            {
                if (value == null) return;

                // The invocation lock is the OUTER lock so that wiring the
                // subscriber AND replaying the buffer is one atomic step against
                // EmitOutput. This guarantees (a) the non-thread-safe subscriber
                // (AnsiParser/TerminalBuffer) is never entered from two threads at
                // once, and (b) buffered startup output is delivered before any
                // live output — a ProcessLoop emit cannot slip in ahead of the
                // replay. Lock order is always invocation -> gate (remove takes
                // only the gate), so there is no inversion.
                lock (_outputInvocationLock)
                {
                    string[]? replay = null;
                    lock (_outputHandlerGate)
                    {
                        if (!_hasOutputSubscriberEver)
                        {
                            _hasOutputSubscriberEver = true;
                            if (_pendingOutputReplay != null)
                            {
                                replay = _pendingOutputReplay.ToArray();
                                _pendingOutputReplay = null;
                            }
                        }
                        _onOutputReceived += value;
                    }

                    if (replay != null)
                    {
                        foreach (var text in replay)
                        {
                            value(text);
                        }
                    }
                }
            }
            remove
            {
                if (value == null) return;
                lock (_outputHandlerGate)
                {
                    _onOutputReceived -= value;
                }
            }
        }

        // Delivers decoded output to the current subscriber, or buffers it for
        // replay when none has attached yet. Called only from ProcessLoop. The
        // outer invocation lock makes the whole capture-and-invoke atomic against
        // the add-replay above and other emits, so it never runs before or during
        // that replay.
        private void EmitOutput(string text)
        {
            lock (_outputInvocationLock)
            {
                Action<string>? handler;
                lock (_outputHandlerGate)
                {
                    handler = _onOutputReceived;
                    if (!_hasOutputSubscriberEver && handler == null)
                    {
                        _pendingOutputReplay ??= new List<string>();
                        _pendingOutputReplay.Add(text);
                        return;
                    }
                }

                handler?.Invoke(text);
            }
        }

        public event Action<int>? OnExit;
        public bool IsProcessRunning => Volatile.Read(ref _isExited) == 0 && !_handle.IsClosed && !_handle.IsInvalid;
        public int? ExitCode => _exitCode;

        // DllImport definitions
        private static class Native
        {
            const string LibName = "rusty_pty";

            // NOTE: every string crossing this boundary must be marshalled as UTF-8.
            // The Rust side decodes with CStr::to_string_lossy() (UTF-8); the DllImport
            // default is ANSI (active codepage) on Windows, which silently mangled any
            // non-ASCII cmd/cwd/args/env into U+FFFD replacement bytes (#152).
            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern PtySafeHandle pty_create(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string cmd, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern PtySafeHandle pty_spawn(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string cmd,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string? args,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string? cwd,
                ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern PtySafeHandle pty_spawn_with_envs(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string cmd,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string? args,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string? cwd,
                ushort cols, ushort rows,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string envs);

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

            // Thread-local last-failure message (#120 item 3). Must be read on the same thread
            // that made the failing call. Writes NUL-terminated UTF-8 and returns the byte count
            // excluding the NUL; 0 when there is nothing to report, -1 on bad arguments.
            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_last_error(byte[] buffer, int len);
        }

        /// <summary>
        /// The native layer's message for the most recent failure on this thread, or null if it
        /// had nothing to say.
        /// </summary>
        /// <remarks>
        /// Before this existed, every spawn failure surfaced as the same
        /// "Failed to create Rust PTY session." — a missing shell binary, a deleted working
        /// directory and an openpty failure were indistinguishable, which is exactly the
        /// information a user needs when a tab refuses to open.
        /// </remarks>
        private static string? TryGetNativeLastError()
        {
            try
            {
                // 1 KiB is comfortably more than any message the native side produces; it
                // truncates on a char boundary rather than overflowing, so a long path costs
                // detail, not correctness.
                var buffer = new byte[1024];
                int written = Native.pty_last_error(buffer, buffer.Length);
                if (written <= 0) return null;
                return Encoding.UTF8.GetString(buffer, 0, written);
            }
            catch (EntryPointNotFoundException)
            {
                // A native library predating this export. This helper exists only to *improve* an
                // error message, so it must never replace one with something worse — without this
                // catch, a stale rusty_pty turned "failed to spawn <shell>" into an unrelated
                // EntryPointNotFoundException from inside the failure path.
                return null;
            }
            catch (DllNotFoundException)
            {
                return null;
            }
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
                int pid;
                try { pid = Native.pty_get_pid(_handle); }
                catch (ObjectDisposedException) { return false; }
                if (pid <= 0) return false;
                return HasChildProcesses(pid, ShellCommand);
            }
        }

        public int? Pid
        {
            get
            {
                if (_handle.IsClosed || _handle.IsInvalid) return null;
                try
                {
                    int pid = Native.pty_get_pid(_handle);
                    return pid > 0 ? pid : null;
                }
                catch (ObjectDisposedException) { return null; }
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

        /// Message for a spawn attempt with no command. Shared with the tests so the
        /// contract is asserted, not re-typed.
        internal const string EmptyCommandMessage =
            "Cannot start a terminal session: no shell command was supplied.";

        /// <summary>
        /// Rejects a command that is empty or whitespace, before it reaches the native layer.
        /// </summary>
        /// <remarks>
        /// An empty command is not a spawn failure the OS reports usefully. On Windows,
        /// portable-pty resolves the program name by joining it onto each %PATH% entry
        /// (cmdbuilder.rs <c>search_path</c>) and taking the first candidate that *exists* —
        /// and <c>Path::join("")</c> of a directory is that directory, which exists. So an
        /// empty command resolves to the first directory on %PATH% and CreateProcessW is
        /// asked to execute a folder, failing with "Access is denied. (os error 5)". The
        /// reported command line is then a %PATH% entry the user never typed, which sends
        /// debugging in entirely the wrong direction (it named a VMware directory in the
        /// original report). Fail here, where the message can say what is actually wrong.
        /// </remarks>
        internal static string ValidateShellCommand(string? shellCommand)
        {
            if (string.IsNullOrWhiteSpace(shellCommand))
            {
                throw new ArgumentException(EmptyCommandMessage, nameof(shellCommand));
            }

            return RejectEmbeddedNuls(shellCommand, nameof(shellCommand))!;
        }

        /// <summary>
        /// Rejects a string carrying an embedded NUL before it is marshalled to the native side.
        /// </summary>
        /// <remarks>
        /// Every string on this boundary is marshalled as <see cref="UnmanagedType.LPUTF8Str"/>,
        /// i.e. a NUL-terminated C string, and Rust reads it back with
        /// <c>CStr::from_ptr</c> — which stops at the FIRST NUL. An embedded NUL therefore
        /// cannot be detected on the Rust side at all: it silently truncates, and the child
        /// is spawned with a command/cwd that is a prefix of what the caller asked for. This
        /// is the only place the whole string is still visible, so it is the only place the
        /// check can be made.
        /// </remarks>
        internal static string? RejectEmbeddedNuls(string? value, string parameterName)
        {
            if (value != null && value.IndexOf('\0') >= 0)
            {
                throw new ArgumentException(
                    $"Cannot start a terminal session: {parameterName} contains an embedded NUL character, " +
                    "which would be silently truncated when passed to the native PTY layer.",
                    parameterName);
            }

            return value;
        }

        /// The PTY read call, injectable so tests can drive the read loop's failure
        /// handling. `pty_read` returns the byte count, 0 for EOF, or a negative value for
        /// any error - see MaxConsecutiveReadErrors.
        internal delegate int PtyReadDelegate(PtySafeHandle handle, byte[] buffer, int length);

        private readonly PtyReadDelegate _readFromPty;

        public RustPtySession(
            string shellCommand,
            int cols = 120,
            int rows = 30,
            string? args = null,
            string? cwd = null,
            bool skipPowerShellPostLaunchInit = false,
            IReadOnlyDictionary<string, string>? environmentOverrides = null)
            : this(
                shellCommand,
                cols,
                rows,
                args,
                cwd,
                skipPowerShellPostLaunchInit,
                environmentOverrides,
                readFromPty: null)
        {
        }

        /// Test-facing constructor. Identical to the public one except that the PTY read
        /// call can be substituted: `Native.pty_read` is a static P/Invoke, so without a
        /// seam here the read loop's error handling (bounded retry, teardown, failure exit
        /// code) is unreachable from a test. The session still spawns a real shell, so the
        /// teardown path is exercised against a real handle and a real child process.
        internal RustPtySession(
            string shellCommand,
            int cols,
            int rows,
            string? args,
            string? cwd,
            bool skipPowerShellPostLaunchInit,
            IReadOnlyDictionary<string, string>? environmentOverrides,
            PtyReadDelegate? readFromPty)
        {
            // Validate before anything else: everything below either marshals these strings
            // to the native layer or derives from them. A bad value must fail here with a
            // message that names the problem, not deep inside CreateProcessW.
            shellCommand = ValidateShellCommand(shellCommand);
            args = RejectEmbeddedNuls(args, nameof(args));
            cwd = RejectEmbeddedNuls(cwd, nameof(cwd));
            if (environmentOverrides != null)
            {
                foreach (var kv in environmentOverrides)
                {
                    RejectEmbeddedNuls(kv.Key, "environment variable name");
                    RejectEmbeddedNuls(kv.Value, $"the value of environment variable '{kv.Key}'");
                }
            }

            _readFromPty = readFromPty ?? Native.pty_read;
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

            PtyLogger.Info($"[RustPtySession] Spawning '{effectiveShell}' args='{combinedArgs}' cwd='{cwd}' at {cols}x{rows}");
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
                // Read the native reason immediately: it is thread-local and the next spawn on
                // this thread clears it.
                string? reason = TryGetNativeLastError();
                throw new InvalidOperationException(
                    reason is null
                        ? $"Failed to create Rust PTY session for '{effectiveShell}'."
                        : $"Failed to create Rust PTY session for '{effectiveShell}': {reason}");
            }

            // Start reading and processing on DEDICATED background threads, not the
            // threadpool. These loops make blocking native calls (pty_read) and an
            // outright-blocking consuming enumerator; on the threadpool a leaked or
            // slow-to-close session would tie up pool threads and, on low-core CI,
            // starve the test-run completion -> testhost teardown hang (#81). Dedicated
            // IsBackground threads never consume the pool and never block process exit.
            _readLoopThread = new Thread(ReadLoop) { IsBackground = true, Name = $"PtyRead-{Id:N}" };
            _processLoopThread = new Thread(ProcessLoop) { IsBackground = true, Name = $"PtyProcess-{Id:N}" };
            _writeLoopThread = new Thread(WriteLoop) { IsBackground = true, Name = $"PtyWrite-{Id:N}" };
            _readLoopThread.Start();
            _processLoopThread.Start();
            _writeLoopThread.Start();

            // POST-LAUNCH INJECTION for PowerShell
            if (!skipPowerShellPostLaunchInit &&
                (shellLower.Contains("powershell") || shellLower.Contains("pwsh")))
            {
                // Tracked (not discarded) so Dispose can observe failures, and cancelled
                // with the session so closing a tab inside the delay window doesn't
                // inject into a dead handle.
                _powerShellInitTask = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(300, _cts.Token).ConfigureAwait(false);

                        string tempScript = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(),
                            $"nova_init_{Guid.NewGuid()}.ps1");
                        string cleanPath = tempScript.Replace("'", "''");

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
                        // 4. Delete this script. Self-deletion is what actually fixes the
                        //    %TEMP% leak (#107): the file is removed the moment the shell
                        //    has finished sourcing it, with no timer to guess at. The
                        //    Dispose-time cleanup below is only a backstop for the case
                        //    where the shell never runs it at all.
                        sb.AppendLine($"Remove-Item -LiteralPath '{cleanPath}' -Force -ErrorAction SilentlyContinue;");

                        // Record the path before writing, so a failure between write and
                        // injection still leaves something for Dispose to clean up.
                        _powerShellInitScriptPath = tempScript;

                        // Deliberately the synchronous write, matching the original code.
                        // An awaited WriteAllTextAsync adds a scheduling hop between the
                        // delay and SendInput, which moved the injected keystrokes later
                        // and made PtySmokeTests.AgentSentInput_IsByteFaithful flaky - the
                        // injection landed inside that test's recording window. Injection
                        // timing is observable behaviour, so it is kept as it was.
                        System.IO.File.WriteAllText(tempScript, sb.ToString());

                        SendInput($"& '{cleanPath}'\r");
                    }
                    catch (OperationCanceledException)
                    {
                        // Session closed inside the delay window — nothing to inject.
                    }
                    catch (Exception ex)
                    {
                        PtyLogger.Warning($"[RustPtySession] PS Injection Failed: {ex.Message}");
                    }
                    finally
                    {
                        // Close the window where Dispose finished before this task did.
                        // Dispose waits only briefly for us; if the write ran long, its
                        // single delete attempt could hit a file still open here, or run
                        // before the file existed at all. Whichever of the two finishes
                        // last performs the delete, so neither ordering leaks.
                        //
                        // Guarded on teardown: on the happy path the shell sources the
                        // script and it deletes itself, and deleting it here would race
                        // that read.
                        if (_cts.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                        {
                            TryDeleteInitScript();
                        }
                    }
                });
            }
        }

        private NovaTerminal.Replay.ReplayWriter? _recorder;

        // Flight recorder ring (agent replay export). Written from the read loop and
        // Resize; enabled/disabled from the App's agent-host lifecycle. Reference
        // swap is atomic; loops observe it with the same null-conditional pattern as
        // _recorder. Never records input — see ITerminalFlightRecorder.
        private NovaTerminal.Replay.FlightRecordingBuffer? _flightRecorder;

        public bool IsRecording => _recorder != null;

        public bool IsFlightRecording => _flightRecorder != null;

        public void EnableFlightRecording(long maxTotalBytes)
        {
            if (_flightRecorder != null) return; // Already enabled
            // Defensive fallback: geometry should always be positive here, but the
            // ring constructor rejects non-positive dimensions and enabling must
            // never throw at the agent-host lifecycle call site.
            int cols = _cols > 0 ? _cols : 80;
            int rows = _rows > 0 ? _rows : 24;
            _flightRecorder = new NovaTerminal.Replay.FlightRecordingBuffer(maxTotalBytes, cols, rows);
        }

        public void DisableFlightRecording()
        {
            _flightRecorder = null;
        }

        public bool TryExportFlightRecording(string filePath, out NovaTerminal.Replay.FlightExportInfo info)
        {
            var ring = _flightRecorder;
            if (ring == null)
            {
                info = default;
                return false;
            }

            try
            {
                info = ring.ExportTo(filePath, ShellCommand);
                return true;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Try-pattern: expected I/O failures (bad path, permissions, full
                // disk) must not crash the host on an agent-triggered export.
                PtyLogger.Warning($"[RustPtySession] Flight recording export failed: {ex.Message}");
                info = default;
                return false;
            }
        }

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
                PtyLogger.Warning($"[RustPtySession] Recording start marker failed: {ex.Message}");
            }

            _recorder = recorder;
            PtyLogger.Info($"[RustPtySession] Recording started to: {filePath}");
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
                PtyLogger.Warning($"[RustPtySession] Recording stop marker failed: {ex.Message}");
            }

            try
            {
                recorder.Dispose();
            }
            catch (Exception ex)
            {
                PtyLogger.Warning($"[RustPtySession] Recorder dispose failed: {ex.Message}");
            }

            PtyLogger.Info("[RustPtySession] Recording stopped.");
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];
            // Sized via GetMaxCharCount, NOT buffer.Length: the stateful decoder can carry
            // up to 3 pending bytes from the previous read, so a full 4096-byte read can
            // decode to 4097 chars. With a same-sized buffer GetChars threw
            // ArgumentException and the catch-all below terminated the loop — the session
            // went silently mute mid-stream (#168).
            char[] charBuffer = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

            // This runs on a dedicated thread, so an unhandled exception would crash the
            // whole process (unlike the old Task.Run, whose unobserved exceptions were
            // swallowed). Contain it so a decode/recorder failure can't take down the host.
            // Set when the loop gives up after repeated read failures, so the exit is
            // reported as a failure rather than the clean 0 ProcessLoop would send.
            bool readFailed = false;
            int consecutiveReadErrors = 0;

            try
            {
                while (!_cts.Token.IsCancellationRequested && !_handle.IsInvalid)
                {
                    int read = _readFromPty(_handle, buffer, buffer.Length);
                    if (read > 0)
                    {
                        consecutiveReadErrors = 0;

                        // Record raw bytes before any processing
                        _recorder?.RecordChunk(buffer, read);
                        _flightRecorder?.RecordChunk(buffer, read);

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
                        PtyLogger.Info("[RustPtySession] EOF received.");
                        break;
                    }
                    else // read < 0: error
                    {
                        // Reset decoder state on error to prevent corruption
                        _utf8Decoder.Reset();

                        if (++consecutiveReadErrors >= MaxConsecutiveReadErrors)
                        {
                            // Fail the session instead of spinning forever. The native reason is
                            // read on this thread, which is the same thread that made the failing
                            // pty_read - the channel is thread-local (#120 item 3).
                            string? reason = TryGetNativeLastError();
                            PtyLogger.Error(
                                $"[RustPtySession] pty_read failed {consecutiveReadErrors} times consecutively; ending session."
                                + (reason is null ? string.Empty : $" Last native error: {reason}"));
                            readFailed = true;
                            break;
                        }

                        Thread.Sleep(ReadErrorRetryDelay);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // _handle was disposed by Dispose() — normal shutdown.
            }
            catch (Exception ex)
            {
                PtyLogger.Error($"[RustPtySession] ReadLoop terminated by unhandled exception: {ex}");
            }
            finally
            {
                if (readFailed)
                {
                    // Claim the exit code FIRST. TryNotifyExit is first-caller-wins, and
                    // everything in the teardown below releases ProcessLoop, which then
                    // reports TryNotifyExit(0) from its own thread:
                    //   - _cts.Cancel() cancels its GetConsumingEnumerable token
                    //   - _outputQueue.CompleteAdding() (further down) ends its enumeration
                    // Notifying after any of those is a race, and it is one this code lost
                    // on Windows CI while winning it locally - the session reported a clean
                    // exit 0 after an unrecoverable read failure.
                    //
                    // The cost of this ordering is that subscribers observe the exit a few
                    // microseconds before the handle is released. That is the lesser evil: a
                    // brief window versus a permanently wrong exit code.
                    TryNotifyExit(ReadFailureExitCode);

                    // Then tear down. Reporting the exit alone would leave the UI recording
                    // a terminated session while the child process, the writer thread and
                    // the native handle all stayed alive — nothing else disposes us on our
                    // own initiative (MainWindow.OnPaneProcessExited only records the code).
                    //
                    // Deliberately not calling Dispose(): it joins this very thread, and
                    // Thread.Join on the current thread is invalid, which would abort the
                    // rest of the teardown. This does the subset that is safe from here.
                    try
                    {
                        _cts.Cancel();

                        if (!_inputQueue.IsAddingCompleted)
                        {
                            _inputQueue.CompleteAdding();
                        }

                        // Releases the PTY, which ends the child and unblocks a writer
                        // parked in pty_write. Safe from this thread: the SafeHandle
                        // refcount makes pty_close wait for any in-flight pty_* call, and
                        // Dispose() is idempotent so a later disposal still works.
                        _handle.Dispose();

                        // Make the emergency path self-contained rather than deferring to
                        // the owner's eventual Dispose. If the shell died without sourcing
                        // the init script, the script would otherwise survive for as long
                        // as the dead pane stayed open. Safe here: the handle is already
                        // released, so no shell can be mid-source.
                        TryDeleteInitScript();
                    }
                    catch (Exception ex)
                    {
                        PtyLogger.Error($"[RustPtySession] teardown after read failure failed: {ex.Message}");
                    }
                }

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
                    EmitOutput(text);
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
                PtyLogger.Error($"[RustPtySession] ProcessLoop terminated by unhandled exception: {ex}");
            }
            TryNotifyExit(0);
        }

        public void SendInput(string input)
        {
            if (_handle.IsClosed || _handle.IsInvalid) return;

            _recorder?.RecordInput(input);

            byte[] data = Encoding.UTF8.GetBytes(input);
            try
            {
                // Queue for the dedicated writer thread — never write on the caller
                // thread; see _inputQueue. Ordering is preserved (single consumer).
                _inputQueue.Add(data, _cts.Token);
            }
            catch (OperationCanceledException) { /* session disposing — drop the write */ }
            catch (InvalidOperationException) { /* adding completed — session closing */ }
        }

        private void WriteLoop()
        {
            // Contained like ReadLoop/ProcessLoop: this runs on a dedicated thread, so an
            // unhandled exception would crash the process.
            try
            {
                foreach (var data in _inputQueue.GetConsumingEnumerable(_cts.Token))
                {
                    try
                    {
                        // Rust side does write_all: success returns the full length,
                        // failure returns -1 (#168). Input loss must not be silent.
                        int written = Native.pty_write(_handle, data, data.Length);
                        if (written != data.Length)
                        {
                            PtyLogger.Warning($"[RustPtySession] pty_write returned {written} (expected {data.Length}); input may be lost");
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        return; // handle released — session is gone
                    }
                }
            }
            catch (OperationCanceledException) { /* dispose */ }
            catch (Exception ex)
            {
                PtyLogger.Error($"[RustPtySession] WriteLoop terminated by unhandled exception: {ex}");
            }
        }

        public void Resize(int cols, int rows)
        {
            if (_handle.IsClosed || _handle.IsInvalid || cols <= 0 || rows <= 0) return;
            _cols = cols;
            _rows = rows;
            PtyLogger.Debug($"[RustPtySession] Resizing to {cols}x{rows}");
            try { Native.pty_resize(_handle, (ushort)cols, (ushort)rows); }
            catch (ObjectDisposedException) { /* session disposed mid-call — ignore resize */ }
            _recorder?.RecordResize(cols, rows);
            _flightRecorder?.RecordResize(cols, rows);
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
                    PtyLogger.Warning($"[RustPtySession] StopRecording during dispose failed: {ex.Message}");
                }
            }

            DisableFlightRecording();

            // 1. Stop the loops re-entering native calls, and let the process loop drain.
            _cts.Cancel();

            if (!_outputQueue.IsAddingCompleted)
            {
                _outputQueue.CompleteAdding();
            }
            if (!_inputQueue.IsAddingCompleted)
            {
                _inputQueue.CompleteAdding();
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
                    PtyLogger.Warning("[RustPtySession] ReadLoop did not exit within join timeout.");
                }
            }

            // 4. Join the process loop (it exits once the queue is completed/cancelled).
            if (!(_processLoopThread?.Join(DisposeJoinTimeout) ?? true))
            {
                PtyLogger.Warning("[RustPtySession] ProcessLoop did not exit within join timeout.");
            }

            // 4b. Join the writer. A write blocked on a full pipe unblocks when the
            //     handle below is released (pty_close tears down the pipe); the thread is
            //     IsBackground, so a timed-out join can never block process exit.
            if (!(_writeLoopThread?.Join(DisposeJoinTimeout) ?? true))
            {
                PtyLogger.Warning("[RustPtySession] WriteLoop did not exit within join timeout.");
            }

            // 5. Release the handle. SafeHandle guarantees pty_close runs only once
            //    no pty_* call is in flight, so this is UAF-safe even if a join timed out.
            _handle.Dispose();

            // 6. Observe the PowerShell injection task and remove its script. Previously
            //    the task was discarded and the file never deleted, leaking one
            //    nova_init_{guid}.ps1 per PowerShell session (#107).
            //
            //    Ordered last on purpose: by here the shell is gone, so it cannot be
            //    holding the script open (Windows refuses to delete a file whose open
            //    handle lacks FILE_SHARE_DELETE). That hazard is reasoned, not observed -
            //    on the happy path the script deletes itself the moment it is sourced, so
            //    this call is usually a no-op and the placement is untestable. Last is
            //    simply the position with no failure mode.
            CleanUpPowerShellInit();

            TryNotifyExit(0);
        }

        /// Observes the injection task's outcome and removes its script if the shell
        /// never sourced it. Bounded and fully guarded: dispose must not block on, or be
        /// derailed by, best-effort cleanup.
        private void CleanUpPowerShellInit()
        {
            Task? initTask = _powerShellInitTask;
            if (initTask != null)
            {
                try
                {
                    // Bounded: the task is either already cancelled by _cts or mid-write.
                    // A timeout is not an error - the file cleanup below still runs.
                    initTask.Wait(TimeSpan.FromMilliseconds(250));
                }
                catch (AggregateException ex)
                    when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
                {
                    // Expected: cancelled by _cts.Cancel() above.
                }
                catch (Exception ex)
                {
                    // The whole point of tracking the task: a failure here used to be
                    // swallowed as an unobserved exception.
                    PtyLogger.Warning($"[RustPtySession] PS injection task faulted: {ex.Message}");
                }
            }

            TryDeleteInitScript();
        }

        /// Removes the init script if one was written. Idempotent and never throws, so it
        /// is safe to call from both Dispose and the injection task's finally - whichever
        /// runs last wins, which is what makes the two orderings equivalent.
        private void TryDeleteInitScript()
        {
            string? scriptPath = _powerShellInitScriptPath;
            if (scriptPath == null) return;

            try
            {
                // Normally already gone - the script's last line deletes itself once the
                // shell sources it, so this is the backstop for the case where the shell
                // never ran it (spawn failed, session closed first, injection faulted).
                System.IO.File.Delete(scriptPath);
            }
            catch (Exception ex)
            {
                // Best effort: a leftover temp script must never fail a disposal.
                PtyLogger.Warning($"[RustPtySession] PS init script cleanup failed: {ex.Message}");
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
