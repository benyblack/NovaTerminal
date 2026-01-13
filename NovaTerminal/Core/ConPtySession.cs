using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Core
{
    /// <summary>
    /// Manages a Windows Pseudo Console (ConPTY) session with a child process
    /// </summary>
    public class ConPtySession : IDisposable
    {
        private readonly string _shell;
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _hPipeIn = IntPtr.Zero;
        private IntPtr _hPipeOut = IntPtr.Zero;
        private Process? _process;
        private bool _stopReading;

        public event Action<string>? OnOutputReceived;

        public ConPtySession(string shell = "cmd.exe")
        {
            _shell = shell;
        }

        /// <summary>
        /// Starts the ConPTY session with the specified dimensions
        /// </summary>
        public async Task StartAsync(int cols, int rows)
        {
            // Create pipes for ConPTY I/O
            if (!ConPtyNative.CreatePipe(out IntPtr hPipeInRead, out IntPtr hPipeInWrite, IntPtr.Zero, 0))
            {
                throw new Exception($"Failed to create input pipe: {Marshal.GetLastWin32Error()}");
            }

            if (!ConPtyNative.CreatePipe(out IntPtr hPipeOutRead, out IntPtr hPipeOutWrite, IntPtr.Zero, 0))
            {
                ConPtyNative.CloseHandle(hPipeInRead);
                ConPtyNative.CloseHandle(hPipeInWrite);
                throw new Exception($"Failed to create output pipe: {Marshal.GetLastWin32Error()}");
            }

            // Create the pseudo console
            var size = new ConPtyNative.COORD((short)cols, (short)rows);
            int result = ConPtyNative.CreatePseudoConsole(size, hPipeInRead, hPipeOutWrite, 0, out _hPC);

            if (result != 0)
            {
                ConPtyNative.CloseHandle(hPipeInRead);
                ConPtyNative.CloseHandle(hPipeInWrite);
                ConPtyNative.CloseHandle(hPipeOutRead);
                ConPtyNative.CloseHandle(hPipeOutWrite);
                throw new Exception($"Failed to create pseudo console: {result}, Error: {Marshal.GetLastWin32Error()}");
            }

            // Store the pipe handles we'll use for I/O
            _hPipeIn = hPipeInWrite;  // We write to this
            _hPipeOut = hPipeOutRead; // We read from this

            // Close the handles the ConPTY owns (it duplicated them)
            ConPtyNative.CloseHandle(hPipeInRead);
            ConPtyNative.CloseHandle(hPipeOutWrite);

            // Launch the child process attached to the ConPTY
            await LaunchProcessAsync();

            // Start reading output
            _ = Task.Run(ReadOutputLoop);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Resizes the pseudo console
        /// </summary>
        public void Resize(int cols, int rows)
        {
            if (_hPC == IntPtr.Zero) return;

            var size = new ConPtyNative.COORD((short)cols, (short)rows);
            int result = ConPtyNative.ResizePseudoConsole(_hPC, size);

            if (result != 0)
            {
                Debug.WriteLine($"Failed to resize pseudo console: {result}, Error: {Marshal.GetLastWin32Error()}");
            }
        }

        /// <summary>
        /// Sends input to the shell
        /// </summary>
        public void SendInput(string input)
        {
            if (_hPipeIn == IntPtr.Zero) return;

            byte[] data = Encoding.UTF8.GetBytes(input);
            ConPtyNative.WriteFile(_hPipeIn, data, (uint)data.Length, out _, IntPtr.Zero);
        }

        private async Task LaunchProcessAsync()
        {
            // Prepare attribute list for ConPTY
            IntPtr lpSize = IntPtr.Zero;
            ConPtyNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);

            IntPtr lpAttributeList = Marshal.AllocHGlobal(lpSize);
            
            try
            {
                if (!ConPtyNative.InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref lpSize))
                {
                    throw new Exception($"Failed to initialize attribute list: {Marshal.GetLastWin32Error()}");
                }

                // Attach the ConPTY to the attribute list
                IntPtr hPC = _hPC;
                if (!ConPtyNative.UpdateProcThreadAttribute(
                    lpAttributeList,
                    0,
                    (IntPtr)ConPtyNative.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    throw new Exception($"Failed to update proc thread attribute: {Marshal.GetLastWin32Error()}");
                }

                // Prepare startup info
                var startupInfo = new ConPtyNative.STARTUPINFOEX();
                startupInfo.StartupInfo.cb = Marshal.SizeOf<ConPtyNative.STARTUPINFOEX>();
                startupInfo.lpAttributeList = lpAttributeList;

                var processAttributes = new ConPtyNative.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>()
                };

                var threadAttributes = new ConPtyNative.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>()
                };

                // Launch the process
                string commandLine = _shell;
                bool success = ConPtyNative.CreateProcess(
                    null,
                    commandLine,
                    ref processAttributes,
                    ref threadAttributes,
                    false,
                    ConPtyNative.EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero,
                    null,
                    ref startupInfo,
                    out ConPtyNative.PROCESS_INFORMATION processInfo);

                if (!success)
                {
                    throw new Exception($"Failed to create process: {Marshal.GetLastWin32Error()}");
                }

                // Wrap in Process object for management
                _process = Process.GetProcessById(processInfo.dwProcessId);

                // Close thread handle (we don't need it)
                ConPtyNative.CloseHandle(processInfo.hThread);

                await Task.CompletedTask;
            }
            finally
            {
                ConPtyNative.DeleteProcThreadAttributeList(lpAttributeList);
                Marshal.FreeHGlobal(lpAttributeList);
            }
        }

        private async Task ReadOutputLoop()
        {
            byte[] buffer = new byte[4096];

            try
            {
                while (!_stopReading && _hPipeOut != IntPtr.Zero)
                {
                    bool success = ConPtyNative.ReadFile(_hPipeOut, buffer, (uint)buffer.Length, out uint bytesRead, IntPtr.Zero);

                    if (!success || bytesRead == 0)
                    {
                        break; // Pipe closed or error
                    }

                    string text = Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
                    OnOutputReceived?.Invoke(text);

                    await Task.Delay(1); // Yield to prevent tight loop
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading from ConPTY: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _stopReading = true;

            try
            {
                _process?.Kill();
                _process?.Dispose();
            }
            catch { }

            if (_hPipeIn != IntPtr.Zero)
            {
                ConPtyNative.CloseHandle(_hPipeIn);
                _hPipeIn = IntPtr.Zero;
            }

            if (_hPipeOut != IntPtr.Zero)
            {
                ConPtyNative.CloseHandle(_hPipeOut);
                _hPipeOut = IntPtr.Zero;
            }

            if (_hPC != IntPtr.Zero)
            {
                ConPtyNative.ClosePseudoConsole(_hPC);
                _hPC = IntPtr.Zero;
            }
        }
    }
}
