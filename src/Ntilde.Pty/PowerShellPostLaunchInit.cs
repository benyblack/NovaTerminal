using System.Text;

namespace NovaTerminal.Pty;

/// <summary>
/// The statements injected into a freshly-launched PowerShell to fix console encoding and print
/// the banner <c>-NoLogo</c> suppressed.
/// </summary>
/// <remarks>
/// These used to be written to <c>%TEMP%\nova_init_{guid}.ps1</c> and invoked with
/// <c>&amp; '&lt;path&gt;'</c>. Loading a .ps1 is gated by PowerShell's execution policy, and the
/// stock Windows client default is <c>Restricted</c> — so on any machine that had not loosened it,
/// every PowerShell pane without shell integration opened with a red UnauthorizedAccess error.
/// Reported from a real install; the same root cause as the Command Assist bootstrap.
///
/// Sending the statements as input loads nothing from disk, so no policy applies. It also retires
/// the <c>%TEMP%</c> leak of #107 outright — there is no file to leak, no self-delete line, and no
/// dispose-time cleanup to get right.
/// </remarks>
public static class PowerShellPostLaunchInit
{
    /// <summary>
    /// One submission, newline-terminated. Single-line on purpose: this is typed into a live
    /// shell, so an embedded newline would submit a partial statement and strand the rest.
    /// </summary>
    public static string BuildInjection()
    {
        var sb = new StringBuilder();

        // 1. Set encoding cleanly — the reason this injection exists.
        sb.Append("$OutputEncoding = [System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ");
        // 2. Wipe the command text we just typed into the user's shell.
        sb.Append("Clear-Host; ");
        // 3. Print the banner -NoLogo suppressed.
        sb.Append("Write-Host 'Windows PowerShell'; ");
        sb.Append("Write-Host 'Copyright (C) Microsoft Corporation. All rights reserved.'; ");
        sb.Append("Write-Host ''; ");
        sb.Append("Write-Host 'Install the latest PowerShell for new features and improvements! https://aka.ms/PSWindows'; ");
        sb.Append("Write-Host ''");
        sb.Append('\r');

        return sb.ToString();
    }
}
