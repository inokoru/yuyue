"""将即时层模型（ZipVoice-Distill-Int8 + Vocos 24kHz + 中英词表）打包为 live-zipvoice-1.zip，
供三端（macOS / iOS / Android）按 BLAKE3 校验下载解压到 models/live-zipvoice-1/。
"""
from __future__ import annotations

import argparse
import shutil
import zipfile
from pathlib import Path

LICENSE_TEXT = """Live Voice Model Bundle (live-zipvoice-1)

1. ZipVoice-Distill (encoder.int8.onnx, decoder.int8.onnx, tokens.txt, lexicon-zh.txt)
   Source: https://github.com/k2-fsa/ZipVoice / https://huggingface.co/k2-fsa/ZipVoice
   Code License: Apache-2.0
   Pre-trained Weights License: CC-BY-NC 4.0 (trained on Emilia dataset; redistributed unchanged for free non-commercial personal reading in Yuyue).

2. Vocos 24 kHz Mel Vocoder (vocos_24khz.onnx)
   Source: https://huggingface.co/charactr/vocos-mel-24khz
   License: MIT

3. English Pronouncing Dictionary (lexicon-en.txt)
   Source: CMUDict / k2-fsa sherpa-onnx tts-models
   License: BSD-2-Clause / Public Domain
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default="voice-audition/.models", help="已下载的模型缓存目录")
    ap.add_argument("--out", default="models", help="输出目录")
    args = ap.parse_args()

    src = Path(args.src)
    zsrc = src / "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia"
    out_dir = Path(args.out)
    stage = out_dir / "live-zipvoice-1"
    stage.mkdir(parents=True, exist_ok=True)

    files = {
        "encoder.int8.onnx": zsrc / "encoder.int8.onnx",
        "decoder.int8.onnx": zsrc / "decoder.int8.onnx",
        "tokens.txt": zsrc / "tokens.txt",
        "lexicon-zh.txt": zsrc / "lexicon.txt",
        "lexicon-en.txt": src / "lexicon-en-us.txt",
        "vocos_24khz.onnx": src / "vocos_24khz.onnx",
    }
    for dst_name, src_path in files.items():
        if not src_path.is_file():
            raise FileNotFoundError(f"missing {src_path}")
        shutil.copy2(src_path, stage / dst_name)
    (stage / "LICENSE.txt").write_text(LICENSE_TEXT, encoding="utf-8")

    zpath = out_dir / "live-zipvoice-1.zip"
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for p in sorted(stage.iterdir()):
            if p.is_file():
                zf.write(p, f"live-zipvoice-1/{p.name}")
    print(f"✓ wrote {zpath} ({zpath.stat().st_size} bytes)", flush=True)


if __name__ == "__main__":
    main()
