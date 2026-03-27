# publish.ps1 — builds a single self-contained sqlr.exe
# Usage: .\publish.ps1 [-Runtime win-x64|linux-x64|osx-x64]

param(
    [string]$Runtime = "win-x64"
)

$OutDir = ".\dist\$Runtime"

Write-Host "Publishing sqlr for $Runtime -> $OutDir" -ForegroundColor Cyan

dotnet publish `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $OutDir

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Build succeeded: $OutDir\sqlr.exe" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Yellow
    Write-Host "  1. Copy $OutDir to a permanent location, e.g. C:\tools\sqlr\"
    Write-Host "  2. Run:  sqlr --add-to-path"
    Write-Host "  3. Restart your terminal"
    Write-Host "  4. Run:  sqlr connections add"
} else {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}

# ── Alternate targets (uncomment as needed) ───────────────────────────────
#
# Linux:
#   dotnet publish -c Release -r linux-x64 --self-contained true `
#       -p:PublishSingleFile=true -o ./dist/linux-x64
#
# macOS:
#   dotnet publish -c Release -r osx-x64 --self-contained true `
#       -p:PublishSingleFile=true -o ./dist/osx-x64
#
# macOS ARM (M1/M2):
#   dotnet publish -c Release -r osx-arm64 --self-contained true `
#       -p:PublishSingleFile=true -o ./dist/osx-arm64
