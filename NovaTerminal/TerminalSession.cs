using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal
{
    public class TerminalSession : IDisposable
    {
        private Process? _process;
        private readonly string _shell;
        public event Action<string>? OnOutputReceived;
        private bool _stopReading;

        public TerminalSession(string shell = "cmd.exe")
        {
            _shell = shell;
        }

        public async Task StartAsync()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _shell,
                // Start with echo off to prevent double echoing of commands 
                // Arguments = "/q", // Removed global argument
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Custom arguments for specific shells
            if (_shell.EndsWith("cmd.exe"))
            {
                startInfo.Arguments = "/q";
            }
            else if (_shell.EndsWith("powershell.exe") || _shell.EndsWith("pwsh.exe") || _shell.EndsWith("pwsh"))
            {
                // Force PowerShell to use UTF-8 for output immediately
                startInfo.Arguments = "-NoExit -Command \"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\"";
            }
            
            // Force tools to output color even when redirected
            startInfo.Environment["TERM"] = "xterm-256color";
            startInfo.Environment["COLORTERM"] = "truecolor";
            startInfo.Environment["CLICOLOR_FORCE"] = "1"; // Force color for some tools
            startInfo.Environment["LANG"] = "C.UTF-8";
            startInfo.Environment["LC_ALL"] = "C.UTF-8";
            
            // Hint initial size to the shell (Commonly 120x30 for modern HD screens)
            startInfo.Environment["COLUMNS"] = "120";
            startInfo.Environment["LINES"] = "30";
            
            // For git specifically, it often checks isatty, but we can try basic vars. 
            // Note: Git for Windows might need 'git -c color.ui=always' if it strictly checks handles.

            _process = new Process { StartInfo = startInfo };
            _process.Start();
            
            // Turn off echo explicitly just in case /q doesn't cover interactive pipe
            // _process.StandardInput.WriteLine("@echo off"); 

            // Initialize Encoding
            // Windows Console default is often CP437, which converts UTF-8 chars to '?'
            // We must force code page 65001 (UTF-8).
            
            // For CMD, we inject chcp. For PowerShell, we used the startup arg.
            bool isPowerShell = _shell.EndsWith("powershell.exe") || _shell.EndsWith("pwsh.exe") || _shell.EndsWith("pwsh");
            
            if (!_shell.Contains("wsl") && !_shell.Contains("bash") && !isPowerShell)
            {
                // CMD needs explicit switch, and we silence output with > nul
                _process.StandardInput.WriteLine("chcp 65001 > nul");
                _process.StandardInput.WriteLine("cls"); 
                _process.StandardInput.Flush();
            }
            
            // Start reading threads
            _ = Task.Run(() => ReadStream(_process.StandardOutput.BaseStream));
            _ = Task.Run(() => ReadStream(_process.StandardError.BaseStream));
            
            await Task.CompletedTask;
        }

        private async Task ReadStream(Stream stream)
        {
            // Use StreamReader to handle UTF-8 multi-byte characters correctly across buffer boundaries
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var buffer = new char[4096];
            
            try
            {
                while (!_stopReading)
                {
                    int charsRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                    if (charsRead == 0) break; // End of stream

                    // DEBUG: Log raw bytes
                    var bytes = System.Text.Encoding.UTF8.GetBytes(buffer, 0, charsRead);
                    var hex = BitConverter.ToString(bytes).Replace("-", " ");
                    await System.IO.File.AppendAllTextAsync("d:/projects/nova2/NovaTerminal/ansi_debug.txt", $"{hex}\n");

                    string text = new string(buffer, 0, charsRead);
                    OnOutputReceived?.Invoke(text);
                }
            }
            catch { }
        }

        public void SendInput(string input)
        {
            if (_process != null && !_process.HasExited)
            {
                // WSL/Linux expects \n. Windows msg be okay with \r\n, but PowerShell might also prefer \n.
                // Safest for cross-platform tools is often just \n.
                // But cmd.exe DEFINITELY needs \r\n (or specific behavior).
                
                string newline = "\r\n";
                if (_shell.Contains("wsl") || _shell.Contains("bash"))
                {
                    newline = "\n";
                }

                if (input == "\r") 
                {
                   _process.StandardInput.Write(newline); 
                }
                else 
                {
                   _process.StandardInput.Write(input);
                }
                _process.StandardInput.Flush();
            }
        }

        public void Dispose()
        {
            _stopReading = true;
            try {
                _process?.Kill();
                _process?.Dispose();
            } catch {}
        }
    }
}
