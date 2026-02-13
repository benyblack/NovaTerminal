param(
    [string]$Configuration = "Release",
    [string]$Filter = "",
    [string]$TestProject = "NovaTerminal.Tests/NovaTerminal.Tests.csproj",
    [switch]$NoRestore,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetTestArgs
)

$ErrorActionPreference = "Stop"

Write-Host "Building NovaTerminal ($Configuration)..."
$buildArgs = @("build", "NovaTerminal/NovaTerminal.csproj", "-c", $Configuration)
if ($NoRestore) { $buildArgs += "--no-restore" }

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Running tests with --no-build ($Configuration)..."
$testArgs = @("test", $TestProject, "-c", $Configuration, "--no-build")
if ($NoRestore) { $testArgs += "--no-restore" }
if ($Filter) { $testArgs += @("--filter", $Filter) }
if ($DotnetTestArgs) { $testArgs += $DotnetTestArgs }

& dotnet @testArgs
exit $LASTEXITCODE
