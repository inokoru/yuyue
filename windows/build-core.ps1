#!/usr/bin/env pwsh
<#
.SYNOPSIS
Builds the Rust core (yuyue_core.dll), generates C# UniFFI bindings, and syncs
the TypeScript reader bundle + design tokens into windows/Yuyue.

.EXAMPLE
  .\windows\build-core.ps1
  .\windows\build-core.ps1 -Debug
#>
param(
    [switch]$Debug,
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64",
    [switch]$SkipReader,
    [switch]$SkipTokens
)

$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$CoreDir = Join-Path $Root "core"
$WinApp = Join-Path $Root "windows\Yuyue"

# Ensure cargo bin is on PATH
$CargoBin = Join-Path $env:USERPROFILE ".cargo\bin"
if ((Test-Path $CargoBin) -and ($env:Path -notlike "*$CargoBin*")) {
    $env:Path = "$CargoBin;$env:Path"
}

$Profile = if ($Debug) { "dev" } else { "release" }
$OutDir = if ($Debug) { "debug" } else { "release" }
$RustTarget = if ($Arch -eq "arm64") { "aarch64-pc-windows-msvc" } else { "x86_64-pc-windows-msvc" }

if (-not $SkipTokens) {
    Write-Host "▸ Design tokens (design/generate.mjs)"
    node (Join-Path $Root "design\generate.mjs")
    if ($LASTEXITCODE -ne 0) { throw "design/generate.mjs failed" }
}

if (-not $SkipReader) {
    Write-Host "▸ Reader bundle (reader/dist)"
    Push-Location (Join-Path $Root "reader")
    try {
        if (-not (Test-Path "node_modules")) {
            pnpm install --frozen-lockfile
            if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
        }
        pnpm build
        if ($LASTEXITCODE -ne 0) { throw "pnpm build failed" }
    } finally {
        Pop-Location
    }
    $ReaderAssets = Join-Path $WinApp "Assets\Reader"
    New-Item -ItemType Directory -Force -Path $ReaderAssets | Out-Null
    Copy-Item (Join-Path $Root "reader\dist\reader.js") $ReaderAssets -Force
    Copy-Item (Join-Path $Root "reader\dist\reader.css") $ReaderAssets -Force
}

Write-Host "▸ Rust core for $RustTarget ($Profile)"
Push-Location $CoreDir
try {
    cargo rustc -p yuyue-core --lib --crate-type cdylib --profile $Profile --target $RustTarget
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
} finally {
    Pop-Location
}

$DllPath = Join-Path $CoreDir "target\$RustTarget\$OutDir\yuyue_core.dll"
if (-not (Test-Path $DllPath)) {
    throw "Expected native library not found at $DllPath"
}

$NativeDir = Join-Path $WinApp "Native\$Arch"
New-Item -ItemType Directory -Force -Path $NativeDir | Out-Null
Copy-Item $DllPath (Join-Path $NativeDir "yuyue_core.dll") -Force
Write-Host "✓ Copied yuyue_core.dll -> $NativeDir"

# Check uniffi-bindgen-cs (v0.12.0+v0.32.0 matches uniffi 0.32.x in core/yuyue-core)
$BindgenCs = Get-Command "uniffi-bindgen-cs" -ErrorAction SilentlyContinue
if (-not $BindgenCs) {
    Write-Host "▸ Installing uniffi-bindgen-cs (v0.12.0+v0.32.0)..."
    cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.12.0+v0.32.0
    if ($LASTEXITCODE -ne 0) { throw "Failed to install uniffi-bindgen-cs" }
}

Write-Host "▸ Generating C# UniFFI bindings"
$CoreCsDir = Join-Path $WinApp "Core\Generated"
New-Item -ItemType Directory -Force -Path $CoreCsDir | Out-Null
$ConfigToml = Join-Path $Root "windows\uniffi.toml"
Push-Location $CoreDir
try {
    uniffi-bindgen-cs --library --config $ConfigToml --out-dir $CoreCsDir --no-format $DllPath
    if ($LASTEXITCODE -ne 0) { throw "uniffi-bindgen-cs failed" }
} finally {
    Pop-Location
}

# Fix two C# record member name collisions in uniffi-bindgen-cs 0.12.0+0.32.0:
# 1. `record Gloss(string Form, string Gloss)` -> CS0542 (member same name as enclosing type)
# 2. `SyncHit.Gap(uint Section, uint Sentence)` collides with sibling nested type `SyncHit.Sentence` -> CS8866
$GeneratedCs = Join-Path $CoreCsDir "yuyue_core.cs"
$csText = [System.IO.File]::ReadAllText($GeneratedCs)
$csText = $csText.Replace("public record Gloss (`r`n    string Form, `r`n    string Gloss", "public record Gloss (`r`n    string Form, `r`n    string GlossValue")
$csText = $csText.Replace("public record Gloss (`n    string Form, `n    string Gloss", "public record Gloss (`n    string Form, `n    string GlossValue")
$csText = $csText.Replace("Gloss: FfiConverterString.INSTANCE.Read(stream)", "GlossValue: FfiConverterString.INSTANCE.Read(stream)")
$csText = $csText.Replace("AllocationSize(value.Gloss)", "AllocationSize(value.GlossValue)")
$csText = $csText.Replace("Write(value.Gloss, stream)", "Write(value.GlossValue, stream)")
$csText = $csText.Replace("public record Gap (`r`n        uint Section,`r`n        uint Sentence`r`n    ) : SyncHit {}", "public record Gap (`r`n        uint Section,`r`n        uint SentenceValue`r`n    ) : SyncHit {}")
$csText = $csText.Replace("public record Gap (`n        uint Section,`n        uint Sentence`n    ) : SyncHit {}", "public record Gap (`n        uint Section,`n        uint SentenceValue`n    ) : SyncHit {}")
$csText = $csText.Replace("case SyncHit.Gap variant_value:`r`n                return 4`r`n                    + FfiConverterUInt32.INSTANCE.AllocationSize(variant_value.Section)`r`n                    + FfiConverterUInt32.INSTANCE.AllocationSize(variant_value.Sentence);", "case SyncHit.Gap variant_value:`r`n                return 4`r`n                    + FfiConverterUInt32.INSTANCE.AllocationSize(variant_value.Section)`r`n                    + FfiConverterUInt32.INSTANCE.AllocationSize(variant_value.SentenceValue);")
$csText = $csText.Replace("case SyncHit.Gap variant_value:`n                return 4`n                    + FfiConverterUInt32.INSTANCE.AllocationSize(variant_value.Section)`n                    + FfiConverterUInt32.INSTANCE.AllocationSize(variant_value.Sentence);", "case SyncHit.Gap variant_value:`n                return 4`n                    + FfiConverterUInt32.INSTANCE.AllocationSize(variant_value.Section)`n                    + FfiConverterUInt32.INSTANCE.AllocationSize(variant_value.SentenceValue);")
$csText = $csText.Replace("case SyncHit.Gap variant_value:`r`n                stream.WriteInt(3);`r`n                FfiConverterUInt32.INSTANCE.Write(variant_value.Section, stream);`r`n                FfiConverterUInt32.INSTANCE.Write(variant_value.Sentence, stream);", "case SyncHit.Gap variant_value:`r`n                stream.WriteInt(3);`r`n                FfiConverterUInt32.INSTANCE.Write(variant_value.Section, stream);`r`n                FfiConverterUInt32.INSTANCE.Write(variant_value.SentenceValue, stream);")
$csText = $csText.Replace("case SyncHit.Gap variant_value:`n                stream.WriteInt(3);`n                FfiConverterUInt32.INSTANCE.Write(variant_value.Section, stream);`n                FfiConverterUInt32.INSTANCE.Write(variant_value.Sentence, stream);", "case SyncHit.Gap variant_value:`n                stream.WriteInt(3);`n                FfiConverterUInt32.INSTANCE.Write(variant_value.Section, stream);`n                FfiConverterUInt32.INSTANCE.Write(variant_value.SentenceValue, stream);")
[System.IO.File]::WriteAllText($GeneratedCs, $csText, [System.Text.UTF8Encoding]::new($false))

Write-Host "✓ C# bindings generated -> $CoreCsDir"
