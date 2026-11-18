#!/bin/sh
# 把强制对齐模型和声纹模型传到 GitHub Releases 的 models-1。
# 三端第一次合成有声书（对齐）、做广播剧（声纹）时从这里下载，下载后核对 BLAKE3，对不上就不用。
# 声纹模型由 scripts/align-bench/export_speaker.py 生成。
#
# 用法：scripts/upload-models.sh <模型所在文件夹> [仓库]
#   仓库默认 inokoru/yuyue
# 先 gh auth login。仓库必须是公开的，否则别人下载是 404。
# 换了仓库，要同时改 Aligner.swift、Aligner.kt、SpeakerPrint.swift、SpeakerPrint.kt 里的地址。
set -eu
DIR=${1:?要给模型所在文件夹}
REPO=${2:-inokoru/yuyue}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
YUYUE=$ROOT/core/target/release/yuyue

check() { # 文件 应有的 BLAKE3（和 App 里写的一致）
  got=$("$YUYUE" digest "$1")
  [ "$got" = "$2" ] || { echo "$1 的 BLAKE3 是 $got，App 要的是 $2，不传" >&2; exit 1; }
}
FILES=""
maybe_add() { # 文件 应有的 BLAKE3
  [ -f "$1" ] || return 0
  check "$1" "$2"
  FILES="$FILES $1"
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
maybe_add "$DIR/sense_voice.int8.onnx" "$(sed -n 's/.*MODEL to "\(.*\)".*/\1/p' "$ASR")"
maybe_add "$DIR/sense_voice_tokens.txt" "$(sed -n 's/.*TOKENS to "\(.*\)".*/\1/p' "$ASR")"
# 即时层朗读模型（ZipVoice-Distill-Int8 + Vocos 24 kHz，CC-BY-NC 4.0 / MIT）与自带声音包（voices-1，CC0 1.0）。
LIVE=$ROOT/apple/Yuyue/Speech/LiveVoiceModel.swift
if [ -f "$LIVE" ]; then
  LIVE_DIGEST=$(sed -n 's/.*static let modelDigest = "\(.*\)".*/\1/p' "$LIVE")
  PACK_DIGEST=$(sed -n 's/.*static let packDigest = "\(.*\)".*/\1/p' "$LIVE")
  [ -n "$LIVE_DIGEST" ] && maybe_add "$DIR/live-zipvoice-1.zip" "$LIVE_DIGEST"
  [ -n "$PACK_DIGEST" ] && maybe_add "$DIR/voices-1.zip" "$PACK_DIGEST"
fi
[ -n "$FILES" ] || { echo "$DIR 里没有待上传的模型文件" >&2; exit 1; }

[ "$(gh repo view "$REPO" --json visibility -q .visibility)" = PUBLIC ] || { echo "$REPO 不是公开仓库，别人下载会是 404" >&2; exit 1; }
gh release view models-1 -R "$REPO" >/dev/null 2>&1 || gh release create models-1 -R "$REPO" --latest=false --title "语阅本机模型 1" --notes "语阅按需下载的本机离线模型与声音包：Meta MMS 对齐模型（CC-BY-NC 4.0，仅非商用）、ZipVoice-Distill 即时朗读与克隆模型（CC-BY-NC 4.0，仅非商用，含 Vocos MIT）、CAM++ 声纹模型（Apache-2.0）、SenseVoice-Small 听写模型（FunASR 许可）、voices-1 自带生成声音包（CC0 1.0，全由模型按文字描述生成，不含真人录音）。"
# shellcheck disable=SC2086
gh release upload models-1 -R "$REPO" --clobber $FILES

for f in MMSAligner.mlmodelc.zip mms_fa.int8.onnx SpeakerPrint.mlmodelc.zip speaker_print.onnx sense_voice.int8.onnx sense_voice_tokens.txt live-zipvoice-1.zip voices-1.zip; do
  code=$(curl -sIL -o /dev/null -w '%{http_code}' "https://github.com/$REPO/releases/download/models-1/$f")
  echo "$f → $code"
done
