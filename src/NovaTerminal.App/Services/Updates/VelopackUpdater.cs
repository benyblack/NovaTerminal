using System.Threading.Tasks;
using NovaTerminal.Platform.Updates;
using Velopack;
using Velopack.Sources;

namespace NovaTerminal.Services.Updates;

/// <summary>
/// The real <see cref="IVelopackUpdater"/>, backed by Velopack's <see cref="UpdateManager"/>
/// against this repository's public GitHub Releases.
/// </summary>
/// <remarks>
/// Deliberately thin, and deliberately untested by unit tests: every line here needs a network,
/// a Velopack-installed application, or both. What is worth testing lives behind the interface
/// in <see cref="UpdateService"/>. This half is covered by the packaging spike and the manual
/// N-to-N+1 acceptance run.
/// </remarks>
public sealed class VelopackUpdater : IVelopackUpdater
{
    private const string RepoUrl = "https://github.com/benyblack/NovaTerminal";

    private readonly UpdateManager _manager =
        new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    private UpdateInfo? _pending;

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<string?> CheckAndStageAsync()
    {
        _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (_pending is null)
        {
            return null;
        }

        // Staged, not applied: the download lands on disk now so that "restart to update" is
        // instant later, but nothing is swapped until the user asks for it.
        await _manager.DownloadUpdatesAsync(_pending).ConfigureAwait(false);
        return _pending.TargetFullRelease.Version.ToString();
    }

    public Task ApplyAndRestartAsync()
    {
        if (_pending is not null)
        {
            // Takes the asset, not the UpdateInfo. Does not return -- it relaunches the app.
            _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
        }

        return Task.CompletedTask;
    }
}
