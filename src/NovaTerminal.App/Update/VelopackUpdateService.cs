using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace NovaTerminal.Update
{
    /// <summary>
    /// <see cref="IUpdateService"/> over Velopack, reading releases straight off this repo's
    /// GitHub releases.
    /// </summary>
    /// <remarks>
    /// This is the only file in the app that names a Velopack type besides the
    /// <c>VelopackApp.Build().Run()</c> hook in <c>Program.Main</c>.
    /// </remarks>
    public sealed class VelopackUpdateService : IUpdateService
    {
        public const string DefaultRepoUrl = "https://github.com/benyblack/NovaTerminal";

        private readonly Action<string> _log;
        private readonly UpdateManager _manager;
        private UpdateInfo? _downloaded;

        public VelopackUpdateService(string repoUrl, Action<string> log)
        {
            _log = log;

            // prerelease: false - a prerelease tag must never pull stable users onto an
            // unfinished build. accessToken: null - the repo is public, and an unauthenticated
            // check is subject to GitHub's anonymous rate limit, which a once-per-launch check
            // is nowhere near.
            //
            // The locator is passed explicitly rather than left to UpdateManager's default.
            // Its parameterless resolution reads VelopackLocator.Current, which only gets
            // populated by VelopackApp.Build().Run() in Program.Main - so it throws
            // InvalidOperationException in every host that never executes that hook: the
            // portable zip, the winget portable package, a plain dev run, and this class's
            // own unit tests (confirmed empirically - constructing UpdateManager with the
            // implicit locator crashes the constructor itself in a test host, before
            // IsSupported ever gets a chance to say no). Falling back to
            // VelopackLocator.CreateDefaultForPlatform keeps construction inert in exactly
            // those hosts, and IsInstalled still resolves to false there as required.
            var locator = VelopackLocator.IsCurrentSet
                ? VelopackLocator.Current
                : VelopackLocator.CreateDefaultForPlatform(null, null);
            _manager = new UpdateManager(new GithubSource(repoUrl, null, false), null, locator);
        }

        /// <summary>
        /// True only when Velopack installed this process. False for the portable zip, the winget
        /// portable package, and every dev run.
        /// </summary>
        public bool IsSupported => _manager.IsInstalled;

        public async Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct)
        {
            if (!IsSupported)
            {
                return new UpdateAvailability(false, null);
            }

            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update == null)
            {
                return new UpdateAvailability(false, null);
            }

            var version = update.TargetFullRelease.Version.ToString();
            _log($"Update available: {version}; downloading.");

            await _manager.DownloadUpdatesAsync(update, null, ct).ConfigureAwait(false);
            _downloaded = update;

            _log($"Update {version} downloaded; waiting for a restart.");
            return new UpdateAvailability(true, version);
        }

        public void ApplyAndRestart()
        {
            if (_downloaded == null)
            {
                return;
            }

            _log("Applying update and restarting.");
            _manager.ApplyUpdatesAndRestart(_downloaded);
        }
    }
}
