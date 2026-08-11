# Nova Terminal remote shell integration installer (PowerShell).
#
# Decoded to a temp file by the one-liner Settings copies, invoked with the call operator (& ) so it
# runs in a CHILD SCOPE - nothing it defines reaches your session - and then deleted. $PROFILE is
# still visible here because it is an automatic variable in every scope.
#
# The parameters exist so this file is testable without touching the developer's real profile; the
# generated one-liner passes none of them.

param(
    [string]$ProfilePath = $PROFILE,
    [string]$DestDir = $HOME
)

$dest = Join-Path $DestDir '.nova-shell-integration.ps1'
$snippet = @'
@@NOVA_SNIPPET@@
'@

# WriteAllText with an explicit no-BOM UTF-8 rather than Set-Content -Encoding utf8NoBOM: that
# parameter value does not exist on Windows PowerShell 5.1, and a remote host may be running it.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
try {
    [IO.File]::WriteAllText($dest, $snippet, $utf8NoBom)
} catch {
    # try/catch rather than a Test-Path afterwards: WriteAllText throws on failure, so the file
    # always exists by the time a Test-Path could run and the check could never fire.
    Write-Host "nova: could not write $dest - $($_.Exception.Message)"
    exit 1
}
Write-Host 'nova: wrote ~/.nova-shell-integration.ps1'

$loader = '. ~/.nova-shell-integration.ps1'
$profileDir = Split-Path -Parent $ProfilePath
if ($profileDir -and -not (Test-Path -LiteralPath $profileDir)) {
    New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
}

$novaStatus = 0

# A regex anchored to the start of a non-comment line rather than -SimpleMatch anywhere in the
# file. The marker is the file name, so a hand-typed variant of the loader line still counts, but
# a profile whose only mention is a comment - a previous attempt commented out, or a note to self -
# must not be read as "already installed" and left without a loader line. '#' is PowerShell's
# comment character too, so this is the same rule the sh installer applies.
if ((Test-Path -LiteralPath $ProfilePath) -and
    (Select-String -LiteralPath $ProfilePath -Pattern '^[^#]*nova-shell-integration' -Quiet)) {
    Write-Host "nova: loader line already present in $ProfilePath - unchanged"
} else {
    # Add-Content appends at the exact end of the file with no separator of its own. A profile
    # that does not end in a newline (common - many editors don't add one) would otherwise get the
    # loader line concatenated onto the user's last line instead of appended as its own line. Skip
    # this for a file that does not exist yet or is empty - Get-Content -Raw returns $null/empty
    # there, so the check below is a no-op and the Add-Content further down creates the file.
    #
    # -ErrorAction Stop on both appends. Add-Content's default is Continue: a profile the user
    # cannot write - read-only, ACL-denied, a directory in the way, a full disk - reports the
    # error and lets the script carry on to print "added loader line" over the top of it, which
    # sends the user looking in the right file for a line that was never written.
    try {
        if (Test-Path -LiteralPath $ProfilePath) {
            $existingProfileContent = Get-Content -LiteralPath $ProfilePath -Raw -ErrorAction SilentlyContinue
            if ($existingProfileContent -and
                $existingProfileContent.Substring($existingProfileContent.Length - 1) -ne "`n") {
                Add-Content -LiteralPath $ProfilePath -Value '' -ErrorAction Stop
            }
        }
        Add-Content -LiteralPath $ProfilePath -Value $loader -ErrorAction Stop
        Write-Host "nova: added loader line to $ProfilePath"
    } catch {
        Write-Host "nova: could not write $ProfilePath - add this line to it by hand:"
        Write-Host "nova:   $loader"
        $novaStatus = 1
    }
}

Write-Host 'nova: run  . ~/.nova-shell-integration.ps1  to enable it in this session,'
Write-Host 'nova: or open a new Nova session to this host.'
exit $novaStatus
