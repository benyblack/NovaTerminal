using System;
using System.Threading.Tasks;

namespace NovaTerminal.Platform.Updates;

/// <summary>
/// Orchestrates the startup update check and apply-on-restart. Pure logic over
/// <see cref="IVelopackUpdater"/>; <see cref="CheckAsync"/> never throws.
/// </summary>
public sealed class UpdateService
{
    private readonly IVelopackUpdater _updater;
    private readonly Action<string> _log;

    /// <param name="updater">The update backend. See <see cref="IVelopackUpdater"/>.</param>
    /// <param name="log">
    /// Where swallowed failures go. Injected rather than reached for statically: Platform does
    /// not reference VT (where TerminalLogger lives), and taking it as a parameter turns
    /// "a failed check is logged, not silently dropped" into something a test can assert.
    /// </param>
    public UpdateService(IVelopackUpdater updater, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(log);
        _updater = updater;
        _log = log;
    }

    /// <summary>True once a newer release has been staged and is ready to apply on restart.</summary>
    public bool UpdateReady { get; private set; }

    /// <summary>The staged target version, or null when none.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>
    /// Raised when <see cref="UpdateReady"/> transitions to true. Raised on whichever thread
    /// completed the check, which is not the UI thread -- subscribers that touch UI must marshal.
    /// </summary>
    public event Action? UpdateReadyChanged;

    /// <summary>
    /// Fire-and-forget startup check. Never throws. A no-op when not running as a
    /// Velopack-installed build, which covers the portable zip and every dev run.
    /// </summary>
    public async Task CheckAsync()
    {
        if (!_updater.IsInstalled)
        {
            return;
        }

        try
        {
            string? version = await _updater.CheckAndStageAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(version))
            {
                return;
            }

            AvailableVersion = version;
            UpdateReady = true;
            UpdateReadyChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, malformed feed -- none of it is worth failing a launch over.
            _log("UpdateService.CheckAsync failed: " + ex);
        }
    }

    /// <summary>
    /// Applies the staged update and restarts. A no-op when no update is ready. Never throws,
    /// for the same reason <see cref="CheckAsync"/> does not: the UI calls this from an
    /// <c>async void</c> click handler, where an escaping exception reaches the dispatcher
    /// unhandled and can take down a window full of live terminal sessions. Losing a user's
    /// panes because an update could not be applied is a far worse outcome than not updating.
    /// </summary>
    /// <remarks>
    /// <see cref="UpdateReady"/> is deliberately left set on failure. The package is still
    /// staged on disk, so a transient cause -- a locked file, a running child process -- should
    /// not hide an update that will apply cleanly on the next attempt.
    /// </remarks>
    public async Task ApplyAsync()
    {
        if (!UpdateReady)
        {
            return;
        }

        try
        {
            await _updater.ApplyAndRestartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("UpdateService.ApplyAsync failed: " + ex);
        }
    }
}
