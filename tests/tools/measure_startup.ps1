[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [Parameter(Mandatory = $true)]
    [string]$Label,
    [int]$Iterations = 10,
    [int]$TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-LineCount {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return 0
    }

    return @((Get-Content -LiteralPath $Path)).Count
}

function Write-MeasurementSettings {
    param([string]$SettingsPath)

    $settings = @{
        QuakeModeEnabled = $false
    }

    $settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $SettingsPath -Encoding UTF8
}

function Write-MeasurementSession {
    param([string]$SessionPath)

    $activePane = [guid]::NewGuid().ToString()
    $splitLeft = [guid]::NewGuid().ToString()
    $splitRight = [guid]::NewGuid().ToString()
    $backgroundPane = [guid]::NewGuid().ToString()

    $session = @{
        ActiveTabIndex = 0
        Tabs = @(
            @{
                TabId = [guid]::NewGuid().ToString()
                Title = "Primary"
                ActivePaneId = $activePane
                BroadcastInputEnabled = $false
                Root = @{
                    Type = 0
                    PaneId = $activePane
                    Command = "cmd.exe"
                    Arguments = ""
                }
            },
            @{
                TabId = [guid]::NewGuid().ToString()
                Title = "Split"
                ActivePaneId = $splitRight
                BroadcastInputEnabled = $false
                Root = @{
                    Type = 1
                    SplitOrientation = 0
                    Sizes = @("1*", "1*")
                    Children = @(
                        @{
                            Type = 0
                            PaneId = $splitLeft
                            Command = "cmd.exe"
                            Arguments = ""
                        },
                        @{
                            Type = 0
                            PaneId = $splitRight
                            Command = "cmd.exe"
                            Arguments = ""
                        }
                    )
                }
            },
            @{
                TabId = [guid]::NewGuid().ToString()
                Title = "Background"
                ActivePaneId = $backgroundPane
                BroadcastInputEnabled = $false
                Root = @{
                    Type = 0
                    PaneId = $backgroundPane
                    Command = "cmd.exe"
                    Arguments = ""
                }
            }
        )
    }

    $session | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $SessionPath -Encoding UTF8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$appExe = Join-Path $repoRoot "src\NovaTerminal.App\bin\$Configuration\net10.0\NovaTerminal.exe"

if (-not (Test-Path -LiteralPath $appExe)) {
    throw "App executable not found at $appExe. Build the app first."
}

$outputRoot = Join-Path $repoRoot "artifacts-codex\startup\$Label"
$metricsFile = Join-Path $outputRoot "startup_metrics.jsonl"
$appDataRoot = Join-Path $outputRoot "appdata"
$settingsPath = Join-Path $appDataRoot "settings.json"
$sessionPath = Join-Path $appDataRoot "sessions\last_session.json"

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $appDataRoot "sessions") | Out-Null

if (Test-Path -LiteralPath $metricsFile) {
    Remove-Item -LiteralPath $metricsFile -Force
}

Write-MeasurementSettings -SettingsPath $settingsPath

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    Write-MeasurementSession -SessionPath $sessionPath
    $beforeCount = Get-LineCount -Path $metricsFile

    $process = Start-Process -FilePath $appExe -PassThru -WindowStyle Normal -Environment @{
        NOVATERM_APPDATA_ROOT = $appDataRoot
        NOVATERM_STARTUP_METRICS = "1"
        NOVATERM_STARTUP_METRICS_OUT = $metricsFile
    }

    $captured = $false
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    try {
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100

            if ((Get-LineCount -Path $metricsFile) -gt $beforeCount) {
                $captured = $true
                break
            }

            if ($process.HasExited) {
                break
            }
        }
    }
    finally {
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) {
                $process.Kill($true)
                $process.WaitForExit()
            }
        }

        $process.Dispose()
    }

    if (-not $captured) {
        throw "Iteration $iteration did not produce a startup metrics record within $TimeoutSeconds seconds."
    }
}

Write-Host "Startup metrics captured to $metricsFile"
