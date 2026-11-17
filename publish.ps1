# publish.ps1 — builds a single self-contained sqlr executable (no .NET runtime needed on the target)
# Usage: .\publish.ps1 [-Runtime win-x64|win-arm64|linux-x64|osx-x64|osx-arm64]

param(
    [string]$Runtime = "win-x64"
)

$OutDir = ".\dist\$Runtime"
$Exe    = if ($Runtime -like "win-*") { "sqlr.exe" } else { "sqlr" }

Write-Host "Publishing sqlr for $Runtime -> $OutDir" -ForegroundColor Cyan

# Single-file settings (native SqlClient library bundled, compression on) live in sqlr.csproj.
dotnet publish .\sqlr.csproj `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $OutDir

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Build succeeded: $OutDir\$Exe" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Yellow
    Write-Host "  1. Copy $OutDir\$Exe to a permanent location, e.g. C:\tools\sqlr\"
    Write-Host "  2. Run:  sqlr --add-to-path"
    Write-Host "  3. Restart your terminal"
    Write-Host "  4. Run:  sqlr connections add"
} else {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}
