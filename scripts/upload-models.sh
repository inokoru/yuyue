#!/bin/sh
# 把本机离线模型同步到 GitHub Releases（models-1）和 / 或魔搭 ModelScope（国内高速镜像）。
# 三端按需下载后均会核对 BLAKE3，对不上就不用；支持 HTTP Range 断点续传。
#
# 用法：
#   scripts/upload-models.sh <模型文件夹> [选项]
#
# 选项：
#   --repo <owner/repo>          GitHub 仓库（默认 inokoru/yuyue）
#   --modelscope [owner/repo]    同步到魔搭公开模型仓（默认 nowtin/yuyue-models）
#   --only-modelscope            只传魔搭，跳过 GitHub Releases
#   --fonts <字体文件夹>          顺带把 fonts/*.ttf|*.otf 传到魔搭的 fonts/ 目录（加速霞鹜文楷/思源字体）
#
# 兼容旧用法：scripts/upload-models.sh <模型文件夹> <GitHub 仓库>
#
# 魔搭首次使用：
#   1. 在 https://modelscope.cn 创建公开模型仓（如 nowtin/yuyue-models）
#   2. pip install -U modelscope && modelscope login --token <你的访问令牌>（也支持 uvx --from modelscope）
#   3. 运行：scripts/upload-models.sh models --modelscope nowtin/yuyue-models
set -eu

DIR=""
REPO="inokoru/yuyue"
MS_REPO=""
ONLY_MS=0
FONTS_DIR=""

while [ $# -gt 0 ]; do
  case "$1" in
    --repo)
      REPO="${2:?--repo 需要仓库名}"
      shift 2
      ;;
    --modelscope)
      if [ $# -ge 2 ] && [ "${2#--}" = "$2" ]; then
        MS_REPO="$2"
        shift 2
      else
        MS_REPO="nowtin/yuyue-models"
        shift
      fi
      ;;
    --only-modelscope)
      ONLY_MS=1
      [ -n "$MS_REPO" ] || MS_REPO="nowtin/yuyue-models"
      shift
      ;;
    --fonts)
      FONTS_DIR="${2:?--fonts 需要字体文件夹}"
      shift 2
      ;;
    -h|--help)
      sed -n '2,20p' "$0"
      exit 0
      ;;
    *)
      if [ -z "$DIR" ]; then
        DIR="$1"
      else
        REPO="$1"
      fi
      shift
      ;;
  esac
done

: "${DIR:?要给模型所在文件夹（用法见 --help）}"
ROOT=$(cd "$(dirname "$0")/.." && pwd)
YUYUE=$ROOT/core/target/release/yuyue

check() { # 文件 应有的 BLAKE3（和 App 里写的一致）
  got=$("$YUYUE" digest "$1")
  [ "$got" = "$2" ] || { echo "$1 的 BLAKE3 是 ${got}，App 要的是 $2，不传" >&2; exit 1; }
}
FILES=""
NAMES=""
maybe_add() { # 文件 应有的 BLAKE3
  [ -f "$1" ] || return 0
  check "$1" "$2"
  FILES="$FILES $1"
  NAMES="$NAMES $(basename "$1")"
}
APPLE=$(sed -n 's/.*static let digest = "\(.*\)".*/\1/p' "$ROOT/apple/Yuyue/App/Aligner.swift")
DROID=$(sed -n 's/.*DIGEST = "\(.*\)".*/\1/p' "$ROOT/android/app/src/main/java/app/yuyue/speech/Aligner.kt")
maybe_add "$DIR/MMSAligner.mlmodelc.zip" "$APPLE"
maybe_add "$DIR/mms_fa.int8.onnx" "$DROID"
SPK_APPLE=$(sed -n 's/.*static let digest = "\(.*\)".*/\1/p' "$ROOT/apple/Yuyue/Speech/SpeakerPrint.swift")
SPK_DROID=$(sed -n 's/.*DIGEST = "\(.*\)".*/\1/p' "$ROOT/android/app/src/main/java/app/yuyue/speech/SpeakerPrint.kt")
maybe_add "$DIR/SpeakerPrint.mlmodelc.zip" "$SPK_APPLE"
maybe_add "$DIR/speaker_print.onnx" "$SPK_DROID"
# Android 的听写模型（SenseVoice-Small，sherpa-onnx 导出的 int8 版和 tokens）。
ASR=$ROOT/android/app/src/main/java/app/yuyue/speech/Listener.kt
ASR_MODEL_DIGEST=$(sed -n 's/.*MODEL_DIGEST = "\(.*\)".*/\1/p' "$ASR")
ASR_TOKENS_DIGEST=$(sed -n 's/.*TOKENS_DIGEST = "\(.*\)".*/\1/p' "$ASR")
maybe_add "$DIR/sense_voice.int8.onnx" "$ASR_MODEL_DIGEST"
maybe_add "$DIR/sense_voice_tokens.txt" "$ASR_TOKENS_DIGEST"
# 即时层朗读模型（ZipVoice-Distill-Int8 + Vocos 24 kHz，CC-BY-NC 4.0 / MIT）与自带声音包（voices-1，CC0 1.0）。
LIVE=$ROOT/apple/Yuyue/Speech/LiveVoiceModel.swift
if [ -f "$LIVE" ]; then
  LIVE_DIGEST=$(sed -n 's/.*static let modelDigest = "\(.*\)".*/\1/p' "$LIVE")
  PACK_DIGEST=$(sed -n 's/.*static let packDigest = "\(.*\)".*/\1/p' "$LIVE")
  [ -n "$LIVE_DIGEST" ] && maybe_add "$DIR/live-zipvoice-1.zip" "$LIVE_DIGEST"
  [ -n "$PACK_DIGEST" ] && maybe_add "$DIR/voices-1.zip" "$PACK_DIGEST"
fi
[ -n "$FILES" ] || { echo "$DIR 里没有待上传的模型文件" >&2; exit 1; }

if [ "$ONLY_MS" -eq 0 ]; then
  [ "$(gh repo view "$REPO" --json visibility -q .visibility)" = PUBLIC ] || { echo "$REPO 不是公开仓库，别人下载会是 404" >&2; exit 1; }
  gh release view models-1 -R "$REPO" >/dev/null 2>&1 || gh release create models-1 -R "$REPO" --latest=false --title "语阅本机模型 1" --notes "语阅按需下载的本机离线模型与声音包：Meta MMS 对齐模型（CC-BY-NC 4.0，仅非商用）、ZipVoice-Distill 即时朗读与克隆模型（CC-BY-NC 4.0，仅非商用，含 Vocos MIT）、CAM++ 声纹模型（Apache-2.0）、SenseVoice-Small 听写模型（FunASR 许可）、voices-1 自带生成声音包（CC0 1.0，全由模型按文字描述生成，不含真人录音）。"
  # shellcheck disable=SC2086
  gh release upload models-1 -R "$REPO" --clobber $FILES

  echo "=== 验证 GitHub Releases ($REPO / models-1) ==="
  for f in MMSAligner.mlmodelc.zip mms_fa.int8.onnx SpeakerPrint.mlmodelc.zip speaker_print.onnx sense_voice.int8.onnx sense_voice_tokens.txt live-zipvoice-1.zip voices-1.zip; do
    code=$(curl -sIL -o /dev/null -w '%{http_code}' "https://github.com/$REPO/releases/download/models-1/$f")
    echo "GitHub: $f → $code"
  done
fi

if [ -n "$MS_REPO" ]; then
  if command -v modelscope >/dev/null 2>&1; then
    MS_CMD="modelscope"
  elif command -v uvx >/dev/null 2>&1; then
    MS_CMD="uvx --from modelscope modelscope"
  else
    echo "未找到 modelscope CLI 或 uvx。请先安装并登录：" >&2
    echo "  pip install -U modelscope && modelscope login --token <ACCESS_TOKEN>" >&2
    exit 1
  fi
  echo "=== 上传模型到魔搭 ModelScope ($MS_REPO) ==="
  for path in $FILES; do
    name=$(basename "$path")
    echo "上传 $name → $MS_REPO"
    # shellcheck disable=SC2086
    $MS_CMD upload "$MS_REPO" "$path" "$name" --commit-message "upload $name"
  done

  if [ -n "$FONTS_DIR" ] && [ -d "$FONTS_DIR" ]; then
    echo "=== 上传字体到魔搭 ModelScope ($MS_REPO / fonts/) ==="
    for font in "$FONTS_DIR"/*.ttf "$FONTS_DIR"/*.otf; do
      [ -f "$font" ] || continue
      fname=$(basename "$font")
      echo "上传 fonts/$fname → $MS_REPO"
      # shellcheck disable=SC2086
      $MS_CMD upload "$MS_REPO" "$font" "fonts/$fname" --commit-message "upload fonts/$fname"
    done
  fi

  echo "=== 验证魔搭匿名直链与断点续传 (Range 206) ==="
  for name in $NAMES; do
    code=$(curl -sL -r 0-63 -o /dev/null -w '%{http_code}' "https://modelscope.cn/models/$MS_REPO/resolve/master/$name")
    echo "ModelScope: $name → HTTP $code"
  done
fi
