namespace NovaTerminal.Shell.Backup;

/// <summary>
/// A unit of configuration a bundle can carry. The manifest stores these as
/// lowercase names; see <see cref="BackupCatalog"/> for the path mapping.
/// </summary>
public enum BackupCategory
{
    Settings,
    Themes,
    Connections,
    Workspaces,
    Policy,
    Snippets
}

/// <summary>Why a snapshot was written. Encoded as the snapshot file-name prefix.</summary>
public enum SnapshotReason
{
    /// <summary>Written by <c>SnapshotScheduler</c> after tracked files changed.</summary>
    Auto,

    /// <summary>Forced immediately before an import.</summary>
    PreImport,

    /// <summary>Forced immediately before a restore.</summary>
    PreRestore
}

/// <summary>How an import reconciles bundle content with what is already on disk.</summary>
public enum ImportMode
{
    /// <summary>Bundle wins per item; local items with no counterpart survive.</summary>
    Merge,

    /// <summary>For each included category the bundle becomes the truth.</summary>
    Replace
}
