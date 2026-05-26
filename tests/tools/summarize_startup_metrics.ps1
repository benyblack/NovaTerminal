[CmdletBinding(DefaultParameterSetName = "single")]
param(
    [Parameter(ParameterSetName = "single", Mandatory = $true)]
    [string]$InputPath,
    [Parameter(ParameterSetName = "compare", Mandatory = $true)]
    [string]$Baseline,
    [Parameter(ParameterSetName = "compare", Mandatory = $true)]
    [string]$Candidate,
    [string]$Out
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-OrderedPhases {
    return @(
        "MainWindowConstructedMs",
        "WindowOpenedMs",
        "FirstTerminalReadyMs",
        "SessionRestoreCompleteMs",
        "DeferredWorkCompleteMs",
        "BackgroundRestoreCompleteMs"
    )
}

function Get-PhaseLabel {
    param([string]$PhaseName)

    switch ($PhaseName) {
        "MainWindowConstructedMs" { return "Main Window Constructed" }
        "WindowOpenedMs" { return "Window Opened" }
        "FirstTerminalReadyMs" { return "First Terminal Ready" }
        "SessionRestoreCompleteMs" { return "Session Restore Complete" }
        "DeferredWorkCompleteMs" { return "Deferred Work Complete" }
        "BackgroundRestoreCompleteMs" { return "Background Restore Complete" }
        default { return $PhaseName }
    }
}

function Get-StartupRecords {
    param([string]$Path)

    $fullPath = (Resolve-Path $Path).Path
    $records = New-Object System.Collections.Generic.List[object]
    $files = @()

    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        $files = Get-ChildItem -LiteralPath $fullPath -Filter *.jsonl -Recurse | Sort-Object FullName
    }
    else {
        $files = @(Get-Item -LiteralPath $fullPath)
    }

    foreach ($file in $files) {
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            $trimmed = $line.Trim()
            if ($trimmed.Length -eq 0) {
                continue
            }

            $records.Add(($trimmed | ConvertFrom-Json))
        }
    }

    return $records
}

function Get-StartupSummary {
    param([System.Collections.Generic.List[object]]$Records)

    $phases = [ordered]@{}

    foreach ($phase in Get-OrderedPhases) {
        $values = @(@(
            foreach ($record in $Records) {
                $value = $record.$phase
                if ($null -ne $value) {
                    [double]$value
                }
            }
        ) | Sort-Object)

        if ($values.Count -eq 0) {
            continue
        }

        $count = $values.Count
        $average = ($values | Measure-Object -Average).Average
        if (($count % 2) -eq 1) {
            $median = $values[[int]($count / 2)]
        }
        else {
            $median = ($values[($count / 2) - 1] + $values[$count / 2]) / 2.0
        }

        $phases[$phase] = [pscustomobject]@{
            Count = $count
            AverageMs = [double]$average
            MedianMs = [double]$median
            MinMs = [double]$values[0]
            MaxMs = [double]$values[-1]
        }
    }

    return [pscustomobject]@{
        LaunchCount = $Records.Count
        Phases = $phases
    }
}

function Compare-StartupSummaries {
    param(
        [pscustomobject]$BaselineSummary,
        [pscustomobject]$CandidateSummary
    )

    $phases = [ordered]@{}

    foreach ($phase in Get-OrderedPhases) {
        if (-not $BaselineSummary.Phases.Contains($phase) -or -not $CandidateSummary.Phases.Contains($phase)) {
            continue
        }

        $baselineAverage = [double]$BaselineSummary.Phases[$phase].AverageMs
        $candidateAverage = [double]$CandidateSummary.Phases[$phase].AverageMs
        $delta = $candidateAverage - $baselineAverage
        $improvement = if ($baselineAverage -le 0) { 0 } else { (($baselineAverage - $candidateAverage) / $baselineAverage) * 100.0 }

        $phases[$phase] = [pscustomobject]@{
            BaselineAverageMs = $baselineAverage
            CandidateAverageMs = $candidateAverage
            DeltaMs = $delta
            ImprovementPercent = [double]$improvement
        }
    }

    return [pscustomobject]@{
        BaselineLaunchCount = $BaselineSummary.LaunchCount
        CandidateLaunchCount = $CandidateSummary.LaunchCount
        Phases = $phases
    }
}

function Build-MarkdownReport {
    param([pscustomobject]$Comparison)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Startup Performance Report")
    $lines.Add("")
    $lines.Add("Baseline launches: $($Comparison.BaselineLaunchCount)")
    $lines.Add("Candidate launches: $($Comparison.CandidateLaunchCount)")
    $lines.Add("")
    $lines.Add("| Phase | Baseline Avg (ms) | Candidate Avg (ms) | Delta (ms) | Improvement |")
    $lines.Add("| --- | ---: | ---: | ---: | ---: |")

    foreach ($phase in Get-OrderedPhases) {
        if (-not $Comparison.Phases.Contains($phase)) {
            continue
        }

        $values = $Comparison.Phases[$phase]
        $lines.Add(
            ("| {0} | {1:N2} | {2:N2} | {3:N2} | {4:N2}% |" -f (Get-PhaseLabel $phase), $values.BaselineAverageMs, $values.CandidateAverageMs, $values.DeltaMs, $values.ImprovementPercent))
    }

    return ($lines -join [Environment]::NewLine)
}

if ($PSCmdlet.ParameterSetName -eq "single") {
    $records = Get-StartupRecords -Path $InputPath
    $summary = Get-StartupSummary -Records $records

    Write-Host ("Launches: {0}" -f $summary.LaunchCount)
    foreach ($phase in Get-OrderedPhases) {
        if (-not $summary.Phases.Contains($phase)) {
            continue
        }

        $stats = $summary.Phases[$phase]
        Write-Host ("{0}: count={1} avg={2:N2}ms median={3:N2}ms min={4:N0}ms max={5:N0}ms" -f (Get-PhaseLabel $phase), $stats.Count, $stats.AverageMs, $stats.MedianMs, $stats.MinMs, $stats.MaxMs)
    }

    return
}

$baselineRecords = Get-StartupRecords -Path $Baseline
$candidateRecords = Get-StartupRecords -Path $Candidate
$baselineSummary = Get-StartupSummary -Records $baselineRecords
$candidateSummary = Get-StartupSummary -Records $candidateRecords
$comparison = Compare-StartupSummaries -BaselineSummary $baselineSummary -CandidateSummary $candidateSummary
$report = Build-MarkdownReport -Comparison $comparison

if ($Out) {
    $outPath = $Out
    if (-not [System.IO.Path]::IsPathRooted($outPath)) {
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
        $outPath = Join-Path $repoRoot $outPath
    }

    $directory = Split-Path -Parent $outPath
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    Set-Content -LiteralPath $outPath -Value $report -Encoding UTF8
}

Write-Output $report
