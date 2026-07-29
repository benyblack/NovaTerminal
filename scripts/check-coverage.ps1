#!/usr/bin/env pwsh
# Fails when an assembly's line coverage drops below a floor (#117).
#
# Why a script and not a coverlet setting: coverlet.collector (the `--collect` /
# runsettings path) has no threshold option - only coverlet.msbuild does, which would mean
# a second coverage package and a different invocation. Parsing the cobertura report the
# collector already writes keeps one mechanism.
#
# Why pwsh and not bash: this runs on both windows-latest and ubuntu-latest, pwsh ships on
# both GitHub runner images, and it parses XML natively instead of grepping attributes out
# of markup.
#
# Usage:
#   scripts/check-coverage.ps1 -ReportDirectory coverage/VT -MinimumLinePercent 50

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory,

    [Parameter(Mandatory = $true)]
    [double]$MinimumLinePercent,

    # Purely for the log line, so a failure says which assembly is short.
    [string]$Label = ""
)

$ErrorActionPreference = 'Stop'

$reports = @(Get-ChildItem -Path $ReportDirectory -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue)

if ($reports.Count -eq 0) {
    # A missing report means collection silently stopped working, which would otherwise
    # look like a pass. Treat it as a failure.
    Write-Host "::error::No coverage.cobertura.xml found under '$ReportDirectory'. Coverage collection did not run."
    exit 1
}

# One report per test project run; take the newest if a stale one lingers.
$report = ($reports | Sort-Object LastWriteTime -Descending)[0]
[xml]$xml = Get-Content -Path $report.FullName

$lineRate = [double]$xml.coverage.'line-rate'
$branchRate = [double]$xml.coverage.'branch-rate'
$linePercent = [math]::Round($lineRate * 100, 2)
$branchPercent = [math]::Round($branchRate * 100, 2)

$name = if ([string]::IsNullOrWhiteSpace($Label)) { $ReportDirectory } else { $Label }

Write-Host "$name coverage: line ${linePercent}% (floor ${MinimumLinePercent}%), branch ${branchPercent}%"
Write-Host "  report: $($report.FullName)"

if ($linePercent -lt $MinimumLinePercent) {
    Write-Host "::error::$name line coverage ${linePercent}% is below the ${MinimumLinePercent}% floor."
    exit 1
}

exit 0
