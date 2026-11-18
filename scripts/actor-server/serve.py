"""广播剧“本机会演的声音”：在这台 Mac 上用 Qwen3-TTS（MLX）提供 App 要的接口。

用法：
  uv run -p 3.12 --with mlx-audio==0.6.5 python -I scripts/actor-server/serve.py [--port 8880] [--dir ~/.yuyue-actor]

模型第一次用时从 Hugging Face 下（mlx-community，Apache-2.0）：
- 预设声音 + 说话指令（带语气）：Qwen3-TTS-12Hz-1.7B-CustomVoice-8bit
- 造声音：Qwen3-TTS-12Hz-1.7B-VoiceDesign-6bit 按描述念一句样句；
  之后这个声音的台词用 Qwen3-TTS-12Hz-1.7B-Base-6bit 照样句克隆，前后是同一个人。

接口（照 OpenAI 的 speech 接口）：
- POST /v1/audio/speech {"voice", "input", "instructions"?} → WAV（16 位，24 kHz，单声道）
- POST /v1/voices/design {"description", "sample_text", "language"} → {"voice": id}
- GET  /v1/voices → 现有的声音（预设的和造出来的）

App（调试版）这样接上，启动时会把 -debugActorVoices 那一串打出来：
  -debugActorURL http://127.0.0.1:8880/v1 -debugActorModel qwen3-tts -debugActorVoices "…" [-debugActorDesign 1]
"""
import argparse
import hashlib
import io
import json
import os
import re
import sys
import threading
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np

MODELS = {
    "custom": "mlx-community/Qwen3-TTS-12Hz-1.7B-CustomVoice-8bit",
    "design": "mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-6bit",
    "base": "mlx-community/Qwen3-TTS-12Hz-1.7B-Base-6bit",
}
# CustomVoice 的预设声音：男女、年纪、母语（Qwen3-TTS 的说明）。
PRESETS = {
    "Vivian": ("female", "young", "Chinese"),
    "Serena": ("female", "middle", "Chinese"),
    "Uncle_Fu": ("male", "old", "Chinese"),
    "Dylan": ("male", "young", "Chinese"),
    "Eric": ("male", "middle", "Chinese"),
    "Ryan": ("male", "middle", "English"),
    "Aiden": ("male", "young", "English"),
    "Ono_Anna": ("female", "young", "Japanese"),
    "Sohee": ("female", "young", "Korean"),
}
SAMPLES = {
    "Chinese": "今天天气不错，我们出去走走吧，顺便买点东西回来。",
    "English": "It's a fine day today; let's go out for a walk and pick up a few things on the way back.",
}

lock = threading.Lock()
loaded = {}


def model(kind):
    if kind not in loaded:
        from mlx_audio.tts.utils import load_model

        print(f"loading {MODELS[kind]} …", flush=True)
        loaded[kind] = load_model(MODELS[kind])
    return loaded[kind]


def language(tag):
    tag = (tag or "").lower()
    if tag.startswith("zh"):
        return "Chinese"
    if tag.startswith("en"):
        return "English"
    if tag.startswith("ja"):
        return "Japanese"
    if tag.startswith("ko"):
        return "Korean"
    return "auto"


def guess_language(text):
    return "Chinese" if re.search(r"[一-鿿]", text) else "English"


def joined(results):
    parts = [np.asarray(r.audio, dtype=np.float32).reshape(-1) for r in results]
    rate = results[0].sample_rate if results else 24000
    return (np.concatenate(parts) if parts else np.zeros(0, np.float32)), rate


def wav_bytes(samples, rate):
    pcm = (np.clip(samples, -1, 1) * 32767).astype("<i2")
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.tobytes())
    return buf.getvalue()


def read_wav(path):
    with wave.open(path, "rb") as w:
        rate = w.getframerate()
        data = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768
    return data, rate


class Voices:
    """造出来的声音：样句录音、样句文字、描述，放在 --dir 下。"""

    def __init__(self, folder):
        self.folder = folder
        os.makedirs(folder, exist_ok=True)

    def path(self, vid, ext):
        return os.path.join(self.folder, f"{vid}.{ext}")

    def has(self, vid):
        return re.fullmatch(r"d[0-9a-f]{10}", vid or "") is not None and os.path.exists(self.path(vid, "wav"))

    def all(self):
        out = []
        for f in sorted(os.listdir(self.folder)):
            if f.endswith(".json"):
                with open(os.path.join(self.folder, f)) as fh:
                    out.append(json.load(fh))
        return out


def speech(voices, body):
    text = (body.get("input") or "").strip()
    voice = body.get("voice") or "Vivian"
    instruct = (body.get("instructions") or "").strip() or None
    if not text:
        raise ValueError("no input")
    lang = guess_language(text)
    with lock:
        if voice in PRESETS:
            m = model("custom")
            return joined(list(m.generate_custom_voice(text=text, speaker=voice, language=lang, instruct=instruct)))
        if voices.has(voice):
            # 克隆不听说话指令：照样句的声音平着念。
            with open(voices.path(voice, "txt")) as fh:
                ref_text = fh.read()
            m = model("base")
            return joined(list(m.generate(text=text, ref_audio=voices.path(voice, "wav"), ref_text=ref_text, lang_code=lang.lower())))
    raise KeyError(voice)


def design(voices, body):
    description = (body.get("description") or "").strip()
    if not description:
        raise ValueError("no description")
    lang = language(body.get("language"))
    if lang == "auto":
        lang = guess_language(description)
    sample = (body.get("sample_text") or "").strip() or SAMPLES.get(lang, SAMPLES["English"])
    vid = "d" + hashlib.sha256(f"{description}\n{sample}".encode()).hexdigest()[:10]
    if voices.has(vid):
        return vid
    with lock:
        m = model("design")
        samples, rate = joined(list(m.generate_voice_design(text=sample, language=lang, instruct=description)))
    with open(voices.path(vid, "wav"), "wb") as fh:
        fh.write(wav_bytes(samples, rate))
    with open(voices.path(vid, "txt"), "w") as fh:
        fh.write(sample)
    low = description.lower()
    gender = "female" if ("female" in low or "女" in description) else "male" if ("male" in low or "男" in description) else ""
    with open(voices.path(vid, "json"), "w") as fh:
        json.dump({"id": vid, "description": description, "language": lang, "gender": gender}, fh, ensure_ascii=False)
    return vid


def handler(voices):
    class H(BaseHTTPRequestHandler):
        def reply(self, code, body, kind="application/json"):
            data = body if isinstance(body, bytes) else json.dumps(body, ensure_ascii=False).encode()
            self.send_response(code)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path.rstrip("/") == "/v1/voices":
                presets = [{"id": k, "gender": g, "age": a, "language": l} for k, (g, a, l) in PRESETS.items()]
                return self.reply(200, {"voices": presets + voices.all()})
            self.reply(404, {"error": "not found"})

        def do_POST(self):
            try:
                body = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
                if self.path.rstrip("/") == "/v1/audio/speech":
                    samples, rate = speech(voices, body)
                    return self.reply(200, wav_bytes(samples, rate), "audio/wav")
                if self.path.rstrip("/") == "/v1/voices/design":
                    return self.reply(200, {"voice": design(voices, body)})
                self.reply(404, {"error": "not found"})
            except KeyError as e:
                self.reply(404, {"error": f"no voice {e}"})
            except ValueError as e:
                self.reply(400, {"error": str(e)})
            except Exception as e:  # the model failing: the app retries or falls back
                print("error:", repr(e), file=sys.stderr, flush=True)
                self.reply(500, {"error": str(e)})

        def log_message(self, fmt, *args):
            print(self.address_string(), fmt % args, flush=True)

    return H


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8880)
    ap.add_argument("--dir", default=os.path.expanduser("~/.yuyue-actor"))
    a = ap.parse_args()
    voices = Voices(a.dir)
    listed = ",".join(f"{k}:{g}:{age}" for k, (g, age, l) in PRESETS.items() if l in ("Chinese", "English"))
    print(f"http://127.0.0.1:{a.port}/v1")
    print(f'-debugActorURL http://127.0.0.1:{a.port}/v1 -debugActorModel qwen3-tts -debugActorVoices "{listed}" -debugActorDesign 1', flush=True)
    ThreadingHTTPServer(("127.0.0.1", a.port), handler(voices)).serve_forever()


if __name__ == "__main__":
    main()
