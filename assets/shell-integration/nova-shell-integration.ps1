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
#   - An existing PSReadLine Enter binding is delegated to rather than dropped,
#     as long as it is a NAMED PSReadLine function (AcceptLine,
#     AcceptOrInsertNewline, ValidateAndAcceptLine, ...). A custom SCRIPTBLOCK
#     bound to Enter cannot be read back out of PSReadLine by any public API,
#     so it is replaced; if you have one, re-bind it after this file.
#
# WHAT YOU LOSE WITHOUT PSReadLine
#   PSReadLine is where both the C mark and the command clock come from, so a
#   host without it gets OSC 7, A and B only: no `133;C` command text AND no
#   `133;D`, because there is no accepted command to time. Nova falls back to
#   reading the command line off the grid between B and the cursor, which is
#   what an un-instrumented shell already does for text; it has no fallback for
#   exit codes or durations.
#
# Docs: docs/command-assist/RemoteShellIntegration.md

# Nova-scoped names throughout. This file is dot-sourced into the user's own
# $PROFILE, so a bare `$esc` / `$bel` would land in their session and quietly
# shadow (or be shadowed by) anything else that picked the same obvious name.
$script:NovaEsc = [char]27
$script:NovaBel = [char]7
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
    [Console]::Out.Write("$($script:NovaEsc)$sequence$($script:NovaBel)")
}

function Write-NovaPwd() {
    # The path part of a file:// URI is rooted, so it needs exactly one leading
    # slash. On Linux/macOS pwsh the path ALREADY starts with one, and a blind
    # "host" + "/" + path produced `file://host//home/you` - two slashes, which
    # is a different URI and not the one the user is in. On Windows the drive
    # letter has no leading slash and does need one added, and the separators
    # have to be flipped before they are escaped as %5C.
    #
    # Two further fixes, and they are why there is no hostname here any more:
    #   * No authority. `file://HOST/C:/Users/you` is a well-formed URI whose
    #     path is the UNC share \\HOST\C:\Users\you - a path that does not exist
    #     on the machine that emitted it. Nothing consumes the hostname.
    #   * Per-segment escaping. EscapeUriString leaves the URI-reserved '#' and
    #     '?' alone, so a directory named `a#b` truncated at the fragment;
    #     EscapeDataString covers them. ':' is put back afterwards because a
    #     drive letter is an ordinary path-segment character in a URI.
    # Kept identical to PowerShellBootstrapBuilder.Write-NovaPwd.
    $novaSegments = ((Get-Location).Path -replace '\\', '/') -split '/'
    $novaPath = (($novaSegments | ForEach-Object { [Uri]::EscapeDataString($_) -replace '%3A', ':' }) -join '/')
    if (-not $novaPath.StartsWith('/')) { $novaPath = '/' + $novaPath }
    Write-NovaSequence "]7;file://$novaPath"
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
    # Whatever Enter already did, keep doing it. `AcceptOrInsertNewline` (the
    # pwsh default in recent PSReadLine versions) and `ValidateAndAcceptLine`
    # are both common, and hard-coding AcceptLine would break multi-line editing
    # for anyone on them. Only a NAMED PSReadLine function can be recovered -
    # Get-PSReadLineKeyHandler reports a scriptblock binding as a description
    # string with no way back to the block - so a custom scriptblock is
    # documented as clobbered in the header rather than silently half-handled.
    #
    # The name is validated against [A-Za-z]+ before it is turned into a
    # scriptblock: it comes from PSReadLine, but building executable text out of
    # a string is not something to do on trust alone.
    if (-not (Get-Variable -Name 'NovaEnterFallback' -Scope Script -ErrorAction SilentlyContinue)) {
        $novaExistingEnter = Get-PSReadLineKeyHandler -Chord 'Enter' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $novaEnterFunction = 'AcceptLine'
        if ($novaExistingEnter -and
            $novaExistingEnter.Function -match '^[A-Za-z]+$' -and
            $novaExistingEnter.Description -notlike '*Nova*') {
            $novaEnterFunction = $novaExistingEnter.Function
        }
        $script:NovaEnterFallback = [scriptblock]::Create(
            "[Microsoft.PowerShell.PSConsoleReadLine]::$novaEnterFunction()")
    }

    Set-PSReadLineKeyHandler -Chord 'Enter' -Description 'Nova shell integration: OSC 133;C then accept' -ScriptBlock {
        $line = $null
        $cursor = $null
        [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line))
            [Console]::Out.Write("$([char]27)]133;C;$encoded$([char]7)")
            $script:NovaAcceptedCommandText = $line
            $script:NovaCommandStart = [DateTimeOffset]::UtcNow
        }
        if ($script:NovaEnterFallback) {
            & $script:NovaEnterFallback
        } else {
            [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
        }
    }
}

Write-NovaPromptReady
