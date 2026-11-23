#!/usr/bin/env pwsh
<#
.SYNOPSIS
Builds a self-contained portable Release ZIP for Yuyue on Windows (x64 or arm64).

.EXAMPLE
  .\windows\package-release.ps1
  .\windows\package-release.ps1 -Arch arm64 -Version 0.1.0
#>
param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64",
    [string]$Version = "0.1.0",
    [switch]$SkipCoreBuild
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$WinDir = Join-Path $Root "windows"
$Proj = Join-Path $WinDir "Yuyue\Yuyue.csproj"
$Platform = if ($Arch -eq "arm64") { "ARM64" } else { "x64" }
$Rid = "win-$Arch"

if (-not $SkipCoreBuild) {
    Write-Host "▸ Building Rust core + reader bundle ($Arch Release)..."
    & (Join-Path $WinDir "build-core.ps1") -Arch $Arch -SkipTokens
    if ($LASTEXITCODE -ne 0) { throw "build-core.ps1 failed" }
}

$DistRoot = Join-Path $WinDir "dist"
$StageDir = Join-Path $DistRoot "Yuyue-$Version-windows-$Arch"
$ZipPath = Join-Path $DistRoot "Yuyue-$Version-windows-$Arch.zip"

if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null

Write-Host "▸ Publishing self-contained WinUI 3 app ($Rid)..."
dotnet publish $Proj `
    -c Release `
    -p:Platform=$Platform `
    -r $Rid `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false `
    -o $StageDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Strip debug symbols from portable distribution
Get-ChildItem $StageDir -Filter "*.pdb" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "▸ Compressing portable archive -> $ZipPath"
Compress-Archive -Path $StageDir -DestinationPath $ZipPath -CompressionLevel Optimal

$sizeMb = [Math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Host "✓ Release package ready: $ZipPath ($sizeMb MB)"
