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

    Task RunAsync(ShotContext context);
}
