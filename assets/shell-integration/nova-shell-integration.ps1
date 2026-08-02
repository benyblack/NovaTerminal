# Nova Terminal remote shell integration (PowerShell / pwsh).
#
# WHAT IT DOES
#   Emits the OSC 133 shell-integration marks and OSC 7 working-directory
#   reports that Nova Terminal's Command Assist reads:
#     OSC 7               the current directory, once per prompt
#     OSC 133;A           prompt start
#     OSC 133;B           prompt end / first cell of your input
#     OSC 133;C;<b64>     the line you submitted, base64-encoded
#     OSC 133;D;<n>;<ms>  exit code and duration of the command that just ran
#   Nova cannot inject this over SSH (the -File bootstrap does not survive the
#   hop), so you install it on the remote host yourself.
#
# INSTALL (on the REMOTE host, in pwsh)
#   1. New-Item -ItemType Directory -Force -Path (Split-Path $PROFILE)
#   2. notepad $PROFILE      # or: code $PROFILE / vi $PROFILE
#      Paste this whole file at the end and save.
#      (Or save it as ~/.nova-shell-integration.ps1 and add
#       `. ~/.nova-shell-integration.ps1` to $PROFILE.)
#   3. Open a new Nova session to that host.
#
# GUARANTEES
#   - Your `prompt` function is wrapped, never replaced. If you have none, the
#     PowerShell default is synthesized.
#   - Dot-sourcing this file twice does not wrap the wrapper.
#   - PSReadLine is optional: without it you still get A / B / D and lose only
#     the C command text.
#
# Docs: docs/command-assist/RemoteShellIntegration.md

$esc = [char]27
$bel = [char]7
$script:NovaCommandStart = $null
$script:NovaAcceptedCommandText = $null

# Capture the user's `prompt` exactly once. Re-running this in a session it has
# already initialized (a second dot-source, `exec pwsh` into the same profile)
# would otherwise capture OUR wrapper as "the original" and the wrapper would
# call itself forever. Two guards, because a second pass may get a fresh script
# scope where the first guard alone cannot see the earlier capture:
#   1. don't overwrite an already-captured original in this scope;
#   2. never capture a `prompt` whose body carries our wrapper sentinel.
# Worst case (fresh scope + already-wrapped prompt) this degrades to the
# synthesized default prompt; it never recurses.
if (-not (Get-Variable -Name 'NovaOriginalPrompt' -Scope Script -ErrorAction SilentlyContinue)) {
    $script:NovaOriginalPrompt = $null
}
$novaPromptCommand = Get-Command prompt -ErrorAction SilentlyContinue
if ($null -eq $script:NovaOriginalPrompt -and
    $novaPromptCommand -and $novaPromptCommand.ScriptBlock -and
    $novaPromptCommand.ScriptBlock.ToString() -notlike '*__nova_prompt_wrapper*') {
    $script:NovaOriginalPrompt = $novaPromptCommand.ScriptBlock
}

function Write-NovaSequence([string]$sequence) {
    [Console]::Out.Write("$esc$sequence$bel")
}

function Write-NovaPwd() {
    $cwd = [Uri]::EscapeUriString((Get-Location).Path)
    Write-NovaSequence "]7;file://$([System.Net.Dns]::GetHostName())/$cwd"
}

function Write-NovaPromptReady() {
    Write-NovaPwd
    Write-NovaSequence ']133;A'
}

# Emits OSC 133;D only when the previous prompt cycle saw a real accepted
# command, then clears tracked state so the next cycle starts clean even if no
# command was entered.
function Write-NovaCompletion([bool]$lastSuccess, $lastExitCode) {
    if ($script:NovaCommandStart -eq $null) { return }
    $durationMs = [math]::Round((([DateTimeOffset]::UtcNow) - $script:NovaCommandStart).TotalMilliseconds)
    # PowerShell cmdlets set $? to $false on failure but do NOT touch
    # $LASTEXITCODE, so a failing cmdlet after a prior successful external
    # command would otherwise be reported with the stale $LASTEXITCODE=0 (i.e.
    # as a SUCCESS). Only trust $LASTEXITCODE when it is itself nonzero; treat
    # any other failure as exit 1 so Nova's error-insight surfaces see one.
    $exitCode = if ($lastSuccess) { 0 } elseif ($lastExitCode -ne $null -and $lastExitCode -ne 0) { $lastExitCode } else { 1 }
    Write-NovaSequence "]133;D;$exitCode;$durationMs"
    $script:NovaCommandStart = $null
    $script:NovaAcceptedCommandText = $null
}

function Global:prompt {
    # Sentinel the capture guard above greps for. Must stay inside the function
    # body so it survives into ScriptBlock.ToString().
    # __nova_prompt_wrapper
    # Snapshot $? / $LASTEXITCODE on the first lines so later statements do not
    # clobber them before Write-NovaCompletion reads the values.
    $lastSuccess = $?
    $lastExit = $global:LASTEXITCODE
    Write-NovaCompletion $lastSuccess $lastExit
    Write-NovaPromptReady
    # OSC 133;B marks the END of the prompt. Anything this function *writes*
    # lands before the prompt text (the host prints the returned string
    # afterwards), so B must be appended to the returned string instead.
    # -join '' flattens the rare prompt that emits several objects; a
    # single-string prompt (the overwhelming case, including oh-my-posh and
    # starship) round-trips unchanged.
    $novaPromptText = if ($script:NovaOriginalPrompt -ne $null) {
        (& $script:NovaOriginalPrompt) -join ''
    } else {
        [string]::Concat('PS ', (Get-Location), '> ')
    }
    return "$novaPromptText$([char]27)]133;B$([char]7)"
}

# Capture the accepted command text at the shell boundary by wrapping
# PSReadLine's Enter chord. Emits OSC 133;C;<base64> via a direct console write
# (not Write-NovaSequence) so it is unambiguously the only path producing C.
#
# PSReadLine is not loaded in every PowerShell environment (minimal hosts,
# server-core, some constrained-language modes), and on a remote host it is
# less likely to be there than locally. Probe for the cmdlet and skip silently
# if absent: A / B / D still work, and Nova falls back to reading the command
# line out of the grid between the B and C marks.
if (Get-Command Set-PSReadLineKeyHandler -ErrorAction SilentlyContinue) {
    Set-PSReadLineKeyHandler -Chord 'Enter' -ScriptBlock {
        $line = $null
        $cursor = $null
        [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line))
            [Console]::Out.Write("$([char]27)]133;C;$encoded$([char]7)")
            $script:NovaAcceptedCommandText = $line
            $script:NovaCommandStart = [DateTimeOffset]::UtcNow
        }
        [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
    }
}

Write-NovaPromptReady
