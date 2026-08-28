using NovaTerminal.Shell;

namespace NovaTerminal.Shots;

public interface IScenario
{
    ShotSpec Spec { get; }

    /// <summary>
    /// Settings this scenario needs seeded before its window is constructed, or null for the
    /// defaults. It must be applied before construction rather than after: MainWindow reads
    /// TerminalSettings in its constructor, so a theme or tab-orientation change made later
    /// only half-applies and produces an image that looks almost right.
    /// </summary>
    Action<TerminalSettings>? Settings => null;

    /// <summary>
    /// Process-environment setup this scenario needs before its window is constructed, or null
    /// if none. Separate from <see cref="Settings"/> because it answers a different question -
    /// not "what should TerminalSettings say" but "what should a spawned shell inherit" - and
    /// the two run at genuinely different moments even though both are "before the window."
    /// MainWindow restores (or creates) its startup tab during construction/Show(), spawning a
    /// PTY before <see cref="RunAsync"/> ever gets control; <see cref="ShotContext.OpenTab"/>'s
    /// first call then *adopts* that already-running shell rather than spawning a new one (see
    /// its remarks). A variable set from inside <c>RunAsync</c> - after that adoption - is set
    /// too late for the shell that every scenario's first pane actually is: the process
    /// environment it inherited at spawn is fixed for its lifetime. This runs early enough to
    /// still land before that spawn.
    /// </summary>
    Action? PrepareEnvironment => null;

    Task RunAsync(ShotContext context);
}
