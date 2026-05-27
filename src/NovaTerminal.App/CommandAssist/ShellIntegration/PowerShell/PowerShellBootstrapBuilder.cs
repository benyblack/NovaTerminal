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
        builder.Append("$script:NovaAcceptedCommandText = $null").Append(nl);
        builder.Append("$script:NovaOriginalPrompt = $null").Append(nl);
        builder.Append("$script:NovaPromptCommand = Get-Command prompt -ErrorAction SilentlyContinue").Append(nl);
        builder.Append("if ($script:NovaPromptCommand -and $script:NovaPromptCommand.ScriptBlock) {").Append(nl);
        builder.Append("    $script:NovaOriginalPrompt = $script:NovaPromptCommand.ScriptBlock").Append(nl);
        builder.Append("}").Append(nl);
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
        // Emits OSC 133;D only when the previous prompt cycle saw a real
        // accepted command, then clears tracked state so the next prompt
        // cycle starts clean even if no command was entered.
        builder.Append("function Write-NovaCompletion([bool]$lastSuccess, $lastExitCode) {").Append(nl);
        builder.Append("    if ($script:NovaCommandStart -eq $null) { return }").Append(nl);
        builder.Append("    $durationMs = [math]::Round((([DateTimeOffset]::UtcNow) - $script:NovaCommandStart).TotalMilliseconds)").Append(nl);
        // PowerShell cmdlets set $? to $false on failure but do NOT touch
        // $LASTEXITCODE, so a failing cmdlet after a prior successful
        // external command would otherwise be reported with the stale
        // $LASTEXITCODE=0 (i.e. as a SUCCESS). Only trust $LASTEXITCODE
        // when it is itself nonzero; treat any other failure as exit 1
        // so Command Assist's error-insight surfaces see a real failure.
        builder.Append("    $exitCode = if ($lastSuccess) { 0 } elseif ($lastExitCode -ne $null -and $lastExitCode -ne 0) { $lastExitCode } else { 1 }").Append(nl);
        builder.Append("    Write-NovaSequence \"]133;D;$exitCode;$durationMs\"").Append(nl);
        builder.Append("    $script:NovaCommandStart = $null").Append(nl);
        builder.Append("    $script:NovaAcceptedCommandText = $null").Append(nl);
        builder.Append("}").Append(nl);
        builder.Append(nl);
        builder.Append("function Global:prompt {").Append(nl);
        // Snapshot $? / $LASTEXITCODE on the first line so subsequent statements
        // don't clobber them before Write-NovaCompletion reads the values.
        builder.Append("    $lastSuccess = $?").Append(nl);
        builder.Append("    $lastExit = $global:LASTEXITCODE").Append(nl);
        builder.Append("    Write-NovaCompletion $lastSuccess $lastExit").Append(nl);
        builder.Append("    Write-NovaPromptReady").Append(nl);
        builder.Append("    if ($script:NovaOriginalPrompt -ne $null) {").Append(nl);
        builder.Append("        return & $script:NovaOriginalPrompt").Append(nl);
        builder.Append("    }").Append(nl);
        builder.Append("    return [string]::Concat('PS ', (Get-Location), '> ')").Append(nl);
        builder.Append("}").Append(nl);
        builder.Append(nl);
        // Capture the accepted command text at the shell boundary by wrapping
        // PSReadLine's Enter chord. Emits OSC 133;C;<base64> via direct console
        // write (not Write-NovaSequence) so it is unambiguously the only path
        // that produces the C marker.
        //
        // PSReadLine is not loaded in every PowerShell environment (minimal
        // hosts, server-core, some constrained-language modes); calling
        // Set-PSReadLineKeyHandler without it under $ErrorActionPreference =
        // 'Stop' would terminate the bootstrap and prevent the shell from
        // starting. Probe for the cmdlet first and skip silently if absent.
        builder.Append("if (Get-Command Set-PSReadLineKeyHandler -ErrorAction SilentlyContinue) {").Append(nl);
        builder.Append("    Set-PSReadLineKeyHandler -Chord 'Enter' -ScriptBlock {").Append(nl);
        builder.Append("        $line = $null").Append(nl);
        builder.Append("        $cursor = $null").Append(nl);
        builder.Append("        [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)").Append(nl);
        builder.Append("        if (-not [string]::IsNullOrWhiteSpace($line)) {").Append(nl);
        builder.Append("            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line))").Append(nl);
        builder.Append("            [Console]::Out.Write(\"$([char]27)]133;C;$encoded$([char]7)\")").Append(nl);
        builder.Append("            $script:NovaAcceptedCommandText = $line").Append(nl);
        builder.Append("            $script:NovaCommandStart = [DateTimeOffset]::UtcNow").Append(nl);
        builder.Append("        }").Append(nl);
        builder.Append("        [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()").Append(nl);
        builder.Append("    }").Append(nl);
        builder.Append("}").Append(nl);
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
