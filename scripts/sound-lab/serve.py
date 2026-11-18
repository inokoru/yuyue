"""广播剧“生成音效”：书里写了、素材库里没有的声音，在这台 Mac 上用 MOSS-SoundEffect v2.0 现做。

用法（环境见 moss.py）：... python -I scripts/sound-lab/serve.py [--port 8890]
接口：POST /generate {"prompt": 英文描述, "seconds": 秒数} → WAV（16 位，48 kHz，单声道）
App（调试版）：Mac 用 -debugSfxURL http://127.0.0.1:8890；Android 模拟器用 -e sfxURL http://10.0.2.2:8890
"""
import argparse
import io
import json
import sys
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import os

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import moss  # noqa: E402


def wav_bytes(x):
    pcm = (np.clip(x, -1, 1) * 32767).astype("<i2")
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(moss.RATE)
        w.writeframes(pcm.tobytes())
    return buf.getvalue()


class H(BaseHTTPRequestHandler):
    def reply(self, code, data, kind):
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        if self.path.rstrip("/") != "/generate":
            return self.reply(404, b"{}", "application/json")
        try:
            body = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
            prompt = (body.get("prompt") or "").strip()
            if not prompt:
                return self.reply(400, b'{"error":"no prompt"}', "application/json")
            x = moss.make(prompt, float(body.get("seconds") or 2.0), steps=int(body.get("steps") or 50))
            self.reply(200, wav_bytes(x), "audio/wav")
        except Exception as e:  # the app retries, then leaves the sound out
            print("error:", repr(e), file=sys.stderr, flush=True)
            self.reply(500, json.dumps({"error": str(e)}).encode(), "application/json")

    def log_message(self, fmt, *args):
        print(self.address_string(), fmt % args, flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8890)
    ap.add_argument("--host", default="127.0.0.1")
    a = ap.parse_args()
    print(f"http://{a.host}:{a.port}  (-debugSfxURL http://127.0.0.1:{a.port})", flush=True)
    ThreadingHTTPServer((a.host, a.port), H).serve_forever()


if __name__ == "__main__":
    main()
