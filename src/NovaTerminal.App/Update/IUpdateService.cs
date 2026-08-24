using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Update
{
    /// <summary>
    /// What an update host can do, expressed without reference to Velopack, Avalonia or the
    /// network. <see cref="UpdateCoordinator"/> holds the policy; this holds the mechanism.
    /// </summary>
    /// <remarks>
    /// The seam exists for two reasons. It keeps every rule in <see cref="UpdateCoordinator"/>
    /// testable on a machine with no Velopack install and no network - which includes the ubuntu
    /// leg of CI, where <c>App.Tests</c> also runs. And it confines the Velopack API surface to a
    /// single implementation, which is what makes the design's AOT fallback (drive updates through
    /// the bundled Update.exe instead of the in-process SDK) a one-file change.
    /// </remarks>
    public interface IUpdateService
    {
        /// <summary>
        /// False when this process was not installed by Velopack - a portable zip, a winget
        /// portable install, or a dev run. Those must never see update UI or errors.
        /// </summary>
        bool IsSupported { get; }

        /// <summary>
        /// Checks for a newer release and, if there is one, downloads it. May throw; the caller
        /// is responsible for deciding that a failed update check is not the user's problem.
        /// </summary>
        Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct);

        /// <summary>Applies the downloaded update and restarts the app.</summary>
        void ApplyAndRestart();
    }

    /// <param name="HasUpdate">True when a newer release was found and downloaded.</param>
    /// <param name="Version">The new version, for display. Null when <paramref name="HasUpdate"/> is false.</param>
    public readonly record struct UpdateAvailability(bool HasUpdate, string? Version);
}
