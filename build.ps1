$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "=== 1/3 Building ShanaPress Solution (Release) ===" -ForegroundColor Cyan
dotnet build ShanaPress.sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n=== 2/3 Running Automated Unit & FFmpeg Integration Tests ===" -ForegroundColor Cyan
dotnet run --project tests/ShanaPress.Tests/ShanaPress.Tests.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n=== 3/3 Publishing dist\ShanaPress.exe ===" -ForegroundColor Cyan
$distDir = Join-Path $root "dist"
if (Test-Path $distDir) {
    Remove-Item -Recurse -Force $distDir
}

dotnet publish src/ShanaPress.App/ShanaPress.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o $distDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exePath = Join-Path $distDir "ShanaPress.exe"
if (Test-Path $exePath) {
    $sizeMb = [Math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host "`n[OK] Published: $exePath ($sizeMb MB)" -ForegroundColor Green
} else {
    Write-Error "dist\ShanaPress.exe was not generated."
    exit 1
}
