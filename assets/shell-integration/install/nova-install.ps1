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
[IO.File]::WriteAllText($dest, $snippet, $utf8NoBom)

if (-not (Test-Path -LiteralPath $dest)) {
    Write-Host "nova: could not write $dest"
    exit 1
}
Write-Host 'nova: wrote ~/.nova-shell-integration.ps1'

$loader = '. ~/.nova-shell-integration.ps1'
$profileDir = Split-Path -Parent $ProfilePath
if ($profileDir -and -not (Test-Path -LiteralPath $profileDir)) {
    New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
}

if ((Test-Path -LiteralPath $ProfilePath) -and
    (Select-String -LiteralPath $ProfilePath -SimpleMatch 'nova-shell-integration' -Quiet)) {
    Write-Host 'nova: loader line already present in $PROFILE - unchanged'
} else {
    # Add-Content appends at the exact end of the file with no separator of its own. A profile
    # that does not end in a newline (common - many editors don't add one) would otherwise get the
    # loader line concatenated onto the user's last line instead of appended as its own line. Skip
    # this for a file that does not exist yet or is empty - Get-Content -Raw returns $null/empty
    # there, so the check below is a no-op and the Add-Content further down creates the file.
    if (Test-Path -LiteralPath $ProfilePath) {
        $existingProfileContent = Get-Content -LiteralPath $ProfilePath -Raw -ErrorAction SilentlyContinue
        if ($existingProfileContent -and
            $existingProfileContent.Substring($existingProfileContent.Length - 1) -ne "`n") {
            Add-Content -LiteralPath $ProfilePath -Value ''
        }
    }
    Add-Content -LiteralPath $ProfilePath -Value $loader
    Write-Host 'nova: added loader line to $PROFILE'
}

Write-Host 'nova: run  . ~/.nova-shell-integration.ps1  to enable it in this session,'
Write-Host 'nova: or open a new Nova session to this host.'
