using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Core.Execution
{
    public interface IProcessRunner
    {
        Task<(int ExitCode, string StdOut)> RunProcessAsync(string fileName, string arguments, CancellationToken ct = default);
    }

    public class DefaultProcessRunner : IProcessRunner
    {
        public async Task<(int ExitCode, string StdOut)> RunProcessAsync(string fileName, string arguments, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return (-1, string.Empty);
            }

            var tcs = new TaskCompletionSource<bool>();
            using var registration = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                tcs.TrySetCanceled();
            });

            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(ct);

            return (process.ExitCode, stdout);
        }
    }
}
