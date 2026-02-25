$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

Write-Host "=== TOOLING ==="
dotnet --info
rustc --version
cargo --version

Write-Host "=== CLEAN ==="
dotnet clean

Write-Host "=== RESTORE ==="
dotnet restore

Write-Host "=== BUILD RELEASE ==="
dotnet build -c Release -warnaserror

Write-Host "=== TEST ==="
dotnet test -c Release --no-build

Write-Host "=== REPLAY TESTS ==="
dotnet test -c Release --filter Category=Replay

Write-Host "=== AOT PUBLISH ==="
dotnet publish src/NovaTerminal.App/NovaTerminal.App.csproj `
    -c Release `
    -r win-x64 `
    -p:PublishAot=true `
    -p:StripSymbols=true

Write-Host "=== FORMAT CHECK ==="
dotnet format --verify-no-changes

Write-Host "CI SUCCESS"
