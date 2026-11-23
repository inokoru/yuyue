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
    [switch]$SkipCoreBuild,
    [switch]$KeepAllLocales,
    [switch]$BuildInstaller
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

# Trim ~80 unused WindowsAppSDK satellite locale folders (keeps zh/en only, saving ~25-35 MB)
if (-not $KeepAllLocales) {
    $keepLocales = @("zh-CN", "zh-TW", "zh-Hans", "zh-Hant", "en-US", "en-GB", "en")
    $localeRegex = '^[a-z]{2,3}(-[A-Za-z0-9]{2,8}){1,2}$'
    $removed = 0
    Get-ChildItem $StageDir -Directory | Where-Object {
        $_.Name -match $localeRegex -and ($keepLocales -notcontains $_.Name)
    } | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        $removed++
    }
    Write-Host "▸ Trimmed $removed unused satellite locale folders (kept zh/en)"
}

Write-Host "▸ Compressing portable archive -> $ZipPath"
Compress-Archive -Path $StageDir -DestinationPath $ZipPath -CompressionLevel Optimal

$sizeMb = [Math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Host "✓ Portable release package ready: $ZipPath ($sizeMb MB)"

if ($BuildInstaller) {
    $issPath = Join-Path $WinDir "installer.iss"
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $candidates = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        )
        $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($found) { $iscc = Get-Item $found }
    }
    if (-not $iscc) {
        Write-Warning "Inno Setup 6 (ISCC.exe) not found; skipped installer build. Install Inno Setup 6 or run without -BuildInstaller."
    } else {
        $isccExe = if ($iscc -is [System.Management.Automation.CommandInfo]) { $iscc.Source } else { $iscc.FullName }
        Write-Host "▸ Building Windows Setup installer via Inno Setup ($issPath)..."
        & $isccExe "/DMyAppVersion=$Version" "/DMyArch=$Arch" "/DStageDir=$StageDir" "/DOutDir=$DistRoot" $issPath
        if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed" }
        Write-Host "✓ Installer ready in $DistRoot"
    }
}
