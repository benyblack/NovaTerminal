using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NovaTerminal.Core;

namespace NovaTerminal.ExternalSuites.Vttest
{
    public sealed class VttestDriver
    {
        private readonly VttestCapture _pty;
        private DateTime _lastActivity;
        private readonly object _lock = new();

        public VttestDriver(VttestCapture pty)
        {
            _pty = pty;
            // Use property that notifies activity (user changed this to pass string.Empty)
            _pty.OnOutputReceived += _ =>
            {
                lock (_lock)
                {
                    _lastActivity = DateTime.UtcNow;
                }
            };
            _lastActivity = DateTime.UtcNow;
        }

        public async Task ExecuteStepsAsync(IEnumerable<Step> steps)
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case SendBytes send:
                        // Use the new byte-exact SendInputBytes implemented by user
                        _pty.SendInputBytes(send.Bytes);
                        break;

                    case WaitForQuiet quiet:
                        await WaitInternal(quiet.QuietMs, quiet.MaxWaitMs);
                        break;
                }
            }
        }

        private async Task WaitInternal(int quietMs, int maxWaitMs)
        {
            DateTime startWait = DateTime.UtcNow;
            while (true)
            {
                DateTime now = DateTime.UtcNow;
                TimeSpan elapsedSinceLastActivity;
                lock (_lock)
                {
                    elapsedSinceLastActivity = now - _lastActivity;
                }

                if (elapsedSinceLastActivity.TotalMilliseconds >= quietMs)
                {
                    break; // Quiet enough
                }

                if ((now - startWait).TotalMilliseconds >= maxWaitMs)
                {
                    break; // Timed out
                }

                await Task.Delay(50);
            }
        }
    }
}
