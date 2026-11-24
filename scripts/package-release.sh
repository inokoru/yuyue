#!/bin/bash
# 打包发布文件到 dist/：Mac 的 .dmg（Apple 芯片 + Intel）、iPhone 未签名的 .ipa、Android 的 .apk。
# 用法：scripts/package-release.sh [mac | mac-arm64 | mac-x86_64] [ios] [android]     不写就全打
#
# Mac 和 iPhone 不用开发者证书：Mac 是临时签名，.ipa 由用户安装时自己签。
# Android 要 android/keystore.properties。没有的话用调试密钥签，文件名带 -test，只能自己试，
# 不能发布：换了密钥，用户就没法覆盖升级。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="$ROOT/dist"
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
# Rust 会把源文件的绝对路径编进程序，换掉，发布文件里就没有本机的用户名和目录。
export RUSTFLAGS="${RUSTFLAGS:-} --remap-path-prefix=$HOME=~ --remap-path-prefix=$ROOT=."

want=("$@")
[ ${#want[@]} -eq 0 ] && want=(mac ios android)
wants() { [[ " ${want[*]} " == *" $1 "* ]]; }
wants_mac_arm64() { wants mac || wants mac-arm64; }
wants_mac_x86_64() { wants mac || wants mac-x86_64 || wants mac-intel; }
wants_any_mac() { wants_mac_arm64 || wants_mac_x86_64; }

VERSION="$(sed -n 's/^ *MARKETING_VERSION: *//p' "$ROOT/apple/project.yml" | head -1)"
ANDROID_VERSION="$(sed -n 's/^ *versionName = "\(.*\)"/\1/p' "$ROOT/android/app/build.gradle.kts" | head -1)"
if [ "$VERSION" != "$ANDROID_VERSION" ]; then
  echo "版本不一致：apple/project.yml 是 ${VERSION}，android/app/build.gradle.kts 是 $ANDROID_VERSION" >&2
  exit 1
fi
mkdir -p "$DIST"

if wants_any_mac || wants ios || wants android; then
  (cd "$ROOT/reader" && node scripts/build.mjs)
fi

if wants ios; then
  "$ROOT/apple/build-core.sh"
  (cd "$ROOT/apple" && xcodegen generate --quiet)
elif wants_any_mac; then
  "$ROOT/apple/build-core.sh" --mac
  (cd "$ROOT/apple" && xcodegen generate --quiet)
fi

apple_build() { # destination derived-data-folder [settings…]
  local destination="$1" derived="$2"
  shift 2
  xcodebuild -project "$ROOT/apple/Yuyue.xcodeproj" -scheme Yuyue -configuration Release \
    -destination "$destination" -derivedDataPath "$derived" "$@" build -quiet
}

mac_dmg() { # arch derived-data-folder
  local arch="$1" derived="$2"
  apple_build 'generic/platform=macOS' "$derived" "ARCHS=$arch" ONLY_ACTIVE_ARCH=NO
  local stage out
  stage="$(mktemp -d)"
  ditto "$derived/Build/Products/Release/Yuyue.app" "$stage/Yuyue.app"
  ln -s /Applications "$stage/Applications"
  out="$DIST/Yuyue-$VERSION-mac-$arch.dmg"
  rm -f "$out"
  hdiutil create -quiet -volname "语阅 $VERSION" -srcfolder "$stage" -format UDZO "$out"
  rm -rf "$stage"
  echo "$out"
}

if wants_mac_arm64; then
  mac_dmg arm64 "$ROOT/apple/build/release-mac"
fi

if wants_mac_x86_64; then
  mac_dmg x86_64 "$ROOT/apple/build/release-mac-x86_64"
fi

if wants ios; then
  derived="$ROOT/apple/build/release-ios"
  # SIDELOAD: 自己签名装的版本自己查更新（App Store 版不查）。
  apple_build 'generic/platform=iOS' "$derived" CODE_SIGNING_ALLOWED=NO 'SWIFT_ACTIVE_COMPILATION_CONDITIONS=$(inherited) SIDELOAD'
  stage="$(mktemp -d)"
  mkdir "$stage/Payload"
  ditto "$derived/Build/Products/Release-iphoneos/Yuyue.app" "$stage/Payload/Yuyue.app"
  out="$DIST/Yuyue-$VERSION-ios-unsigned.ipa"
  rm -f "$out"
  (cd "$stage" && zip -qry "$out" Payload)
  rm -rf "$stage"
  echo "$out"
fi

if wants android; then
  "$ROOT/android/build-core.sh"
  (cd "$ROOT/android" && ./gradlew --quiet :app:assembleRelease)
  suffix=""
  if [ ! -f "$ROOT/android/keystore.properties" ]; then
    suffix="-test"
    echo "没有 android/keystore.properties：这个 APK 用调试密钥签名，不能发布。" >&2
  fi
  out="$DIST/Yuyue-$VERSION-android$suffix.apk"
  cp "$ROOT/android/app/build/outputs/apk/release/app-release.apk" "$out"
  echo "$out"
fi
