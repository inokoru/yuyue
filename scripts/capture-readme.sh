#!/bin/bash
# README screenshots and short promo clips.
#
#   scripts/capture-readme.sh macos
#   scripts/capture-readme.sh ios
#   scripts/capture-readme.sh ipad
#   scripts/capture-readme.sh android
#
# Windows is scripts/capture-readme.ps1. After the shots exist:
#   python3 scripts/readme-shots.py compose
#
# The scratch library lives under build/readme-shots or the simulator's
# container. It is not the real 语阅 library. Pass --keep to reuse a library
# this script already seeded. Pass --shots or --clips to run only that half.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PY="${PYTHON:-python3}"
SCRATCH="$ROOT/build/readme-shots"
BOOKS="$SCRATCH/books"
RAW="$ROOT/docs/screenshots/raw"
CLIPS="$ROOT/docs/screenshots/clips"
KEEP=0
DO_SHOTS=1
DO_CLIPS=1
ALLOW_DEVICE=0

usage() {
  cat <<'EOF'
用法:
  scripts/capture-readme.sh macos
  scripts/capture-readme.sh ios
  scripts/capture-readme.sh ipad
  scripts/capture-readme.sh android [--device]

可选: --keep  沿用已经种好的演示书库
      --shots 只截图
      --clips 只录短片
说明: docs/dev/readme-screenshots.md
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    macos|ios|iphone|ipad|android) CMD="$1" ;;
    --keep) KEEP=1 ;;
    --shots) DO_CLIPS=0 ;;
    --clips) DO_SHOTS=0 ;;
    --device) ALLOW_DEVICE=1 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "不认识的参数: $1"; usage; exit 2 ;;
  esac
  shift
done

if [ -z "${CMD:-}" ]; then usage; exit 2; fi
mkdir -p "$SCRATCH" "$BOOKS" "$RAW" "$CLIPS"

need() {
  command -v "$1" >/dev/null 2>&1 || { echo "缺少 $1"; exit 1; }
}

# Pillow is imported by cover drawing and by compose. Fail here, before the
# app is copied or launched, so a missing module cannot fall through into an
# empty-shelf capture.
preflight() {
  need "$PY"
  if ! "$PY" -c "import PIL" >/dev/null 2>&1; then
    echo "缺少 Pillow，封面和合成图都要用它。先装上再重跑："
    echo "  python3 -m pip install --user pillow"
    if [ "$(uname -s)" = "Darwin" ]; then
      echo "  若 pip 不可用：brew install pillow"
    fi
    exit 1
  fi
  if [ "$DO_CLIPS" = "1" ]; then
    if ! command -v ffmpeg >/dev/null 2>&1; then
      echo "录短片需要 ffmpeg，当前没有找到。只截图可以加 --shots。"
      if [ "$(uname -s)" = "Darwin" ]; then
        echo "  brew install ffmpeg"
      else
        echo "  请先安装 ffmpeg，并确认它在 PATH 里。"
      fi
      exit 1
    fi
  fi
}

prepare_books() {
  mkdir -p "$BOOKS"
  "$PY" "$ROOT/scripts/readme-shots.py" books --out "$BOOKS"
  local n
  n="$(find "$BOOKS" -type f \( -name '*.epub' -o -name '*.pdf' \) | wc -l | tr -d ' ')"
  if [ "${n:-0}" -ne 14 ]; then
    echo "演示书只生成了 ${n:-0} 本，要正好 14 本。没有启动应用。"
    exit 1
  fi
}

count_books() {
  if [ ! -d "$1" ]; then
    echo 0
    return 0
  fi
  find "$1" -name book.json | wc -l | tr -d ' '
}

require_library() {
  local n
  n="$(count_books "$1")"
  if [ "${n:-0}" -ne 14 ]; then
    echo "隔离书库里只有 ${n:-0} 本书，要正好 14 本才截图或录屏。"
    echo "没有改你的真实书库。这是一次没导全的临时书库的话，删掉它再重跑，不要加 --keep。"
    exit 1
  fi
  echo "隔离书库里有 $n 本"
}

preflight

input_bin() {
  if [ ! -x "$SCRATCH/mac-input" ]; then
    need swiftc
    swiftc -O -o "$SCRATCH/mac-input" "$ROOT/scripts/readme_shots/mac-input.swift"
  fi
  echo "$SCRATCH/mac-input"
}

# owner substring, prints x y w h
window_bounds() {
  local bin want line
  bin="$(input_bin)"
  want="$1"
  line="$("$bin" windows | awk -F '\t' -v w="$want" 'index($1, w) { print; exit }')"
  if [ -z "$line" ]; then return 1; fi
  echo "$line" | cut -f2- | tr '\t' ' '
}

# 「Window move completed without beginning」是系统在窗口还没开始移动时
# 就收到一次定位时打的警告，窗口仍会打开，不影响截图。等窗口出现再改大小，
# 避免对一个还不存在的窗口发定位。
resize_process() {
  local proc="$1" w="$2" h="$3"
  osascript <<EOF || true
tell application "System Events"
  if exists process "$proc" then
    tell process "$proc"
      set frontmost to true
      repeat 20 times
        if (count of windows) > 0 then exit repeat
        delay 0.3
      end repeat
      if (count of windows) > 0 then
        try
          set position of window 1 to {70, 50}
          set size of window 1 to {$w, $h}
        end try
      end if
    end tell
  end if
end tell
EOF
}

click_menu() {
  local name="$1" proc="${2:-Yuyue}"
  osascript <<EOF
tell application "System Events"
  tell process "$proc"
    if exists menu item "$name" of menu 1 then
      click menu item "$name" of menu 1
      return "ok"
    end if
  end tell
end tell
return "missing"
EOF
}

record_mac_window() {
  local out="$1" seconds="$2" cursor="$3" owner="$4"
  local x y w h pid
  set -- $(window_bounds "$owner") || { echo "找不到 $owner 的窗口"; return 1; }
  x="$1"; y="$2"; w="$3"; h="$4"
  mkdir -p "$(dirname "$out")"
  # screencapture -v records until SIGINT. -C keeps the cursor in the clip.
  if [ "$cursor" = "1" ]; then
    screencapture -C -v -R "${x},${y},${w},${h}" "$out" &
  else
    screencapture -v -R "${x},${y},${w},${h}" "$out" &
  fi
  pid=$!
  sleep "$seconds"
  kill -INT "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  echo "片段 → $out"
}

rel_point() {
  local owner="$1" fx="$2" fy="$3"
  local x y w h
  set -- $(window_bounds "$owner") || return 1
  x="$1"; y="$2"; w="$3"; h="$4"
  "$PY" -c "print(int($x + $w * $fx)); print(int($y + $h * $fy))"
}

# ---------- macOS app ----------
macos_app() {
  local c
  if [ -n "${APP:-}" ] && [ -d "$APP" ]; then echo "$APP"; return; fi
  for c in \
    "$ROOT/apple/build/dd/Build/Products/Debug/Yuyue.app" \
    "$ROOT/apple/build/Build/Products/Debug/Yuyue.app"
  do
    if [ -d "$c" ]; then echo "$c"; return; fi
  done
  cat <<EOF
没有找到 Debug 版 Yuyue.app。在仓库里先编一次（只要 Debug 才有隔离书库和截图参数）:

  cd apple && xcodegen
  xcodebuild -project Yuyue.xcodeproj -scheme Yuyue -configuration Debug \\
    -destination 'platform=macOS' -derivedDataPath build/dd build

也可以 APP=/path/Yuyue.app scripts/capture-readme.sh macos
EOF
  exit 1
}

resign_catalog() {
  local src="$1" dest="$SCRATCH/YuyueCatalog.app"
  rm -rf "$dest"
  cp -R "$src" "$dest"
  /usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier app.yuyue.YuyueCatalog" "$dest/Contents/Info.plist"
  cat > "$SCRATCH/no-sandbox.entitlements" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.security.files.user-selected.read-write</key><true/>
  <key>com.apple.security.network.client</key><true/>
</dict></plist>
EOF
  codesign --force --sign - --entitlements "$SCRATCH/no-sandbox.entitlements" "$dest"
  echo "$dest"
}

run_macos() {
  local style
  style="$(defaults read -g AppleInterfaceStyle 2>/dev/null || echo Light)"
  if [ "$style" = "Dark" ]; then
    echo "现在是深色外观。README 要浅色，请先把系统外观改成浅色再跑。"
    exit 1
  fi
  need codesign
  prepare_books
  local src dest bin lib shots n expect
  src="$(macos_app)"
  dest="$(resign_catalog "$src")"
  bin="$dest/Contents/MacOS/Yuyue"
  lib="$SCRATCH/library"
  shots="$SCRATCH/shots"
  mkdir -p "$shots" "$RAW/macos" "$CLIPS/macos"
  if [ "$KEEP" != "1" ] || [ ! -f "$lib/position.json" ]; then
    rm -rf "$lib"
    mkdir -p "$lib"
    local joined="" f
    for f in "$BOOKS"/*; do
      [ -n "$joined" ] && joined="$joined|$f" || joined="$f"
    done
    expect="$(find "$BOOKS" -type f \( -name '*.epub' -o -name '*.pdf' \) | wc -l | tr -d ' ')"
    echo "导入 $expect 本书到隔离书库 $lib"
    "$bin" -debugLibrary "$lib" -debugShots "$shots" -debugShotName _import -debugShotDelay 36 \
      -debugImport "$joined" -AppleLanguages "(zh-Hans)" -AppleLocale "zh_CN"
    "$PY" "$ROOT/scripts/readme-shots.py" seed --library "$lib"
  fi
  require_library "$lib/books"
  shot() { # name delay w h extra-args...
    local name="$1" delay="$2" w="$3" h="$4"
    shift 4
    rm -rf "$shots/$name"
    "$bin" -debugLibrary "$lib" -debugShots "$shots" -debugShotName "$name" -debugShotDelay "$delay" \
      -AppleLanguages "(zh-Hans)" -AppleLocale "zh_CN" "$@" &
    local pid=$!
    sleep 2
    resize_process "Yuyue" "$w" "$h"
    wait "$pid" || true
    if [ -f "$shots/$name/1.png" ]; then
      cp "$shots/$name/1.png" "$RAW/macos/$name.png"
      echo "截图 → $RAW/macos/$name.png"
    else
      echo "没有截到 $name，停止。没有改你的真实书库。"
      exit 1
    fi
  }
  if [ "$DO_SHOTS" = "1" ]; then
    "$PY" "$ROOT/scripts/readme-shots.py" focus --library "$lib" --mode notes
    shot 01-shelf 6 1320 900
    shot 02-listening 7 1200 820 -debugOpen 呼兰河传 -debugListen 1
    shot 03-notes 7 1200 820 -debugOpen 呼兰河传 -debugPanel notes -debugSeed 1
    shot 04-english 7 1200 820 -debugOpen Walden
  fi
  if [ "$DO_CLIPS" = "1" ]; then
    local input fx fy px py
    input="$(input_bin)"
    fx="${YY_TAP_SENTENCE_X:-0.36}"
    fy="${YY_TAP_SENTENCE_Y:-0.46}"
    # Shelf scroll.
    "$bin" -debugLibrary "$lib" -AppleLanguages "(zh-Hans)" &
    sleep 3
    resize_process "Yuyue" 1320 900
    sleep 1
    record_mac_window "$CLIPS/macos/01-shelf-scroll.mov" 5 1 "Yuyue" &
    local rec=$!
    sleep 0.8
    set -- $(window_bounds "Yuyue" || true)
    if [ -n "${1:-}" ]; then
      px="$($PY -c "print(int($1 + $3 * 0.62))")"
      py="$($PY -c "print(int($2 + $4 * 0.55))")"
      "$input" move "$px" "$py"
      i=0
      while [ "$i" -lt 6 ]; do
        "$input" scroll "$px" "$py" -5
        sleep 0.5
        i=$((i + 1))
      done
    fi
    wait "$rec" || true
    pkill -f "$bin" 2>/dev/null || true
    sleep 1

    "$PY" "$ROOT/scripts/readme-shots.py" focus --library "$lib" --mode listen
    "$bin" -debugLibrary "$lib" -debugOpen 呼兰河传 -AppleLanguages "(zh-Hans)" &
    sleep 6
    resize_process "Yuyue" 1200 820
    record_mac_window "$CLIPS/macos/02-listen-follow.mov" 9 1 "Yuyue" &
    rec=$!
    sleep 1
    set -- $(rel_point "Yuyue" "$fx" "$fy" || true)
    if [ -n "${1:-}" ]; then
      "$input" right "$1" "$2"
      sleep 0.5
      click_menu "从这句读" || click_menu "从这听" || true
    fi
    wait "$rec" || true
    pkill -f "$bin" 2>/dev/null || true
    sleep 1

    "$PY" "$ROOT/scripts/readme-shots.py" focus --library "$lib" --mode notes
    "$bin" -debugLibrary "$lib" -debugOpen 呼兰河传 -AppleLanguages "(zh-Hans)" &
    sleep 6
    resize_process "Yuyue" 1200 820
    record_mac_window "$CLIPS/macos/03-highlight-note.mov" 11 1 "Yuyue" &
    rec=$!
    sleep 1
    set -- $(window_bounds "Yuyue" || true)
    if [ -n "${1:-}" ]; then
      local x1 y1 x2 y2
      x1="$($PY -c "print(int($1 + $3 * 0.22))")"
      y1="$($PY -c "print(int($2 + $4 * 0.42))")"
      x2="$($PY -c "print(int($1 + $3 * 0.48))")"
      y2="$y1"
      "$input" drag "$x1" "$y1" "$x2" "$y2"
      sleep 0.4
      "$input" key 1
      sleep 0.6
      click_menu "想法" || click_menu "想法…" || true
      sleep 0.6
      "$input" type "花是自己醒的。"
      sleep 0.4
      "$input" key return
    fi
    wait "$rec" || true
    pkill -f "$bin" 2>/dev/null || true
    sleep 1

    "$PY" "$ROOT/scripts/readme-shots.py" focus --library "$lib" --mode same
    "$bin" -debugLibrary "$lib" -AppleLanguages "(zh-Hans)" &
    sleep 3
    resize_process "Yuyue" 1320 900
    record_mac_window "$CLIPS/macos/04-open-same-position.mov" 6 1 "Yuyue" &
    rec=$!
    sleep 1
    osascript <<'EOF' || true
tell application "System Events"
  tell process "Yuyue"
    set frontmost to true
    set hits to (every static text of window 1 whose value contains "呼兰河传")
    if (count of hits) > 0 then click item 1 of hits
  end tell
end tell
EOF
    wait "$rec" || true
    pkill -f "$bin" 2>/dev/null || true
    cp "$lib/position.json" "$CLIPS/macos/position.json" 2>/dev/null || true
    echo "短片在 $CLIPS/macos/"
  fi
  "$PY" "$ROOT/scripts/readme-shots.py" compose
}

# ---------- iOS / iPad simulator ----------
pick_sim() {
  local kind="$1" name line udid
  if [ "$kind" = "ipad" ]; then
    set -- "iPad Pro 11-inch (M4)" "iPad Pro 11-inch (M3)" "iPad Pro 11-inch" "iPad Air 11-inch (M2)" "iPad (10th generation)" "iPad"
  else
    set -- "iPhone 16" "iPhone 16 Pro" "iPhone 15" "iPhone 15 Pro" "iPhone"
  fi
  for name in "$@"; do
    line="$(xcrun simctl list devices available | grep -F "$name (" | head -1 || true)"
    if [ -n "$line" ]; then
      udid="$(echo "$line" | sed -n 's/.*(\([A-F0-9-]*\)) (.*/\1/p')"
      if [ -n "$udid" ]; then echo "$udid"; return; fi
    fi
  done
  echo "没有可用的 $kind 模拟器" >&2
  return 1
}

sim_app() {
  if [ -n "${APP:-}" ] && [ -d "$APP" ]; then echo "$APP"; return; fi
  local c
  for c in \
    "$ROOT/apple/build/sim/Build/Products/Debug-iphonesimulator/Yuyue.app" \
    "$ROOT/apple/build/Build/Products/Debug-iphonesimulator/Yuyue.app"
  do
    if [ -d "$c" ]; then echo "$c"; return; fi
  done
  cat <<EOF
没有找到模拟器用的 Debug Yuyue.app。先编一次:

  cd apple && xcodegen
  xcodebuild -project Yuyue.xcodeproj -scheme Yuyue -configuration Debug \\
    -destination 'platform=iOS Simulator,name=iPhone 16' -derivedDataPath build/sim build

或 APP=/path/Yuyue.app scripts/capture-readme.sh ios
EOF
  exit 1
}

run_sim() {
  local kind="$1" folder="$2"
  need xcrun
  prepare_books
  local udid app container lib docs n
  udid="$(pick_sim "$kind")"
  app="$(sim_app)"
  echo "模拟器 $udid"
  xcrun simctl boot "$udid" 2>/dev/null || true
  xcrun simctl bootstatus "$udid" -b
  open -a Simulator --args -CurrentDeviceUDID "$udid" || true
  xcrun simctl ui "$udid" appearance light || true
  xcrun simctl status_bar "$udid" override --time "9:41" --batteryState charged --batteryLevel 100 --wifiBars 3 --cellularMode active --cellularBars 4 || true
  local touches_prev
  touches_prev="$(defaults read com.apple.iphonesimulator ShowSingleTouches 2>/dev/null || echo 0)"
  defaults write com.apple.iphonesimulator ShowSingleTouches -bool YES
  xcrun simctl install "$udid" "$app"
  xcrun simctl terminate "$udid" app.yuyue.Yuyue 2>/dev/null || true
  xcrun simctl launch "$udid" app.yuyue.Yuyue -debugLibrary '~/readme-lib' -AppleLanguages "(zh-Hans)" -AppleLocale "zh_CN" || true
  sleep 2
  xcrun simctl terminate "$udid" app.yuyue.Yuyue 2>/dev/null || true
  container="$(xcrun simctl get_app_container "$udid" app.yuyue.Yuyue data)"
  lib="$container/readme-lib"
  docs="$container/Documents/readme-books"
  if [ "$KEEP" != "1" ] || [ ! -f "$lib/position.json" ]; then
    rm -rf "$lib" "$docs"
    mkdir -p "$docs" "$lib"
    cp "$BOOKS"/* "$docs"/
    xcrun simctl launch "$udid" app.yuyue.Yuyue -debugLibrary '~/readme-lib' -debugImportDir readme-books \
      -AppleLanguages "(zh-Hans)" -AppleLocale "zh_CN" || true
    local i=0
    n=0
    while [ "$i" -lt 45 ]; do
      n="$(count_books "$lib/books")"
      if [ "$n" -ge 14 ]; then break; fi
      sleep 1
      i=$((i + 1))
    done
    xcrun simctl terminate "$udid" app.yuyue.Yuyue 2>/dev/null || true
    "$PY" "$ROOT/scripts/readme-shots.py" seed --library "$lib"
  fi
  require_library "$lib/books"
  mkdir -p "$RAW/$folder" "$CLIPS/$folder"
  sim_shot() {
    local name="$1"; shift
    xcrun simctl terminate "$udid" app.yuyue.Yuyue 2>/dev/null || true
    sleep 0.5
    xcrun simctl launch "$udid" app.yuyue.Yuyue -debugLibrary '~/readme-lib' -AppleLanguages "(zh-Hans)" -AppleLocale "zh_CN" "$@" || true
    sleep 6
    xcrun simctl io "$udid" screenshot "$RAW/$folder/$name.png"
    echo "截图 → $RAW/$folder/$name.png"
    xcrun simctl terminate "$udid" app.yuyue.Yuyue 2>/dev/null || true
  }
  if [ "$DO_SHOTS" = "1" ]; then
    "$PY" "$ROOT/scripts/readme-shots.py" focus --library "$lib" --mode notes
    sim_shot 01-shelf
    sim_shot 02-reading -debugOpen 呼兰河传 -debugSheet chrome
    sim_shot 03-listening -debugOpen 呼兰河传 -debugSheet listen
    sim_shot 04-notes -debugTab notes
  fi
  if [ "$DO_CLIPS" = "1" ]; then
    local input
    input="$(input_bin)"
    sim_clip() {
      local name="$1"
      local out="$CLIPS/$folder/$name.mp4"
      if xcrun simctl io help recordVideo 2>&1 | grep -q -- --fps; then
        xcrun simctl io "$udid" recordVideo --fps 60 --force "$out" &
      else
        xcrun simctl io "$udid" recordVideo --force "$out" &
      fi
      echo $! > "$SCRATCH/simrec.pid"
      sleep 0.8
    }
    sim_clip_stop() {
      if [ -f "$SCRATCH/simrec.pid" ]; then
        kill -INT "$(cat "$SCRATCH/simrec.pid")" 2>/dev/null || true
        wait "$(cat "$SCRATCH/simrec.pid")" 2>/dev/null || true
        rm -f "$SCRATCH/simrec.pid"
      fi
    }
    sim_tap() {
      local fx="$1" fy="$2"
      set -- $(window_bounds "Simulator" || true)
      if [ -z "${1:-}" ]; then echo "找不到模拟器窗口"; return; fi
      local px py
      px="$($PY -c "print(int($1 + $3 * $fx))")"
      py="$($PY -c "print(int($2 + $4 * $fy))")"
      "$input" click "$px" "$py"
    }
    xcrun simctl launch "$udid" app.yuyue.Yuyue -debugLibrary '~/readme-lib' -AppleLanguages "(zh-Hans)" || true
    sleep 4
    sim_clip 01-shelf-scroll 5
    sim_tap "${YY_TAP_BOOK_X:-0.5}" 0.72
    sleep 0.3
    # swipe up the shelf
    set -- $(window_bounds "Simulator" || true)
    if [ -n "${1:-}" ]; then
      "$input" drag "$( $PY -c "print(int($1 + $3 * 0.5))" )" "$( $PY -c "print(int($2 + $4 * 0.72))" )" \
        "$( $PY -c "print(int($1 + $3 * 0.5))" )" "$( $PY -c "print(int($2 + $4 * 0.38))" )"
    fi
    sleep 3
    sim_clip_stop
    xcrun simctl terminate "$udid" app.yuyue.Yuyue 2>/dev/null || true

    "$PY" "$ROOT/scripts/readme-shots.py" focus --library "$lib" --mode listen
    xcrun simctl launch "$udid" app.yuyue.Yuyue -debugLibrary '~/readme-lib' -debugOpen 呼兰河传 -AppleLanguages "(zh-Hans)" || true
    sleep 6
    sim_clip 02-listen-follow 9
    sim_tap "${YY_TAP_SENTENCE_X:-0.5}" "${YY_TAP_SENTENCE_Y:-0.42}"
    sleep 0.4
    # long-press then the 从这听 chip, a little under the line
    set -- $(window_bounds "Simulator" || true)
    if [ -n "${1:-}" ]; then
      "$input" hold "$( $PY -c "print(int($1 + $3 * ${YY_TAP_SENTENCE_X:-0.5}))" )" \
        "$( $PY -c "print(int($2 + $4 * ${YY_TAP_SENTENCE_Y:-0.42}))" )" 0.7
      sleep 0.5
      sim_tap 0.62 0.55
    fi
    sleep 6
    sim_clip_stop
    xcrun simctl terminate "$udid" app.yuyue.Yuyue 2>/dev/null || true

    "$PY" "$ROOT/scripts/readme-shots.py" focus --library "$lib" --mode notes
    xcrun simctl launch "$udid" app.yuyue.Yuyue -debugLibrary '~/readme-lib' -debugOpen 呼兰河传 -AppleLanguages "(zh-Hans)" || true
    sleep 6
    sim_clip 03-highlight-note 11
    set -- $(window_bounds "Simulator" || true)
    if [ -n "${1:-}" ]; then
      "$input" drag "$( $PY -c "print(int($1 + $3 * 0.18))" )" "$( $PY -c "print(int($2 + $4 * 0.40))" )" \
        "$( $PY -c "print(int($1 + $3 * 0.82))" )" "$( $PY -c "print(int($2 + $4 * 0.40))" )"
      sleep 0.6
      sim_tap 0.28 0.62
      sleep 0.5
      "$input" type "花是自己醒的。"
      sleep 0.3
      "$input" key return
    fi
    sleep 4
    sim_clip_stop
    xcrun simctl terminate "$udid" app.yuyue.Yuyue 2>/dev/null || true

    "$PY" "$ROOT/scripts/readme-shots.py" focus --library "$lib" --mode same
    xcrun simctl launch "$udid" app.yuyue.Yuyue -debugLibrary '~/readme-lib' -AppleLanguages "(zh-Hans)" || true
    sleep 4
    sim_clip 04-open-same-position 6
    sim_tap "${YY_TAP_BOOK_X:-0.28}" "${YY_TAP_BOOK_Y:-0.34}"
    sleep 4
    sim_clip_stop
    xcrun simctl terminate "$udid" app.yuyue.Yuyue 2>/dev/null || true
    cp "$lib/position.json" "$CLIPS/$folder/position.json" 2>/dev/null || true
    echo "短片在 $CLIPS/$folder/"
  fi
  if [ "$touches_prev" = "0" ] || [ "$touches_prev" = "NO" ]; then
    defaults write com.apple.iphonesimulator ShowSingleTouches -bool NO
  fi
  xcrun simctl status_bar "$udid" clear >/dev/null 2>&1 || true
  "$PY" "$ROOT/scripts/readme-shots.py" compose
}

# ---------- Android ----------
is_emulator() {
  local q
  q="$(adb shell getprop ro.boot.qemu 2>/dev/null | tr -d '\r')"
  [ "$q" = "1" ] && return 0
  q="$(adb shell getprop ro.kernel.qemu 2>/dev/null | tr -d '\r')"
  [ "$q" = "1" ] && return 0
  adb shell getprop ro.product.model 2>/dev/null | grep -qi 'sdk\|emulator\|Android SDK' && return 0
  return 1
}

run_android() {
  need adb
  prepare_books
  adb start-server >/dev/null
  if ! adb get-state >/dev/null 2>&1; then
    echo "没有在线的 adb 设备。先开一个模拟器，或接上手机并允许调试。"
    exit 1
  fi
  if ! is_emulator && [ "$ALLOW_DEVICE" != "1" ]; then
    echo "这是真机。请改用模拟器。若确定要用这台机器，加上 --device：脚本会先备份 app 私有目录，拍完还原，不会清空别的数据。"
    exit 1
  fi
  if ! adb shell run-as app.yuyue true >/dev/null 2>&1; then
    echo "run-as app.yuyue 失败。需要已安装的 Debug 包（debuggable）。没有改动应用数据。"
    echo "  cd android && ./gradlew :app:assembleDebug && adb install -r app/build/outputs/apk/debug/app-debug.apk"
    exit 1
  fi
  ANDROID_BACKUP="$SCRATCH/android-backup.tar"
  ANDROID_HAD=0
  mkdir -p "$SCRATCH"
  if adb shell run-as app.yuyue ls files/Yuyue >/dev/null 2>&1; then
    ANDROID_HAD=1
    adb exec-out run-as app.yuyue tar -C files -cf - Yuyue > "$ANDROID_BACKUP"
    echo "已备份原来的应用数据 → $ANDROID_BACKUP"
  fi
  restore_android() {
    adb shell am force-stop app.yuyue >/dev/null 2>&1 || true
    if [ "${ANDROID_HAD:-0}" = "1" ] && [ -s "${ANDROID_BACKUP:-}" ]; then
      adb push "$ANDROID_BACKUP" /data/local/tmp/yuyue-readme-restore.tar >/dev/null
      adb shell run-as app.yuyue sh -c 'cd files && rm -rf Yuyue && tar xf /data/local/tmp/yuyue-readme-restore.tar'
      echo "已还原原来的应用数据"
    else
      adb shell run-as app.yuyue rm -rf files/Yuyue >/dev/null 2>&1 || true
    fi
    adb shell rm -rf /data/local/tmp/yuyue-readme /data/local/tmp/yuyue-readme-restore.tar >/dev/null 2>&1 || true
  }
  trap restore_android EXIT
  local night
  night="$(adb shell cmd uimode night 2>/dev/null | tr -d '\r' || echo yes)"
  adb shell cmd uimode night no >/dev/null 2>&1 || true
  adb shell am force-stop app.yuyue >/dev/null 2>&1 || true
  adb shell run-as app.yuyue rm -rf files/Yuyue
  adb shell mkdir -p /data/local/tmp/yuyue-readme
  local f base
  for f in "$BOOKS"/*; do
    base="$(basename "$f")"
    adb push "$f" "/data/local/tmp/yuyue-readme/$base" >/dev/null
    adb shell am force-stop app.yuyue >/dev/null 2>&1 || true
    adb shell am start -n app.yuyue/.MainActivity --es importPath "/data/local/tmp/yuyue-readme/$base" >/dev/null
    sleep 2
  done
  sleep 2
  adb shell am force-stop app.yuyue
  rm -rf "$SCRATCH/android-lib"
  mkdir -p "$SCRATCH/android-lib"
  adb exec-out run-as app.yuyue tar -C files/Yuyue -cf - . > "$SCRATCH/android-lib.tar"
  tar -C "$SCRATCH/android-lib" -xf "$SCRATCH/android-lib.tar"
  "$PY" "$ROOT/scripts/readme-shots.py" seed --library "$SCRATCH/android-lib"
  require_library "$SCRATCH/android-lib/books"
  local id
  id="$("$PY" -c "import json;print(json.load(open('$SCRATCH/android-lib/position.json'))['bookId'])")"
  rm -f "$SCRATCH/android-back.tar"
  tar -C "$SCRATCH/android-lib" -cf "$SCRATCH/android-back.tar" .
  adb push "$SCRATCH/android-back.tar" /data/local/tmp/yuyue-lib.tar >/dev/null
  adb shell run-as app.yuyue sh -c 'cd files/Yuyue && tar xf /data/local/tmp/yuyue-lib.tar'
  mkdir -p "$RAW/android" "$CLIPS/android"
  adb shell am start -n app.yuyue/.MainActivity >/dev/null
  sleep 4
  if [ "$DO_SHOTS" = "1" ]; then
    adb exec-out screencap -p > "$RAW/android/01-shelf.png"
    echo "截图 → $RAW/android/01-shelf.png"
    adb shell am force-stop app.yuyue
    adb shell am start -n app.yuyue/.MainActivity --es listen "${id},0,1" >/dev/null
    sleep 5
    adb exec-out screencap -p > "$RAW/android/03-listening.png"
    echo "截图 → $RAW/android/03-listening.png"
  fi
  if [ "$DO_CLIPS" = "1" ]; then
    local size w h
    size="$(adb shell wm size | tr -d '\r' | awk -F '[ x]' '/Physical size/{print $(NF-1), $NF}')"
    w="${size% *}"; h="${size#* }"
    rec_and() {
      local name="$1" seconds="$2"
      adb shell screenrecord --time-limit "$seconds" --bit-rate 12000000 "/sdcard/$name.mp4" &
      echo $! > "$SCRATCH/adrec.pid"
      sleep 0.6
    }
    rec_and_pull() {
      local name="$1"
      wait "$(cat "$SCRATCH/adrec.pid")" 2>/dev/null || true
      adb pull "/sdcard/$name.mp4" "$CLIPS/android/$name.mp4" >/dev/null
      adb shell rm -f "/sdcard/$name.mp4"
      echo "片段 → $CLIPS/android/$name.mp4"
    }
    tapf() {
      local x="$1" y="$2"
      x="$("$PY" -c "v=float('$x'); print(int(round(v*100)) if v<=1 else int(v))")"
      y="$("$PY" -c "v=float('$y'); print(int(round(v*100)) if v<=1 else int(v))")"
      adb shell input tap $((w * x / 100)) $((h * y / 100))
    }
    adb shell am force-stop app.yuyue
    adb shell am start -n app.yuyue/.MainActivity >/dev/null
    sleep 3
    rec_and 01-shelf-scroll 5
    adb shell input swipe $((w / 2)) $((h * 70 / 100)) $((w / 2)) $((h * 35 / 100)) 700
    rec_and_pull 01-shelf-scroll
    local lsec lsent
    lsec="$("$PY" -c "import json;print(json.load(open('$SCRATCH/android-lib/position.json'))['listen']['section'])")"
    lsent="$("$PY" -c "import json;print(json.load(open('$SCRATCH/android-lib/position.json'))['listen']['sentence'])")"
    adb shell am force-stop app.yuyue
    adb shell am start -n app.yuyue/.MainActivity --es listen "${id},${lsec},${lsent}" >/dev/null
    sleep 4
    rec_and 02-listen-follow 8
    rec_and_pull 02-listen-follow
    "$PY" "$ROOT/scripts/readme-shots.py" focus --library "$SCRATCH/android-lib" --mode same
    tar -C "$SCRATCH/android-lib" -cf "$SCRATCH/android-back.tar" .
    adb push "$SCRATCH/android-back.tar" /data/local/tmp/yuyue-lib.tar >/dev/null
    adb shell am force-stop app.yuyue
    adb shell run-as app.yuyue sh -c 'cd files/Yuyue && tar xf /data/local/tmp/yuyue-lib.tar'
    adb shell am start -n app.yuyue/.MainActivity >/dev/null
    sleep 3
    rec_and 04-open-same-position 6
    tapf "${YY_TAP_BOOK_X:-28}" "${YY_TAP_BOOK_Y:-34}"
    rec_and_pull 04-open-same-position
    cp "$SCRATCH/android-lib/position.json" "$CLIPS/android/position.json" 2>/dev/null || true
  fi
  adb shell cmd uimode night "$night" >/dev/null 2>&1 || true
  "$PY" "$ROOT/scripts/readme-shots.py" compose
  # trap restores the private directory
}

case "$CMD" in
  macos) run_macos ;;
  ios|iphone) run_sim iphone iphone ;;
  ipad) run_sim ipad ipad ;;
  android) run_android ;;
esac
echo "输出: 截图 $RAW  短片 $CLIPS"
echo "合成: python3 scripts/readme-shots.py compose"
