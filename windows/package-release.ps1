#!/usr/bin/env pwsh
<#
.SYNOPSIS
Builds a self-contained Release portable ZIP (and optional Inno Setup installer)
for Yuyue on Windows (x64 or arm64), verifies all required runtime assets,
and writes SHA256SUMS-windows-<arch>.txt + windows-update-<arch>.json.

.EXAMPLE
  .\windows\package-release.ps1
  .\windows\package-release.ps1 -Arch x64 -Version 0.1.0 -BuildNumber 1 -BuildInstaller
#>
param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64",
    [string]$Version = "0.1.0",
    [int]$BuildNumber = 1,
    [switch]$SkipCoreBuild,
    [switch]$KeepAllLocales,
    [switch]$BuildInstaller,
    [string]$FeedbackTopic = "",
    [string]$FeedbackPublicKey = ""
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$WinDir = Join-Path $Root "windows"
$Proj = Join-Path $WinDir "Yuyue\Yuyue.csproj"
$Platform = if ($Arch -eq "arm64") { "ARM64" } else { "x64" }
$Rid = "win-$Arch"

if ([string]::IsNullOrWhiteSpace($FeedbackTopic) -and $env:YUYUE_FEEDBACK_TOPIC) {
    $FeedbackTopic = $env:YUYUE_FEEDBACK_TOPIC
}
if ([string]::IsNullOrWhiteSpace($FeedbackPublicKey) -and $env:YUYUE_FEEDBACK_PUBLIC_KEY) {
    $FeedbackPublicKey = $env:YUYUE_FEEDBACK_PUBLIC_KEY
}

if (-not $SkipCoreBuild) {
    Write-Host "[1/6] Building Rust core + reader bundle ($Arch Release)..."
    & (Join-Path $WinDir "build-core.ps1") -Arch $Arch -SkipTokens
    if ($LASTEXITCODE -ne 0) { throw "build-core.ps1 failed" }
}

Write-Host "[2/6] Building & auditing English L10n dictionary..."
node (Join-Path $WinDir "build-l10n.mjs") --audit
if ($LASTEXITCODE -ne 0) { throw "windows/build-l10n.mjs --audit failed" }

$DistRoot = Join-Path $WinDir "dist"
$StageName = "Yuyue-$Version-windows-$Arch"
$StageDir = Join-Path $DistRoot $StageName
$ZipName = "$StageName.zip"
$ZipPath = Join-Path $DistRoot $ZipName

if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null

Write-Host "[3/6] Publishing self-contained WinUI 3 app ($Rid v$Version build $BuildNumber)..."
$publishArgs = @(
    "publish", $Proj,
    "-c", "Release",
    "-p:Platform=$Platform",
    "-r", $Rid,
    "--self-contained", "true",
    "-p:WindowsAppSDKSelfContained=true",
    "-p:PublishSingleFile=false",
    "-p:Version=$Version",
    "-p:AssemblyVersion=$Version.$BuildNumber",
    "-p:FileVersion=$Version.$BuildNumber",
    "-o", $StageDir
)
if (-not [string]::IsNullOrWhiteSpace($FeedbackTopic)) {
    Write-Host "      Injecting build-time feedback relay configuration."
    $publishArgs += "-p:YuyueFeedbackTopic=$FeedbackTopic"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Strip debug symbols from release distribution
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
    Write-Host "      Trimmed $removed unused satellite locale folders (kept zh/en)."
}

Write-Host "[4/6] Verifying required release manifest files..."
$requiredFiles = @(
    "Yuyue.exe",
    "yuyue_core.dll",
    "onnxruntime.dll",
    "WebView2Loader.dll",
    "Assets\AppIcon.ico",
    "Assets\Brands\obsidian.png",
    "Assets\Brands\deepseek.png",
    "Assets\Reader\reader.js",
    "Assets\Reader\reader.css",
    "Assets\L10n\en.json",
    "Assets\Dict\ecdict.tsv",
    "Assets\Shared\wifi-page.html",
    "Assets\Shared\CHANGELOG.md"
)
$missing = @()
foreach ($rel in $requiredFiles) {
    $full = Join-Path $StageDir $rel
    if (-not (Test-Path $full)) { $missing += $rel }
}
if ($missing.Count -gt 0) {
    throw "Release staging verification failed! Missing required files: $($missing -join ', ')"
}
Write-Host "      All $($requiredFiles.Count) required runtime assets verified."

Write-Host "[5/6] Compressing portable archive -> $ZipPath"
Compress-Archive -Path $StageDir -DestinationPath $ZipPath -CompressionLevel Optimal
$zipSizeMb = [Math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Host "      Portable ZIP ready: $ZipName ($zipSizeMb MB)"

$artifacts = @($ZipPath)

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
        Write-Host "      Building Windows Setup installer via Inno Setup ($issPath)..."
        & $isccExe "/DMyAppVersion=$Version" "/DMyBuildNumber=$BuildNumber" "/DMyArch=$Arch" "/DStageDir=$StageDir" "/DOutDir=$DistRoot" $issPath
        if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed" }
        $setupPath = Join-Path $DistRoot "$StageName-Setup.exe"
        if (Test-Path $setupPath) {
            $setupMb = [Math]::Round((Get-Item $setupPath).Length / 1MB, 1)
            Write-Host "      Installer ready: $(Split-Path -Leaf $setupPath) ($setupMb MB)"
            $artifacts += $setupPath
        }
    }
}

Write-Host "[6/6] Computing SHA-256 checksums and update manifest fragment..."
$sumsPath = Join-Path $DistRoot "SHA256SUMS-windows-$Arch.txt"
$sumLines = @()
$primaryArtifact = $artifacts[-1]
$primaryHash = ""
foreach ($art in $artifacts) {
    $hash = (Get-FileHash -Algorithm SHA256 -Path $art).Hash.ToLowerInvariant()
    $leaf = Split-Path -Leaf $art
    $sumLines += "$hash  $leaf"
    if ($art -eq $primaryArtifact) { $primaryHash = $hash }
}
[System.IO.File]::WriteAllText($sumsPath, ($sumLines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))

$primaryLeaf = Split-Path -Leaf $primaryArtifact
$updateFragment = [ordered]@{
    arch = $Arch
    version = $Version
    build = $BuildNumber
    file = $primaryLeaf
    url = "https://github.com/inokoru/yuyue/releases/download/v$Version/$primaryLeaf"
    sha256 = $primaryHash
    size = (Get-Item $primaryArtifact).Length
} | ConvertTo-Json -Depth 4
$fragPath = Join-Path $DistRoot "windows-update-$Arch.json"
[System.IO.File]::WriteAllText($fragPath, $updateFragment + "`n", [System.Text.UTF8Encoding]::new($false))

Write-Host "Done! Artifacts in $DistRoot :"
foreach ($line in $sumLines) { Write-Host "  $line" }
