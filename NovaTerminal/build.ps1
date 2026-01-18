Write-Host "Building Native Rust Library..."
Push-Location native
cargo build --release
if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }
Pop-Location

Write-Host "Building .NET Application..."
dotnet build -c Release
