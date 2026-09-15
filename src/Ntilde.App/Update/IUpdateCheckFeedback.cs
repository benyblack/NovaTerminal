namespace NovaTerminal.Update
{
    /// <summary>
    /// Where one interactive update check reports its progress and its answer. Two surfaces share
    /// a single check pipeline (<see cref="UpdateCoordinator"/>): the main window's toasts (the
    /// palette's "Check for updates") and the About window's inline status area. Every call
    /// arrives on the UI thread - the check's continuation resumes there by design.
    /// </summary>
    public interface IUpdateCheckFeedback
    {
        /// <summary>The check has started and is running.</summary>
        void Checking();

        /// <summary>
        /// Another check (manual, or the deferred startup one) is already in flight, so no second
        /// download was started.
        /// </summary>
        void AlreadyRunning();

        /// <summary>
        /// The coordinator could not be constructed at all - a broken install, not a check
        /// result, so the wording differs from a <see cref="UpdateCheckOutcome.Failed"/> answer.
        /// </summary>
        void CoordinatorUnavailable();

        /// <summary>The check finished with this answer.</summary>
        /// <param name="outcome">How the check ended.</param>
        /// <param name="stagedVersion">
        /// The version waiting for a restart when <paramref name="outcome"/> is
        /// <see cref="UpdateCheckOutcome.UpdateReady"/>; the coordinator's
        /// <see cref="UpdateCoordinator.StagedVersion"/>, not necessarily this outcome's version.
        /// </param>
        void Outcome(UpdateCheckOutcome outcome, string? stagedVersion);
    }
}
