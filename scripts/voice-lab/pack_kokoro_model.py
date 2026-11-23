"""将跨端轻量朗读模型（Kokoro-82M-v1.1-zh Int8 + 音色向量库 + 中英词表）打包为 kokoro-v1_1-1.zip，
供四端（macOS / iOS / Android / Windows）按 BLAKE3 校验下载解压到 models/kokoro-v1_1-1/。
"""
from __future__ import annotations

import argparse
import shutil
import zipfile
from pathlib import Path

LICENSE_TEXT = """Kokoro Lightweight Neural Voice Bundle (kokoro-v1_1-1)

1. Kokoro-82M-v1.1-zh Int8 ONNX & Speaker Embeddings (model.int8.onnx, voices.bin)
   Source: https://huggingface.co/hexgrad/Kokoro-82M-v1.1-zh
           https://huggingface.co/csukuangfj/kokoro-int8-multi-lang-v1_1
   License: Apache-2.0

2. Mandarin & English Pronunciation Lexicons (tokens.txt, lexicon-zh.txt, lexicon-us-en.txt)
   Source: hexgrad/misaki & k2-fsa/sherpa-onnx
   License: Apache-2.0 / MIT
"""


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default="voice-audition/.models", help="已下载的模型缓存目录")
    ap.add_argument("--out", default="models", help="输出目录")
    args = ap.parse_args()

    ksrc = Path(args.src) / "kokoro-int8-multi-lang-v1_1"
    out_dir = Path(args.out)
    stage = out_dir / "kokoro-v1_1-1"
    stage.mkdir(parents=True, exist_ok=True)

    files = {
        "model.int8.onnx": ksrc / "model.int8.onnx",
        "voices.bin": ksrc / "voices.bin",
        "tokens.txt": ksrc / "tokens.txt",
        "lexicon-zh.txt": ksrc / "lexicon-zh.txt",
        "lexicon-us-en.txt": ksrc / "lexicon-us-en.txt",
    }
    for dst_name, src_path in files.items():
        if not src_path.is_file():
            raise FileNotFoundError(f"missing {src_path}")
        shutil.copy2(src_path, stage / dst_name)
    (stage / "LICENSE.txt").write_text(LICENSE_TEXT, encoding="utf-8")

    zpath = out_dir / "kokoro-v1_1-1.zip"
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for p in sorted(stage.iterdir()):
            if p.is_file():
                zf.write(p, f"kokoro-v1_1-1/{p.name}")
    print(f"✓ wrote {zpath} ({zpath.stat().st_size} bytes)", flush=True)


if __name__ == "__main__":
    main()
