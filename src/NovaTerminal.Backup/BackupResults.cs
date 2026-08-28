using System;
using System.Collections.Generic;

namespace NovaTerminal.Backup;

/// <summary>Why a backup operation failed. <see cref="None"/> means it succeeded.</summary>
public enum BackupFailureKind
{
    None,
    NotFound,
    NotABackup,
    CorruptArchive,
    UnsupportedSchemaVersion,
    MissingCategoryContent,
    WriteFailed,

    /// <summary>
    /// The path exists but the caller's ACL denies reading it — distinct from
    /// <see cref="NotABackup"/> (a readable file that isn't a valid bundle) because the two lead
    /// the user somewhere different: fix permissions, versus pick a different file.
    /// </summary>
    AccessDenied,

    /// <summary>
    /// A defence-in-depth catch converted an escaping exception of a type none of this codebase's
    /// specific catches recognized. Deliberately distinct from every other kind here so it never
    /// reads as a diagnosed, known cause — see the backstop catches in
    /// <see cref="BackupService"/> and <see cref="BundleReader"/> for what this covers.
    /// </summary>
    Unexpected
}

/// <summary>Result of an operation with no return value.</summary>
public sealed record BackupOutcome(bool Success, BackupFailureKind Failure, string Message)
{
    public static BackupOutcome Ok(string message = "") =>
        new(true, BackupFailureKind.None, message);

    public static BackupOutcome Fail(BackupFailureKind kind, string message) =>
        new(false, kind, message);
}

/// <summary>What a bundle contains, without extracting it.</summary>
public sealed record BundleInspection(
    BackupManifest Manifest,
    IReadOnlyDictionary<BackupCategory, int> ItemCounts);

/// <summary>Result of reading a bundle's manifest.</summary>
public sealed record InspectOutcome(
    bool Success,
    BackupFailureKind Failure,
    string Message,
    BundleInspection? Inspection)
{
    public static InspectOutcome Ok(BundleInspection inspection) =>
        new(true, BackupFailureKind.None, string.Empty, inspection);

    public static InspectOutcome Fail(BackupFailureKind kind, string message) =>
        new(false, kind, message, null);
}

/// <summary>A snapshot on disk. <paramref name="Id"/> is the file-name stem.</summary>
/// <param name="ContentHash">
/// The first 16 hex characters of the SHA-256 over the backed-up content (the live tree, not
/// the zip), used for <see cref="BackupService.Snapshot"/>'s auto-dedupe check and embedded in
/// <paramref name="Id"/>. This is a truncated prefix, not a full digest — it is not collision-
/// resistant enough to use as an integrity check, only as a dedupe key for one snapshot at a
/// time.
/// </param>
public sealed record SnapshotInfo(
    string Id,
    SnapshotReason Reason,
    DateTimeOffset CreatedUtc,
    long SizeBytes,
    string ContentHash,
    string FilePath);
