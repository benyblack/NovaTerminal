using System;

namespace NovaTerminal.Shell
{
    internal enum TabTrackerStatus
    {
        Idle,
        Working,
        Attention,
    }

    /// <summary>
    /// Heuristic per-tab status for the vertical tab sidebar, fed by the pane events the
    /// window already receives. Pure logic — no Avalonia, no timers; the window supplies
    /// "now" and polls <see cref="Evaluate"/> (a 1s DispatcherTimer while vertical mode is
    /// active). Deliberately approximate: works with any agent CLI, zero cooperation needed.
    /// An explicit protocol (OSC / shell-integration marks) would plug in here later.
    /// </summary>
    internal sealed class TabStatusTracker
    {
        /// <summary>Output newer than this counts as "still working".</summary>
        internal static readonly TimeSpan WorkingWindow = TimeSpan.FromSeconds(2);

        /// <summary>A burst must span at least this long for its end to raise Attention.
        /// Filters one-shot output (a restored tab printing its prompt) from "the agent
        /// streamed for a while and stopped — probably finished or waiting for input".</summary>
        internal static readonly TimeSpan MinAttentionBurst = TimeSpan.FromSeconds(5);

        private DateTime _burstStartUtc;
        private DateTime _lastOutputUtc;
        private bool _inBurst;
        private bool _attention;

        public void NoteOutput(DateTime nowUtc)
        {
            if (!_inBurst || nowUtc - _lastOutputUtc > WorkingWindow)
            {
                _inBurst = true;
                _burstStartUtc = nowUtc;
            }

            _lastOutputUtc = nowUtc;
        }

        public void NoteBell() => _attention = true;

        /// <summary>Selecting the tab acknowledges it: Attention clears. The burst history
        /// survives, so a still-streaming agent keeps showing Working after selection.</summary>
        public void NoteSelected() => _attention = false;

        public TabTrackerStatus Evaluate(DateTime nowUtc, bool isSelected)
        {
            bool working = _inBurst && nowUtc - _lastOutputUtc <= WorkingWindow;

            if (_inBurst && !working)
            {
                // The burst just ended. Long burst + nobody watching => the user should look.
                if (!isSelected && _lastOutputUtc - _burstStartUtc >= MinAttentionBurst)
                {
                    _attention = true;
                }

                _inBurst = false;
            }

            if (isSelected)
            {
                _attention = false;
            }

            if (working) return TabTrackerStatus.Working;
            return _attention ? TabTrackerStatus.Attention : TabTrackerStatus.Idle;
        }
    }
}
