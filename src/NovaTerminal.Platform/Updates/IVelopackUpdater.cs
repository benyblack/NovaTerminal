using System.Threading.Tasks;

namespace NovaTerminal.Platform.Updates;

/// <summary>
/// Narrow seam over Velopack's <c>UpdateManager</c>, so <see cref="UpdateService"/> can be unit
/// tested without a network, an installed application, or a reference to Velopack itself.
/// </summary>
/// <remarks>
/// This interface lives in Platform rather than the App on purpose. Platform.Tests runs in the
/// gating test lane of both ci.yml and release.yml; App.Tests runs in neither (it is the
/// non-blocking headless Avalonia suite, see #81). Update logic that only executes during a
/// release has no business being the one thing the release gate cannot see.
/// </remarks>
public interface IVelopackUpdater
{
    /// <summary>True only when running as a Velopack-installed build.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Checks the feed and stages any newer release.
    /// Returns the target version string when an update is staged, or null when up to date.
    /// </summary>
    Task<string?> CheckAndStageAsync();

    /// <summary>Applies the staged update and restarts the app. Does not return on success.</summary>
    Task ApplyAndRestartAsync();
}
