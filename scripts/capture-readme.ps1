# README screenshots and short promo clips for the Windows app.
# Run on Windows, from the repo root:
#   pwsh scripts/capture-readme.ps1
# Optional: -Exe C:\Path\Yuyue.exe  -Keep  -Shots  -Clips
# The library is %LOCALAPPDATA%\YuyueReadmeShots and is never the real 语阅 folder.

param(
    [string]$Exe = "",
    [switch]$Keep,
    [switch]$Shots,
    [switch]$Clips
)

$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $true
}
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Py = if ($env:PYTHON) { $env:PYTHON } else { "python" }
if (-not (Get-Command $Py -ErrorAction SilentlyContinue)) { $Py = "py" }
$Lib = Join-Path $env:LOCALAPPDATA "YuyueReadmeShots\library"
$Real = Join-Path $env:LOCALAPPDATA "Yuyue"
if ($Lib.TrimEnd('\') -eq $Real.TrimEnd('\')) { throw "拒绝使用真实书库 $Real" }
$Books = Join-Path $Root "build\readme-shots\books"
$Raw = Join-Path $Root "docs\screenshots\raw\windows"
$ClipDir = Join-Path $Root "docs\screenshots\clips\windows"
$DoShots = $true
$DoClips = $true
if ($Shots -and -not $Clips) { $DoClips = $false }
if ($Clips -and -not $Shots) { $DoShots = $false }

New-Item -ItemType Directory -Force -Path $Raw, $ClipDir, (Split-Path $Lib), $Books | Out-Null

function Invoke-Py {
    & $Py @args
    if ($LASTEXITCODE -ne 0) {
        throw "python 失败（退出码 $LASTEXITCODE）：$($args -join ' ')"
    }
}

# 缺 Pillow 或（要录短片时）缺 ffmpeg 时直接停，避免空书架也被截下来。
# 这里先不要让原生命令的非零退出变成终止错误，好把安装命令印出来。
$ErrorActionPreference = "Continue"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
& $Py -c "import PIL" 2>$null
$pillowOk = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $true
}
if (-not $pillowOk) {
    Write-Host "缺少 Pillow，封面和合成图都要用它。先装上再重跑："
    Write-Host "  python3 -m pip install --user pillow"
    Write-Host "  Windows：py -m pip install pillow"
    exit 1
}
if ($DoClips) {
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
        Write-Host "录短片需要 ffmpeg，当前没有找到。只截图可以加 -Shots。"
        Write-Host "  winget install Gyan.FFmpeg"
        exit 1
    }
}

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class YuyueShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int cb);
  // x64 INPUT is 40 bytes: type at 0, mouse union at 8.
  [StructLayout(LayoutKind.Explicit)] public struct INPUT {
    [FieldOffset(0)] public int type;
    [FieldOffset(8)] public MOUSEINPUT mi;
  }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
    public int dx, dy, mouseData, dwFlags, time; public IntPtr extra;
  }
  const int MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
  const int MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
  const int MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_WHEEL = 0x0800;
  public static IntPtr Find(uint pid) {
    IntPtr best = IntPtr.Zero; long area = 0;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      long w = r.Right - r.Left, ht = r.Bottom - r.Top;
      if (w >= 400 && ht >= 300 && w * ht > area) { area = w * ht; best = h; }
      return true;
    }, IntPtr.Zero);
    return best;
  }
  public static RECT Bounds(IntPtr h) {
    RECT r;
    if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
    return r;
  }
  public static void Place(IntPtr h, int dipW, int dipH) {
    uint dpi = GetDpiForWindow(h); if (dpi == 0) dpi = 96;
    int w = dipW * (int)dpi / 96, ht = dipH * (int)dpi / 96;
    int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
    if (w > sw - 40) w = sw - 40;
    if (ht > sh - 80) ht = sh - 80;
    ShowWindow(h, 9);
    SetWindowPos(h, IntPtr.Zero, 40, 40, w, ht, 0x0040);
    SetForegroundWindow(h);
  }
  public static void Save(IntPtr h, string path) {
    RECT r = Bounds(h);
    int w = r.Right - r.Left, ht = r.Bottom - r.Top;
    if (w < 50 || ht < 50) throw new Exception("window too small");
    using (var bmp = new Bitmap(w, ht)) {
      using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
      bmp.Save(path, ImageFormat.Png);
    }
  }
  static void Send(int dx, int dy, int data, int flags) {
    var inp = new INPUT();
    inp.type = 0;
    inp.mi.dx = dx; inp.mi.dy = dy; inp.mi.mouseData = data; inp.mi.dwFlags = flags;
    SendInput(1, new[]{ inp }, Marshal.SizeOf(typeof(INPUT)));
  }
  static void MoveAbs(int x, int y) {
    int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
    Send(x * 65535 / sw, y * 65535 / sh, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE);
  }
  public static void Click(int x, int y, bool right) {
    MoveAbs(x, y); System.Threading.Thread.Sleep(40);
    Send(0, 0, 0, right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN);
    System.Threading.Thread.Sleep(40);
    Send(0, 0, 0, right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP);
  }
  public static void Drag(int x1, int y1, int x2, int y2) {
    MoveAbs(x1, y1); System.Threading.Thread.Sleep(30);
    Send(0, 0, 0, MOUSEEVENTF_LEFTDOWN);
    for (int i = 1; i <= 12; i++) {
      int x = x1 + (x2 - x1) * i / 12, y = y1 + (y2 - y1) * i / 12;
      MoveAbs(x, y); System.Threading.Thread.Sleep(16);
    }
    Send(0, 0, 0, MOUSEEVENTF_LEFTUP);
  }
  public static void Wheel(int x, int y, int notches) {
    MoveAbs(x, y); Send(0, 0, notches * -120, MOUSEEVENTF_WHEEL);
  }
  public static int[] Rect(IntPtr h) {
    RECT r = Bounds(h);
    return new[]{ r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
  }
}
"@

Add-Type -AssemblyName System.Windows.Forms
function Send-Keys([string]$Keys) {
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
}
function Paste-Text([string]$Text) {
    Set-Clipboard -Value $Text
    Start-Sleep -Milliseconds 150
    Send-Keys "^v"
}

function Find-Yuyue {
    param([string]$Explicit)
    $cands = @()
    if ($Explicit) { $cands += $Explicit }
    $hives = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    foreach ($hive in $hives) {
        Get-ItemProperty $hive -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.DisplayName -match "语阅|Yuyue" -and $_.InstallLocation) {
                $cands += (Join-Path $_.InstallLocation "Yuyue.exe")
            }
        }
    }
    $cands += @(
        "$env:LOCALAPPDATA\Programs\Yuyue\Yuyue.exe",
        "$env:ProgramFiles\Yuyue\Yuyue.exe"
    )
    Get-ChildItem (Join-Path $Root "windows\dist") -Filter Yuyue.exe -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $cands += $_.FullName }
    Get-ChildItem (Join-Path $Root "windows\Yuyue\bin") -Filter Yuyue.exe -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $cands += $_.FullName }
    $found = @()
    foreach ($c in $cands) {
        if ($c -and (Test-Path $c)) {
            $ver = (Get-Item $c).VersionInfo.FileVersion
            $found += [pscustomobject]@{ Path = (Resolve-Path $c).Path; Version = $ver }
        }
    }
    if (-not $found) {
        throw "没有找到 Yuyue.exe。装官方 0.1.7，或先 dotnet build，也可以 -Exe 指向它。"
    }
    $official = $found | Where-Object { $_.Version -like "0.1.7*" -and $_.Path -notmatch "\\bin\\" } | Select-Object -First 1
    if ($official) { return $official }
    $rel = $found | Where-Object { $_.Path -match "Release" } | Select-Object -First 1
    if ($rel) { return $rel }
    return $found[0]
}

function Stop-ReadmeYuyue {
    Get-CimInstance Win32_Process -Filter "Name = 'Yuyue.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -and $_.CommandLine -like "*YuyueReadmeShots*"
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Wait-Books([int]$Need) {
    $deadline = (Get-Date).AddSeconds(100)
    do {
        $n = @(Get-ChildItem (Join-Path $Lib "books") -Recurse -Filter book.json -ErrorAction SilentlyContinue).Count
        if ($n -ge $Need) { return $n }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)
    return @(Get-ChildItem (Join-Path $Lib "books") -Recurse -Filter book.json -ErrorAction SilentlyContinue).Count
}

function Close-App($proc) {
    if (-not $proc) { return }
    try { $proc.CloseMainWindow() | Out-Null } catch {}
    if (-not $proc.WaitForExit(8000)) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 400
}

function Wait-Window($proc, [int]$ms) {
    $deadline = (Get-Date).AddMilliseconds($ms)
    do {
        $h = [YuyueShot]::Find([uint32]$proc.Id)
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "窗口没有出现"
}

function Book-Id([string]$Needle) {
    $code = @"
import json, sys, pathlib
root = pathlib.Path(sys.argv[1]); needle = sys.argv[2].lower()
for p in root.glob('books/*/book.json'):
    title = json.loads(p.read_text(encoding='utf-8')).get('title') or ''
    if needle in title.lower():
        print(p.parent.name); break
"@
    $id = & $Py -c $code $Lib $Needle
    if ($LASTEXITCODE -ne 0) { throw "按书名查找失败：$Needle" }
    return "$id".Trim()
}

function Set-Prefs([string]$OpenId, [int]$W, [int]$H) {
    $arg = @("$Root\scripts\readme-shots.py", "windows-prefs", "--library", $Lib, "--width", "$W", "--height", "$H")
    if ($OpenId) { $arg += @("--open-book", $OpenId) }
    Invoke-Py @arg
}

function Grab($proc, [string]$name, [int]$dipW, [int]$dipH, [int]$waitMs) {
    $h = Wait-Window $proc $waitMs
    [YuyueShot]::Place($h, $dipW, $dipH)
    Start-Sleep -Milliseconds 800
    $h = [YuyueShot]::Find([uint32]$proc.Id)
    $out = Join-Path $Raw "$name.png"
    [YuyueShot]::Save($h, $out)
    Write-Host "截图 → $out"
}

function Record-Clip($proc, [string]$name, [int]$seconds, [scriptblock]$action) {
    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if (-not $ffmpeg) {
        throw "录短片需要 ffmpeg。winget install Gyan.FFmpeg"
    }
    $h = Wait-Window $proc 12000
    [YuyueShot]::Place($h, 1200, 820)
    Start-Sleep -Milliseconds 600
    $h = [YuyueShot]::Find([uint32]$proc.Id)
    $r = [YuyueShot]::Rect($h)
    $out = Join-Path $ClipDir "$name.mp4"
    $ff = Start-Process -FilePath $ffmpeg.Source -ArgumentList @(
        "-y", "-f", "gdigrab", "-framerate", "60", "-draw_mouse", "1",
        "-offset_x", "$($r[0])", "-offset_y", "$($r[1])", "-video_size", "$($r[2])x$($r[3])",
        "-i", "desktop", "-t", "$seconds", "-c:v", "libx264", "-pix_fmt", "yuv420p", $out
    ) -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 400
    if ($action) { & $action $r }
    if (-not $ff.WaitForExit(($seconds + 8) * 1000)) { Stop-Process -Id $ff.Id -Force }
    Write-Host "片段 → $out"
}

Stop-ReadmeYuyue
$chosen = Find-Yuyue $Exe
Write-Host "使用 $($chosen.Path)"
Write-Host "版本 $($chosen.Version)（优先已安装的 0.1.7；没有就用本机 Release / Debug）"
Write-Host "隔离书库 $Lib"

Invoke-Py "$Root\scripts\readme-shots.py" books --out $Books
$bookFiles = @(Get-ChildItem $Books -File)
if (-not $Keep -or -not (Test-Path (Join-Path $Lib "position.json"))) {
    if (Test-Path $Lib) { Remove-Item -Recurse -Force $Lib }
    New-Item -ItemType Directory -Force -Path $Lib | Out-Null
    $argList = @("--library", $Lib) + @($bookFiles | ForEach-Object { $_.FullName })
    Write-Host "导入 $($bookFiles.Count) 本书"
    $proc = Start-Process -FilePath $chosen.Path -ArgumentList $argList -PassThru
    $n = Wait-Books @($bookFiles).Count
    Close-App $proc
    Write-Host "导入完成，书库里有 $n 本"
    if ($n -ne 14) { throw "导入到的书只有 $n 本，要正好 14 本。没有改你的真实书库 $Real" }
    Invoke-Py "$Root\scripts\readme-shots.py" seed --library $Lib
}

function Assert-SeededLibrary {
    $n = @(Get-ChildItem (Join-Path $Lib "books") -Recurse -Filter book.json -ErrorAction SilentlyContinue).Count
    if ($n -ne 14) {
        throw "隔离书库里只有 $n 本书，要正好 14 本才截图或录屏。没有改你的真实书库 $Real"
    }
    Write-Host "隔离书库里有 $n 本"
}
Assert-SeededLibrary

function Launch-Lib {
    return Start-Process -FilePath $chosen.Path -ArgumentList @("--library", $Lib) -PassThru
}

if ($DoShots) {
    Set-Prefs "" 1320 900
    $p = Launch-Lib
    Grab $p "01-shelf" 1320 900 15000
    Close-App $p

    Invoke-Py "$Root\scripts\readme-shots.py" focus --library $Lib --mode notes
    $id = Book-Id "呼兰河传"
    Set-Prefs $id 1200 820
    $p = Launch-Lib
    $h = Wait-Window $p 15000
    [YuyueShot]::Place($h, 1200, 820)
    Start-Sleep -Seconds 5
    Send-Keys " "
    Start-Sleep -Seconds 2
    $h = [YuyueShot]::Find([uint32]$p.Id)
    [YuyueShot]::Save($h, (Join-Path $Raw "02-listening.png"))
    Write-Host "截图 → $(Join-Path $Raw '02-listening.png')"
    Close-App $p

    $p = Launch-Lib
    $h = Wait-Window $p 15000
    [YuyueShot]::Place($h, 1200, 820)
    Start-Sleep -Seconds 5
    # Ctrl+Alt+N opens the notes panel.
    Send-Keys "^%n"
    Start-Sleep -Seconds 1
    $h = [YuyueShot]::Find([uint32]$p.Id)
    [YuyueShot]::Save($h, (Join-Path $Raw "03-notes.png"))
    Write-Host "截图 → $(Join-Path $Raw '03-notes.png')"
    Close-App $p
}

if ($DoClips) {
    Set-Prefs "" 1320 900
    $p = Launch-Lib
    Record-Clip $p "01-shelf-scroll" 5 {
        param($r)
        $x = $r[0] + [int]($r[2] * 0.62); $y = $r[1] + [int]($r[3] * 0.55)
        1..6 | ForEach-Object { [YuyueShot]::Wheel($x, $y, 3); Start-Sleep -Milliseconds 500 }
    }
    Close-App $p

    Invoke-Py "$Root\scripts\readme-shots.py" focus --library $Lib --mode listen
    $id = Book-Id "呼兰河传"
    Set-Prefs $id 1200 820
    $p = Launch-Lib
    Record-Clip $p "02-listen-follow" 9 {
        param($r)
        Start-Sleep -Seconds 1
        $x = $r[0] + [int]($r[2] * 0.42); $y = $r[1] + [int]($r[3] * 0.46)
        [YuyueShot]::Click($x, $y, $true)
        Start-Sleep -Milliseconds 500
        Send-Keys "{ENTER}"
    }
    Close-App $p

    Invoke-Py "$Root\scripts\readme-shots.py" focus --library $Lib --mode notes
    Set-Prefs $id 1200 820
    $p = Launch-Lib
    Record-Clip $p "03-highlight-note" 11 {
        param($r)
        Start-Sleep -Seconds 1
        $x1 = $r[0] + [int]($r[2] * 0.22); $x2 = $r[0] + [int]($r[2] * 0.55)
        $y = $r[1] + [int]($r[3] * 0.42)
        [YuyueShot]::Drag($x1, $y, $x2, $y)
        Start-Sleep -Milliseconds 400
        Send-Keys "1"
        Start-Sleep -Milliseconds 500
        try {
            Add-Type -AssemblyName UIAutomationClient
            Add-Type -AssemblyName UIAutomationTypes
            $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty, "想法")
            $el = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
            if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
        } catch { Write-Host "没有点到「想法」，片段里可能只有划线。" }
        Start-Sleep -Milliseconds 600
        Paste-Text "花是自己醒的。"
        Start-Sleep -Milliseconds 300
        Send-Keys "{ENTER}"
    }
    Close-App $p

    Invoke-Py "$Root\scripts\readme-shots.py" focus --library $Lib --mode same
    Set-Prefs "" 1320 900
    $p = Launch-Lib
    Record-Clip $p "04-open-same-position" 6 {
        param($r)
        Start-Sleep -Milliseconds 800
        $x = $r[0] + [int]($r[2] * 0.42); $y = $r[1] + [int]($r[3] * 0.32)
        [YuyueShot]::Click($x, $y, $false)
        Start-Sleep -Milliseconds 120
        [YuyueShot]::Click($x, $y, $false)
    }
    Close-App $p
    Copy-Item (Join-Path $Lib "position.json") (Join-Path $ClipDir "position.json") -Force -ErrorAction SilentlyContinue
}

Invoke-Py "$Root\scripts\readme-shots.py" compose
Write-Host "截图目录 $Raw"
Write-Host "短片目录 $ClipDir"
Write-Host "真实书库没有动过：$Real"
