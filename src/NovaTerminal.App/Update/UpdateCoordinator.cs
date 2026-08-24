using System;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Update
{
    /// <summary>Why a check ended the way it did. Returned for tests and logging, not for display.</summary>
    public enum UpdateCheckOutcome
    {
        /// <summary>Not a Velopack install - portable zip, winget, or a dev run.</summary>
        Unsupported,

        /// <summary>Automatic checks are switched off in settings.</summary>
        Disabled,

        /// <summary>Checked successfully; already on the newest release.</summary>
        UpToDate,

        /// <summary>The check or download threw. Logged, never surfaced by an automatic check.</summary>
        Failed,

        /// <summary>A newer release is downloaded and waiting for a restart.</summary>
        UpdateReady,
    }

    /// <summary>
    /// Decides when to check for updates and what the rest of the app is told about the result.
    /// UI-free and Velopack-free by design - see <see cref="IUpdateService"/>.
    /// </summary>
    public sealed class UpdateCoordinator
    {
        private readonly IUpdateService _service;
        private readonly Func<bool> _automaticChecksEnabled;
        private readonly Action<string> _onUpdateReady;
        private readonly Action<string> _log;

        public UpdateCoordinator(
            IUpdateService service,
            Func<bool> automaticChecksEnabled,
            Action<string> onUpdateReady,
            Action<string> log)
        {
            _service = service;
            _automaticChecksEnabled = automaticChecksEnabled;
            _onUpdateReady = onUpdateReady;
            _log = log;
        }

        /// <summary>True once a downloaded update is waiting for a restart.</summary>
        public bool IsUpdateStaged => StagedVersion != null;

        /// <summary>The staged version, or null when nothing is staged.</summary>
        public string? StagedVersion { get; private set; }

        /// <summary>The startup check. Honours the settings toggle and never surfaces a failure.</summary>
        public Task<UpdateCheckOutcome> RunAutomaticCheckAsync(CancellationToken ct = default)
        {
            if (!_automaticChecksEnabled())
            {
                return Task.FromResult(UpdateCheckOutcome.Disabled);
            }

            return RunCheckAsync(ct);
        }

        /// <summary>
        /// The user asked. Deliberately ignores the automatic-checks toggle: that setting governs
        /// background traffic, not whether the user may ask a direct question.
        /// </summary>
        public Task<UpdateCheckOutcome> RunManualCheckAsync(CancellationToken ct = default)
            => RunCheckAsync(ct);

        private async Task<UpdateCheckOutcome> RunCheckAsync(CancellationToken ct)
        {
            if (!_service.IsSupported)
            {
                return UpdateCheckOutcome.Unsupported;
            }

            UpdateAvailability availability;
            try
            {
                availability = await _service.CheckAndDownloadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown during a check is not a failure worth logging.
                return UpdateCheckOutcome.Failed;
            }
            catch (Exception ex)
            {
                // An unreachable GitHub, a rate limit or a malformed feed must cost the user
                // nothing. The caller decides whether to tell them (a manual check does; the
                // startup check does not).
                _log("Update check failed: " + ex);
                return UpdateCheckOutcome.Failed;
            }

            if (!availability.HasUpdate)
            {
                return UpdateCheckOutcome.UpToDate;
            }

            if (availability.Version == null)
            {
                // IUpdateService's contract (see UpdateAvailability's doc comment) guarantees a
                // non-null Version whenever HasUpdate is true. IsUpdateStaged is defined as
                // StagedVersion != null, so silently coalescing to string.Empty here would stage
                // the empty string, report the update as ready, and announce it with a blank
                // version number. Treat the violation as a failed check instead: nothing is
                // staged, nothing is announced.
                _log("Update check violated its contract: HasUpdate was true but Version was null.");
                return UpdateCheckOutcome.Failed;
            }

            var version = availability.Version;

            // Announce a given staged version once. Without this, a manual check after the
            // startup check re-raises the toast the user just dismissed.
            if (StagedVersion != version)
            {
                StagedVersion = version;
                _onUpdateReady(version);
            }

            return UpdateCheckOutcome.UpdateReady;
        }

        /// <summary>Restarts into the staged update. No-op when nothing is staged.</summary>
        public void ApplyStagedUpdate()
        {
            if (!IsUpdateStaged)
            {
                return;
            }

            _service.ApplyAndRestart();
        }
    }
}
