#!/bin/bash
# Builds the Rust core for Apple platforms as the local Swift package apple/YuyueCore:
#
#   YuyueCoreFFI.xcframework           the static library for Mac, iPhone/iPad and the simulator
#   Sources/YuyueCore/yuyue_core.swift  the Swift side of the bindings (UniFFI)
#
# Usage: apple/build-core.sh [--debug] [--mac | --mac-arm64 | --mac-x86_64]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CORE="$ROOT/core"
PACKAGE="$ROOT/apple/YuyueCore"
BUILD="$CORE/target/apple"

PROFILE=release
MAC_TARGETS=(aarch64-apple-darwin x86_64-apple-darwin)
IOS_TARGETS=(aarch64-apple-ios aarch64-apple-ios-sim)
for arg in "$@"; do
  case "$arg" in
    --debug) PROFILE=dev ;;
    --mac) IOS_TARGETS=() ;;
    --mac-arm64) MAC_TARGETS=(aarch64-apple-darwin); IOS_TARGETS=() ;;
    --mac-x86_64) MAC_TARGETS=(x86_64-apple-darwin); IOS_TARGETS=() ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done
TARGETS=("${MAC_TARGETS[@]}")
if [ ${#IOS_TARGETS[@]} -gt 0 ]; then
  TARGETS+=("${IOS_TARGETS[@]}")
fi
OUT_DIR=$([ "$PROFILE" = dev ] && echo debug || echo release)

# shellcheck disable=SC1091
[ -f "$HOME/.cargo/env" ] && . "$HOME/.cargo/env"

if command -v rustup >/dev/null 2>&1; then
  INSTALLED="$(rustup target list --installed)"
  for target in "${TARGETS[@]}"; do
    if ! printf '%s\n' "$INSTALLED" | grep -qx "$target"; then
      echo "▸ rustup target add $target"
      rustup target add "$target"
    fi
  done
fi

# Same minimums as the app, so the linker doesn't warn about newer object files.
export MACOSX_DEPLOYMENT_TARGET=14.0
export IPHONEOS_DEPLOYMENT_TARGET=17.0

for target in "${TARGETS[@]}"; do
  echo "▸ core for $target ($PROFILE)"
  cargo rustc --manifest-path "$CORE/Cargo.toml" -p yuyue-core --lib --crate-type staticlib \
    --profile "$PROFILE" --target "$target" --quiet
done

echo "▸ Swift bindings"
GENERATED="$BUILD/generated"
rm -rf "$GENERATED"
mkdir -p "$GENERATED"
# uniffi-bindgen reads the workspace's cargo metadata from the current folder.
(cd "$CORE" && cargo run -p uniffi-bindgen --quiet -- generate \
  "target/${TARGETS[0]}/$OUT_DIR/libyuyue_core.a" \
  --language swift --out-dir "$GENERATED" --no-format)

# Headers sit in a folder named after the C module, so Swift finds the module map by name.
HEADERS="$BUILD/headers"
rm -rf "$HEADERS"
mkdir -p "$HEADERS/yuyue_coreFFI"
cp "$GENERATED/yuyue_coreFFI.h" "$HEADERS/yuyue_coreFFI/"
cp "$GENERATED/yuyue_coreFFI.modulemap" "$HEADERS/yuyue_coreFFI/module.modulemap"

echo "▸ XCFramework"
MAC_LIPO_DIR="$BUILD/macos-$OUT_DIR"
rm -rf "$MAC_LIPO_DIR"
mkdir -p "$MAC_LIPO_DIR"
MAC_LIBS=()
for target in "${MAC_TARGETS[@]}"; do
  MAC_LIBS+=("$CORE/target/$target/$OUT_DIR/libyuyue_core.a")
done
lipo -create "${MAC_LIBS[@]}" -output "$MAC_LIPO_DIR/libyuyue_core.a"

ARGS=(-library "$MAC_LIPO_DIR/libyuyue_core.a" -headers "$HEADERS")
if [ ${#IOS_TARGETS[@]} -gt 0 ]; then
  for target in "${IOS_TARGETS[@]}"; do
    ARGS+=(-library "$CORE/target/$target/$OUT_DIR/libyuyue_core.a" -headers "$HEADERS")
  done
fi
rm -rf "$PACKAGE/YuyueCoreFFI.xcframework"
xcodebuild -create-xcframework "${ARGS[@]}" -output "$PACKAGE/YuyueCoreFFI.xcframework" >/dev/null

mkdir -p "$PACKAGE/Sources/YuyueCore"
cp "$GENERATED/yuyue_core.swift" "$PACKAGE/Sources/YuyueCore/yuyue_core.swift"
echo "✓ $PACKAGE"

ORT_DIR="$ROOT/apple/OnnxRuntime/onnxruntime.xcframework"
if [ ! -d "$ORT_DIR" ]; then
  echo "▸ OnnxRuntime 1.24.2 XCFramework"
  ORT_URLS=(
    "https://download.onnxruntime.ai/pod-archive-onnxruntime-c-1.24.2.zip"
    "https://github.com/inokoru/yuyue/releases/download/build-deps/pod-archive-onnxruntime-c-1.24.2.zip"
    "https://github.com/BasedHardware/omi/releases/download/desktop-build-deps-onnx-1.24.2-extensions-0.13.0/pod-archive-onnxruntime-c-1.24.2.zip"
  )
  ORT_SHA="f7100a992d2a8135168c8afd831e6a58b465349101982aa58b3e11d36e600b54"
  ORT_TMP="$(mktemp -d)"
  trap 'rm -rf "$ORT_TMP"' EXIT
  ORT_OK=0
  for url in "${ORT_URLS[@]}"; do
    if curl -fsSL --connect-timeout 15 --retry 2 -o "$ORT_TMP/ort.zip" "$url" \
      && echo "$ORT_SHA  $ORT_TMP/ort.zip" | shasum -a 256 -c - >/dev/null 2>&1; then
      ORT_OK=1
      break
    fi
    echo "  mirror unavailable or checksum mismatch: $url" >&2
  done
  if [ "$ORT_OK" -ne 1 ]; then
    echo "failed to download verified OnnxRuntime 1.24.2 XCFramework" >&2
    exit 1
  fi
  unzip -q "$ORT_TMP/ort.zip" "onnxruntime.xcframework/*" -d "$ROOT/apple/OnnxRuntime"
  echo "✓ $ORT_DIR"
fi
