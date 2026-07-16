<#
.SYNOPSIS
    One-time bootstrap submission of NovaTerminal to the winget community repo.

.DESCRIPTION
    The first winget-pkgs submission must be done by hand (the release.yml
    'submit_winget' job uses `wingetcreate update`, which only works once the
    package already exists upstream). This script does that first submission
    from the in-repo manifest set.

    It:
      1. verifies winget + wingetcreate are available (installs wingetcreate if not),
      2. validates the manifest set locally,
      3. submits it to microsoft/winget-pkgs, opening a PR from your fork.

    You must supply a GitHub Personal Access Token (classic) with 'public_repo'
    scope — wingetcreate uses it to fork winget-pkgs and open the PR. Create one
    at https://github.com/settings/tokens. This script never stores it.

.PARAMETER Version
    The manifest version folder to submit. Default: 0.3.0.

.PARAMETER Token
    GitHub PAT. If omitted, the WINGET_PAT environment variable is used.

.EXAMPLE
    ./submit-first-time.ps1 -Token ghp_xxx

.EXAMPLE
    $env:WINGET_PAT = 'ghp_xxx'; ./submit-first-time.ps1 -Version 0.3.0
#>
[CmdletBinding()]
param(
    [string]$Version = "0.3.0",
    [string]$Token = $env:WINGET_PAT
)

$ErrorActionPreference = "Stop"

# Resolve the manifest directory relative to this script (packaging/winget/<version>).
$manifestDir = Join-Path $PSScriptRoot $Version
if (-not (Test-Path $manifestDir)) {
    throw "Manifest directory not found: $manifestDir. Cut the manifest for $Version first (see README.md)."
}

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "No GitHub token. Pass -Token <PAT> or set `$env:WINGET_PAT. Needs 'public_repo' scope to fork winget-pkgs and open the PR."
}

# 1. winget client (ships with App Installer on Windows 10/11).
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "winget not found. Install 'App Installer' from the Microsoft Store, then re-run."
}

# 2. wingetcreate (Windows Package Manager Manifest Creator).
if (-not (Get-Command wingetcreate -ErrorAction SilentlyContinue)) {
    Write-Host "wingetcreate not found; installing via winget..."
    winget install --id Microsoft.WingetCreate --exact --accept-source-agreements --accept-package-agreements
    if (-not (Get-Command wingetcreate -ErrorAction SilentlyContinue)) {
        throw "wingetcreate still not on PATH after install. Open a new shell and re-run, or install manually: winget install Microsoft.WingetCreate"
    }
}

# 3. Validate locally first (schema + required fields; offline).
Write-Host "Validating manifest set in $manifestDir ..."
winget validate --manifest $manifestDir

# 4. Submit. wingetcreate re-validates, downloads the installer URL, verifies the
#    SHA256, forks microsoft/winget-pkgs, commits under
#    manifests/b/benyblack/NovaTerminal/<version>/, and opens the PR.
Write-Host "Submitting to microsoft/winget-pkgs (this forks the repo and opens a PR)..."
wingetcreate submit --token $Token $manifestDir

Write-Host ""
Write-Host "Done. Track the PR at https://github.com/microsoft/winget-pkgs/pulls (author: your GitHub account)."
Write-Host "After it merges, 'winget install benyblack.NovaTerminal' works, and future releases"
Write-Host "auto-submit via the release.yml 'submit_winget' job (set the WINGET_PAT repo secret)."
