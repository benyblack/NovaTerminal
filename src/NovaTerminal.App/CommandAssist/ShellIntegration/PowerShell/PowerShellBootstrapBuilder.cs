using System;
using System.IO;
using System.Text;

namespace NovaTerminal.CommandAssist.ShellIntegration.PowerShell;

public static class PowerShellBootstrapBuilder
{
    public static string BuildScript()
    {
        const string nl = "\n";
        var builder = new StringBuilder();
        builder.Append("$ErrorActionPreference = 'Stop'").Append(nl);
        builder.Append("$esc = [char]27").Append(nl);
        builder.Append("$bel = [char]7").Append(nl);
        builder.Append("$script:NovaCommandStart = $null").Append(nl);
        builder.Append(nl);
        builder.Append("function Write-NovaSequence([string]$sequence) {").Append(nl);
        builder.Append("    [Console]::Out.Write(\"$esc$sequence$bel\")").Append(nl);
        builder.Append("}").Append(nl);
        builder.Append(nl);
        builder.Append("function Write-NovaPwd() {").Append(nl);
        builder.Append("    $cwd = [Uri]::EscapeUriString((Get-Location).Path)").Append(nl);
        builder.Append("    Write-NovaSequence \"]7;file://$env:COMPUTERNAME/$cwd\"").Append(nl);
        builder.Append("}").Append(nl);
        builder.Append(nl);
        builder.Append("function Write-NovaPromptReady() {").Append(nl);
        builder.Append("    Write-NovaPwd").Append(nl);
        builder.Append("    Write-NovaSequence ']133;A'").Append(nl);
        builder.Append("}").Append(nl);
        builder.Append(nl);
        builder.Append("function Global:prompt {").Append(nl);
        builder.Append("    Write-NovaPromptReady").Append(nl);
        builder.Append("    'PS ' + (Get-Location) + '> '").Append(nl);
        builder.Append("}").Append(nl);
        builder.Append(nl);
        builder.Append("Set-PSReadLineOption -AddToHistoryHandler {").Append(nl);
        builder.Append("    param($line)").Append(nl);
        builder.Append("    if ([string]::IsNullOrWhiteSpace($line)) { return $true }").Append(nl);
        builder.Append("    $script:NovaCommandStart = [DateTimeOffset]::UtcNow").Append(nl);
        builder.Append("    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line))").Append(nl);
        builder.Append("    Write-NovaSequence \"]133;C;$encoded\"").Append(nl);
        builder.Append("    Write-NovaSequence ']133;B'").Append(nl);
        builder.Append("    return $true").Append(nl);
        builder.Append("}").Append(nl);
        builder.Append(nl);
        builder.Append("Register-EngineEvent PowerShell.OnIdle -SupportEvent -Action {").Append(nl);
        builder.Append("    if ($global:LASTEXITCODE -ne $null -or $?) {").Append(nl);
        builder.Append("        $durationMs = $null").Append(nl);
        builder.Append("        if ($script:NovaCommandStart -ne $null) {").Append(nl);
        builder.Append("            $durationMs = [math]::Round((([DateTimeOffset]::UtcNow) - $script:NovaCommandStart).TotalMilliseconds)").Append(nl);
        builder.Append("        }").Append(nl);
        builder.Append("        $exitCode = if ($?) { 0 } elseif ($global:LASTEXITCODE -ne $null) { $global:LASTEXITCODE } else { 1 }").Append(nl);
        builder.Append("        if ($durationMs -ne $null) {").Append(nl);
        builder.Append("            Write-NovaSequence \"]133;D;$exitCode;$durationMs\"").Append(nl);
        builder.Append("        } else {").Append(nl);
        builder.Append("            Write-NovaSequence \"]133;D;$exitCode\"").Append(nl);
        builder.Append("        }").Append(nl);
        builder.Append("        $script:NovaCommandStart = $null").Append(nl);
        builder.Append("    }").Append(nl);
        builder.Append("} | Out-Null").Append(nl);
        builder.Append(nl);
        builder.Append("Write-NovaPromptReady").Append(nl);
        return builder.ToString();
    }

    public static string WriteScript(string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        string path = Path.Combine(targetDirectory, "command-assist-bootstrap.ps1");
        File.WriteAllText(path, BuildScript(), Encoding.UTF8);
        return path;
    }
}
