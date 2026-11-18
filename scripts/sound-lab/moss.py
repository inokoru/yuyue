"""MOSS-SoundEffect v2.0（OpenMOSS，Apache-2.0）在 Mac 上用 MLX 跑：一句英文描述 → 48 kHz 单声道。

生成音效服务（serve.py）和内置素材库（build_library.py）共用。
权重 mlx-community/MOSS-SoundEffect-v2.0-4bit 第一次用时从 Hugging Face 下（约 6 GB）。
MLX 移植是 xocialize/moss-soundeffect-mlx（Apache-2.0），钉在审过的提交上：
  uv run -p 3.12 --with "moss-sfx-mlx @ git+https://github.com/xocialize/moss-soundeffect-mlx@4e8e3e36d4d9c991d16a3e65a4b7099d406b9ed8" \\
         --with mlx-lm --with ftfy --with regex --with tqdm --with soundfile python -I scripts/sound-lab/<脚本>
"""
import os
import threading

import numpy as np

REPO = "mlx-community/MOSS-SoundEffect-v2.0-4bit"
RATE = 48_000

_pipe = None
_lock = threading.Lock()


def pipe():
    global _pipe
    if _pipe is None:
        from moss_sfx_mlx.pipeline_mlx import MossSoundEffectPipeline

        local = os.environ.get("MOSS_LOCAL_DIR") or os.path.normpath(
            os.path.join(os.path.dirname(os.path.abspath(__file__)), "../../models/.moss-v2-4bit")
        )
        if os.path.isfile(os.path.join(local, "model_index.json")) and os.path.isfile(os.path.join(local, "vae/config.json")):
            path = local
        else:
            os.environ.setdefault("HF_HUB_DISABLE_XET", "1")
            from huggingface_hub import snapshot_download

            path = snapshot_download(REPO)
        _pipe = MossSoundEffectPipeline.from_pretrained(path)
    return _pipe


def make(prompt, seconds, steps=50, seed=0, window=None):
    """`seconds` of the sound `prompt` describes, mono float32 at 48 kHz.

    The model denoises a full window (30 s by default) whatever is asked; `window` shortens it
    for speed (the duration tag in the prompt still says how long the sound is)."""
    seconds = max(0.5, min(30.0, float(seconds)))
    with _lock:
        import mlx.core as mx

        kwargs = {}
        if window:
            kwargs["max_inference_seconds"] = int(max(window, int(np.ceil(seconds))))
        audio = pipe()(prompt=prompt, seconds=seconds, num_inference_steps=steps, cfg_scale=4.0, seed=seed,
                       progress_bar_cmd=lambda r: r, **kwargs)
        out = np.array(audio.astype(mx.float32)).reshape(-1)
    peak = float(np.max(np.abs(out))) if out.size else 0.0
    if peak > 0.99:
        out = out * (0.99 / peak)
    return out.astype(np.float32)
