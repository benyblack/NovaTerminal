using System;
using System.Globalization;

namespace NovaTerminal.Shell
{
    /// <summary>
    /// Policy for "notify when a long command finishes" (agent-host A2 PR4,
    /// absorbing ROADMAP §5.2). Pure and stateless so the rules are unit-tested
    /// while the toast itself stays thin UI code.
    /// </summary>
    public static class LongCommandNotificationPolicy
    {
        /// <summary>A command must run at least this long to qualify.</summary>
        public const int ThresholdSeconds = 30;

        /// <summary>The mechanical gate, evaluated by the pane: is this command long enough?</summary>
        public static bool QualifiesAsLong(TimeSpan duration) => duration >= TimeSpan.FromSeconds(ThresholdSeconds);

        /// <summary>
        /// The policy gate, evaluated by the window: opt-in setting, and only
        /// when the user isn't already looking at the pane (different pane, or
        /// the window itself is in the background).
        /// </summary>
        public static bool ShouldNotify(bool enabled, bool windowActive, bool isCurrentPane)
            => enabled && !(windowActive && isCurrentPane);

        /// <summary>Compact human duration: "42s", "3m 5s", "1h 12m".</summary>
        public static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h {duration.Minutes}m");
            }
            if (duration.TotalMinutes >= 1)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes}m {duration.Seconds}s");
            }
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)duration.TotalSeconds)}s");
        }

        /// <summary>Toasts must stay one-line; longer commands are elided.</summary>
        public const int MaxCommandLength = 60;

        /// <summary>Toast body, e.g. "cargo build — exit 0 after 3m 5s (Bash)".</summary>
        public static string BuildMessage(string? commandText, int? exitCode, TimeSpan duration, string paneTitle)
        {
            // Flatten multi-line scripts and elide long commands so the toast
            // stays a compact single line.
            var command = string.IsNullOrWhiteSpace(commandText)
                ? "Command"
                : commandText.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace('\n', ' ')
                    .Trim();
            if (command.Length > MaxCommandLength)
            {
                command = command[..(MaxCommandLength - 1)] + "…";
            }

            var exit = exitCode.HasValue
                ? exitCode.Value.ToString(CultureInfo.InvariantCulture)
                : "?";
            return $"{command} — exit {exit} after {FormatDuration(duration)} ({paneTitle})";
        }
    }
}
