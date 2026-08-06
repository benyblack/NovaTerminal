using System;
using System.Globalization;

namespace NovaTerminal.CommandAssist.Domain;

/// <summary>
/// Renders a history timestamp the way a person reads one: "2m ago", "yesterday".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists (UX-polish round, owner dogfooding).</strong> The popup's metadata line
/// used to read <c>Used 2026-08-04 15:23</c>. An absolute wall-clock stamp is the wrong unit for the
/// question the user is actually asking of a <c>Ctrl+R</c> list - "is this the thing I just ran?" -
/// and answering it required the reader to subtract two timestamps in their head, one of which they
/// had to guess. It is also the only part of the row that could have explained the ordering, and it
/// declined to.
/// </para>
/// <para>
/// The buckets are deliberately coarse. This is a caption on a row that is already sorted by the
/// same quantity, so precision past "which of these is newer" is noise; what it has to do is make
/// the sort legible at a glance. Past a week it falls back to a date, because "23d ago" stops being
/// something anyone can place and a date is what you would look for.
/// </para>
/// <para>
/// <paramref name="now"/> is a parameter rather than a read of the clock so the buckets are
/// testable without waiting for time to pass.
/// </para>
/// </remarks>
public static class AssistRelativeTime
{
    /// <summary>
    /// How recent an entry has to be to earn the "Recent" badge.
    /// </summary>
    /// <remarks>
    /// Sized to "this sitting, more or less". The badge's job is to confirm the top of the list is
    /// the top for a reason the user can check against their own memory, so it has to cover the
    /// commands they remember running without also covering this morning's.
    /// </remarks>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(15);

    /// <summary>Whether <paramref name="value"/> is inside <see cref="RecentWindow"/> of <paramref name="now"/>.</summary>
    public static bool IsRecent(DateTimeOffset value, DateTimeOffset now)
    {
        TimeSpan age = now - value;
        return age < RecentWindow;
    }

    /// <summary>
    /// Formats <paramref name="value"/> relative to <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// A future timestamp - a clock skew between this box and an SSH host, which is common enough to
    /// be worth handling - reads as "just now" rather than as a negative age. The entry is real and
    /// the skew is not the user's problem.
    /// </remarks>
    public static string Format(DateTimeOffset value, DateTimeOffset now)
    {
        TimeSpan age = now - value;
        if (age < TimeSpan.Zero)
        {
            return "just now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromHours(24))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        if (age < TimeSpan.FromHours(48))
        {
            return "yesterday";
        }

        if (age < TimeSpan.FromDays(7))
        {
            return $"{(int)age.TotalDays}d ago";
        }

        return value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
