namespace NovaTerminal.Update
{
    /// <summary>
    /// The words every surface that answers "is there an update?" uses. The palette path shows
    /// them in toasts, the About window shows them inline; sharing one source keeps the two from
    /// drifting. UI-free by design so the mapping is unit-testable in the gating CI loop.
    /// </summary>
    public static class UpdateCheckMessages
    {
        public static string OutcomeTitle(UpdateCheckOutcome outcome) => outcome switch
        {
            UpdateCheckOutcome.UpdateReady => "Update ready",
            UpdateCheckOutcome.UpToDate => "Up to date",
            UpdateCheckOutcome.Unsupported => "Updates unavailable",
            UpdateCheckOutcome.Failed => "Update check failed",
            UpdateCheckOutcome.Disabled => "Automatic updates are off",
            _ => "Update check",
        };

        public static string OutcomeMessage(UpdateCheckOutcome outcome, string? stagedVersion) => outcome switch
        {
            UpdateCheckOutcome.UpdateReady =>
                $"NovaTerminal {stagedVersion ?? "(unknown version)"} is downloaded and will be applied when you restart.",
            UpdateCheckOutcome.UpToDate => "You are running the newest version.",
            UpdateCheckOutcome.Unsupported =>
                "This build was not installed by the NovaTerminal installer, so it cannot update itself. " +
                "Download the installer from the releases page to get automatic updates.",
            UpdateCheckOutcome.Failed => "Could not reach GitHub. See the debug log for details.",

            // Unreachable from a manual check - RunManualCheckAsync deliberately ignores the
            // automatic-checks setting - but kept so the mapping is total over the enum.
            UpdateCheckOutcome.Disabled => "Automatic update checks are switched off in settings.",
            _ => "The update check ended without an answer.",
        };

        /// <summary>Whether this answer comes with a restart affordance.</summary>
        public static bool OutcomeOffersRestart(UpdateCheckOutcome outcome)
            => outcome == UpdateCheckOutcome.UpdateReady;

        public const string AlreadyRunningTitle = "Checking for updates";
        public const string AlreadyRunningMessage =
            "A check is already running in the background. If it finds a new version, you'll get a notification.";

        public const string CoordinatorUnavailableTitle = "Update check failed";
        public const string CoordinatorUnavailableMessage =
            "Could not check for updates. See the debug log for details.";
    }
}
