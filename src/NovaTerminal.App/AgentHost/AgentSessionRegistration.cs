using System;
using NovaTerminal.VT;

namespace NovaTerminal.AgentHost
{
    /// <summary>
    /// One live pane's entry in <see cref="AgentSessionRegistry"/>.
    ///
    /// Everything that can change over a pane's lifetime (title, profile,
    /// active state) is captured as a provider delegate rather than a value,
    /// so the registry never holds stale copies and never needs update calls
    /// from the pane. Providers are invoked on whatever thread queries the
    /// registry and must therefore be cheap and non-throwing for a live pane.
    /// </summary>
    public sealed class AgentSessionRegistration
    {
        /// <summary>Stable pane identity; re-keyed via <see cref="AgentSessionRegistry.Rekey"/> on session restore.</summary>
        public required Guid PaneId { get; set; }

        /// <summary>Owning tab; associated lazily by MainWindow via <see cref="AgentSessionRegistry.SetTabAssociation"/>.</summary>
        public Guid TabId { get; internal set; }

        /// <summary>The pane's VT buffer. Reads must take <see cref="TerminalBuffer.Lock"/> (endpoint milestone A1/PR3).</summary>
        public required TerminalBuffer Buffer { get; init; }

        /// <summary>Current display title (OSC title, or profile + cwd fallback).</summary>
        public required Func<string> TitleProvider { get; init; }

        /// <summary>Current profile name ("Terminal" when the pane has no profile).</summary>
        public required Func<string> ProfileNameProvider { get; init; }

        /// <summary>"ssh" or "local".</summary>
        public required Func<string> KindProvider { get; init; }

        /// <summary>True when this pane is the active pane of its tab.</summary>
        public required Func<bool> IsActiveProvider { get; init; }
    }
}
