namespace NovaTerminal;

/// <summary>
/// A specific section within <see cref="SettingsWindow"/> that a caller can ask the window to bring
/// into view once it opens, in addition to selecting a tab.
/// </summary>
/// <remarks>
/// Added for PR #342 Codex round 6: the title bar's right-click "Customize Title Bar..." entry
/// point previously opened Settings via <c>OpenSettings(0)</c>, landing on the Appearance tab at its
/// default scroll position - which is the theme editor and preview, well above the TITLE BAR
/// section further down the same tab. That defeated the entry point's entire purpose (the section
/// is not independently discoverable; this menu item exists solely to reach it), hence a small,
/// general "which section" input rather than a magic string or a title-bar-only constructor
/// overload.
/// </remarks>
public enum SettingsSection
{
    /// <summary>No section targeting - the tab opens at its default scroll position.</summary>
    None,

    /// <summary>The "TITLE BAR" section on the Appearance tab.</summary>
    TitleBar,
}
