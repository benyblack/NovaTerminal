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
        // Capture the user's `prompt` exactly once. Re-running the bootstrap in a
        // session it has already initialized (a second -File pass, a user dot-source,
        // `exec pwsh` into the same profile) would otherwise capture OUR wrapper as
        // "the original" and the wrapper would call itself forever. Two guards, because
        // a second -File pass gets a fresh script scope where the first guard alone
        // cannot see the earlier capture:
        //   1. don't overwrite an already-captured original in this scope;
        //   2. never capture a `prompt` whose body carries our wrapper sentinel.
        // Worst case (fresh scope + already-wrapped prompt) the bootstrap degrades to
        // the synthesized default prompt; it never recurses.
        builder.Append("if (-not (Get-Variable -Name 'NovaOriginalPrompt' -Scope Script -ErrorAction SilentlyContinue)) {").Append(nl);
        builder.Append("    $script:NovaOriginalPrompt = $null").Append(nl);
        builder.Append("}").Append(nl);
        builder.Append("$novaPromptCommand = Get-Command prompt -ErrorAction SilentlyContinue").Append(nl);
        builder.Append("if ($null -eq $script:NovaOriginalPrompt -and").Append(nl);
        builder.Append("    $novaPromptCommand -and $novaPromptCommand.ScriptBlock -and").Append(nl);
        builder.Append("    $novaPromptCommand.ScriptBlock.ToString() -notlike '*__nova_prompt_wrapper*') {").Append(nl);
        builder.Append("    $script:NovaOriginalPrompt = $novaPromptCommand.ScriptBlock").Append(nl);
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
        // Sentinel the capture guard above greps for. Must stay inside the function
        // body so it survives into ScriptBlock.ToString().
        builder.Append("    # __nova_prompt_wrapper").Append(nl);
        // Snapshot $? / $LASTEXITCODE on the first line so subsequent statements
        // don't clobber them before Write-NovaCompletion reads the values.
        builder.Append("    $lastSuccess = $?").Append(nl);
        builder.Append("    $lastExit = $global:LASTEXITCODE").Append(nl);
        builder.Append("    Write-NovaCompletion $lastSuccess $lastExit").Append(nl);
        builder.Append("    Write-NovaPromptReady").Append(nl);
        // OSC 133;B marks the END of the prompt -- the cell where the user's
        // input begins. Anything this function *writes* lands before the
        // prompt text (the host prints the returned string afterwards), so B
        // must be appended to the returned string instead. -join '' flattens
        // the rare prompt that emits several objects; a single-string prompt
        // (the overwhelming case, including oh-my-posh/starship) round-trips
        // unchanged.
        //
        // Deliberate divergence: for a multi-object prompt the host itself
        // stringifies with the $OFS separator (a space by default), so it would
        // render "a b c" where we render "abc". Joining with '' is the choice
        // that keeps the far more common single-string and
        // array-of-adjacent-fragments prompts byte-identical; the alternative
        // would insert phantom spaces into every prompt built by emitting
        // fragments. Prompts that genuinely want separators emit them.
        builder.Append("    $novaPromptText = if ($script:NovaOriginalPrompt -ne $null) {").Append(nl);
        builder.Append("        (& $script:NovaOriginalPrompt) -join ''").Append(nl);
        builder.Append("    } else {").Append(nl);
        builder.Append("        [string]::Concat('PS ', (Get-Location), '> ')").Append(nl);
        builder.Append("    }").Append(nl);
        builder.Append("    return \"$novaPromptText$([char]27)]133;B$([char]7)\"").Append(nl);
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
