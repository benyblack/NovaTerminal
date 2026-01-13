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
        public event Action<string>? OnOutputReceived;
        private bool _stopReading;

        public async Task StartAsync()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // Start with echo off to prevent double echoing of commands
                Arguments = "/q", 
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Force tools to output color even when redirected
            startInfo.Environment["TERM"] = "xterm-256color";
            startInfo.Environment["COLORTERM"] = "truecolor";
            startInfo.Environment["CLICOLOR_FORCE"] = "1"; // Force color for some tools
            // For git specifically, it often checks isatty, but we can try basic vars. 
            // Note: Git for Windows might need 'git -c color.ui=always' if it strictly checks handles.

            _process = new Process { StartInfo = startInfo };
            _process.Start();
            
            // Turn off echo explicitly just in case /q doesn't cover interactive pipe
            // _process.StandardInput.WriteLine("@echo off"); 

            // Start reading threads
            _ = Task.Run(() => ReadStream(_process.StandardOutput.BaseStream));
            _ = Task.Run(() => ReadStream(_process.StandardError.BaseStream));
            
            await Task.CompletedTask;
        }

        private async Task ReadStream(Stream stream)
        {
            var buffer = new byte[1024];
            try
            {
                while (!_stopReading)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break; // End of stream

                    string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    OnOutputReceived?.Invoke(text);
                }
            }
            catch { }
        }

        public void SendInput(string input)
        {
            if (_process != null && !_process.HasExited)
            {
                if (input == "\r") 
                {
                   _process.StandardInput.Write("\r\n"); 
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
