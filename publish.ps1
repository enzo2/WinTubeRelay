# Publish a single-file Windows release build.
# Usage: .\publish.ps1

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectDir "publish\win-x64"
$projectFile = Join-Path $projectDir "WinTubeRelay.csproj"

if (Test-Path $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}

dotnet publish $projectFile `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    -o $outputDir

Write-Host ""
Write-Host "Publish complete:" -ForegroundColor Green
Write-Host "  $outputDir" -ForegroundColor Cyan
