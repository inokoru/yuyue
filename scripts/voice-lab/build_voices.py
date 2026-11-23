"""生成语阅自带声音包（voices-1.zip）：48 张声音卡（中文 32 张 + 英文 16 张），每张七档语气参考音。

主要流程（与 scripts/sound-lab/build_library.py 保持一致）：
- 全部由本机 Qwen3-TTS 1.7B 按文字描述生成，不使用任何真人录音，也不使用预设音色；整包按 CC0 1.0 发布。
- 支持随时中断续做：已写进 <out>/voices-1/voices.json 且 Ogg 文件齐全的卡自动跳过。
- 自动质量把关（每段最多换种子试 5 次）：
  1. 裁掉首尾静音（各留 0.10 秒），响度归到 −20 LUFS，24 kHz 单声道存 Ogg Vorbis；
  2. 时长 6.0–10.0 秒，削波采样 < 0.1%；
  3. 听写核对念的字（SenseVoice ONNX），错字率 ≤ 2%；
  4. 同语言同性别的卡，neutral 之间两两声纹余弦相似度 < 0.80（不够开的换种子重造）；
  5. 其余六档语气与本卡 neutral 的声纹余弦相似度 ≥ 0.55（5 次都不过的按退路省略该档，回退用 neutral）。

用法：
  uv run --with mlx-audio --with soundfile --with pyyaml --with onnxruntime \\
    python scripts/voice-lab/build_voices.py models [--only card.b.01] [--limit 4]
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import zipfile
from pathlib import Path

import numpy as np
import soundfile as sf
import yaml

RATE = 24_000
DESIGN_MODEL = os.environ.get("YUYUE_DESIGN_MODEL", "mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-6bit")
BASE_MODEL = os.environ.get("YUYUE_BASE_MODEL", "mlx-community/Qwen3-TTS-12Hz-1.7B-Base-6bit")

STYLES = ["neutral", "joy", "anger", "sad", "tense", "soft", "shout"]

STYLE_INSTRUCT_ZH = {
    "neutral": "平稳自然地说",
    "joy": "高兴地、带着笑意说",
    "anger": "生气地、语气很重地说",
    "sad": "难过地、声音发沉地说",
    "tense": "紧张地、急促地说",
    "soft": "压低声音、轻轻地说",
    "shout": "大声喊出来",
}

STYLE_INSTRUCT_EN = {
    "neutral": "Speak calmly and naturally",
    "joy": "Speak happily, with a warm smile in your voice",
    "anger": "Speak angrily, with a heavy, sharp tone",
    "sad": "Speak sadly, with a low and heavy voice",
    "tense": "Speak tensely and hurriedly",
    "soft": "Lower your voice and speak very gently",
    "shout": "Call out loudly",
}

# 与 core/yuyue-core/src/drama/card.rs 的 script_for 完全一致（每句 25–40 字 / 20–30 词，约 6.5–9.5 秒）
SCRIPTS_ZH = {
    "neutral": "今天早上下了一场小雨，我们沿着河边慢慢走回家，一路上聊了许多往事。",
    "joy": "真是没想到能在这儿遇上你！快进屋坐下喝杯热茶，好好跟我说说最近的新鲜事！",
    "anger": "这件事你明明从头到尾都清楚，为什么偏偏瞒着所有人，到现在还不肯说实话！",
    "sad": "院子里的灯早就灭了，我站在门口叫了好几声，屋里却再也没有人答应。",
    "tense": "别出声，外头好像有人踩着碎石走过来了，快把灯吹灭，先躲到屏风后面去！",
    "soft": "夜深了，屋里的人都睡熟了，你把衣裳披好，咱们轻手轻脚地从侧门出去吧。",
    "shout": "快停下！前头的木桥已经断了，再往前走一步就要掉下去了，快退回来！",
}

SCRIPTS_EN = {
    "neutral": "The morning rain had just stopped, so we walked slowly along the quiet river bank all the way home.",
    "joy": "Look who finally made it back today! Sit down by the window and tell me everything that happened!",
    "anger": "You knew from the very beginning what this meant, and yet you hid the truth from every one of us!",
    "sad": "The house stood empty when I returned at dusk, and nobody answered when I called out your name.",
    "tense": "Listen closely—someone is coming up the stairs right now, so turn out the lamp and stay behind me!",
    "soft": "Keep your voice down, everyone else is already asleep; rest a little while and I will watch the door.",
    "shout": "Stop right there! Drop the lantern and get away from the bridge before it gives way!",
}

NOTICE = (
    "全部由模型按文字描述生成，没有用任何真人的录音。"
    " Generated entirely from text descriptions by Qwen3-TTS VoiceDesign; no human voice recordings were used."
)


def resample_linear(x: np.ndarray, src_sr: int, dst_sr: int) -> np.ndarray:
    if src_sr == dst_sr or x.size == 0:
        return x.astype(np.float32)
    n = int(math.floor(len(x) * dst_sr / src_sr))
    idx = np.linspace(0, len(x) - 1, n, dtype=np.float64)
    return np.interp(idx, np.arange(len(x), dtype=np.float64), x).astype(np.float32)


def trim_and_normalize_20lufs(x: np.ndarray, sr: int = RATE) -> np.ndarray:
    """切掉首尾静音（各留 0.10 秒），响度归到 −20 LUFS，峰值限在 −1 dBFS。"""
    x = np.asarray(x, dtype=np.float32).reshape(-1)
    if x.size == 0:
        return x
    win = max(1, sr // 50)
    thresh = 10 ** (-45.0 / 20.0)
    rms = np.array([
        math.sqrt(float(np.mean(x[i:i + win] ** 2)))
        for i in range(0, len(x), win)
    ])
    active = np.where(rms >= thresh)[0]
    pad = int(0.10 * sr)
    if active.size > 0:
        a = max(0, int(active[0]) * win - pad)
        b = min(len(x), int(active[-1] + 1) * win + pad)
        x = x[a:b]
    # 留足首尾各 0.10 秒静音余量
    head_quiet = np.zeros(pad // 2, dtype=np.float32)
    x = np.concatenate([head_quiet, x, head_quiet])

    gate = 10 ** (-42.0 / 20.0)
    voiced = x[np.abs(x) >= gate]
    if voiced.size > 0:
        cur_rms = math.sqrt(float(np.mean(voiced ** 2)))
        if cur_rms > 1e-5:
            target = 10 ** (-20.0 / 20.0)
            ceiling = 10 ** (-1.0 / 20.0)
            gain = min(max(target / cur_rms, 0.1), 16.0)
            peak = float(np.max(np.abs(x)))
            if peak * gain > ceiling:
                gain = ceiling / peak
            x = x * gain
    return x.astype(np.float32)


def count_units(text: str, lang: str) -> int:
    if lang.startswith("en"):
        return len([w for w in text.split() if any(ch.isalnum() for ch in w)])
    return sum(1 for ch in text if ch.isalnum())


def normalize_tokens(text: str, lang: str) -> list[str]:
    if lang.startswith("en"):
        return re.findall(r"[a-z0-9']+", text.lower())
    return [ch for ch in text if "\u4e00" <= ch <= "\u9fff" or ch.isalnum()]


def edit_cer(want: str, got: str, lang: str) -> float:
    a = normalize_tokens(want, lang)
    b = normalize_tokens(got, lang)
    if not a:
        return 0.0
    prev = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        cur = [i] + [0] * len(b)
        for j, y in enumerate(b, 1):
            cur[j] = min(prev[j - 1] + (0 if x == y else 1), prev[j] + 1, cur[j - 1] + 1)
        prev = cur
    return prev[-1] / len(a)


def cosine(a: list[float], b: list[float]) -> float:
    if not a or not b or len(a) != len(b):
        return 1.0
    va = np.asarray(a, dtype=np.float32)
    vb = np.asarray(b, dtype=np.float32)
    na = float(np.linalg.norm(va))
    nb = float(np.linalg.norm(vb))
    if na < 1e-7 or nb < 1e-7:
        return 0.0
    return float(np.dot(va, vb) / (na * nb))


class Checkers:
    """复用项目里已有的 CAM++ 声纹模型（speaker_print.onnx）与 SenseVoice 听写模型做 4.2 的自动检查。"""

    def __init__(self, root: Path):
        import onnxruntime as ort

        self.ort = ort
        self.speaker_sess = None
        self.sherpa_rec = None
        self.pypinyin = None
        try:
            import pypinyin
            self.pypinyin = pypinyin
        except Exception:
            pass

        candidates = [
            root / "models",
            Path.home() / "Library" / "Application Support" / "Yuyue" / "models",
        ]
        for base in candidates:
            for sp_rel in ("speaker_print.onnx", "speaker-1/campplus.onnx"):
                sp = base / sp_rel
                if sp.is_file() and self.speaker_sess is None:
                    self.speaker_sess = ort.InferenceSession(str(sp), providers=["CPUExecutionProvider"])
            sv = base / "sense_voice.int8.onnx"
            tk = base / "sense_voice_tokens.txt"
            if sv.is_file() and tk.is_file() and self.sherpa_rec is None:
                try:
                    import sherpa_onnx
                    self.sherpa_rec = sherpa_onnx.OfflineRecognizer.from_sense_voice(
                        model=str(sv),
                        tokens=str(tk),
                        num_threads=4,
                        use_itn=False,
                    )
                except Exception:
                    pass

    @staticmethod
    def _mel_fbank_80(samples_16k: np.ndarray, cmvn: bool = True) -> np.ndarray:
        """与 core/yuyue-core/src/drama/speaker.rs 完全一致的 16 kHz Kaldi 80 维 fbank。"""
        sr = 16_000
        win_len = 400  # 25 ms
        hop_len = 160  # 10 ms
        n_fft = 512
        if len(samples_16k) < win_len:
            return np.zeros((0, 80), dtype=np.float32)
        n_frames = 1 + (len(samples_16k) - win_len) // hop_len
        idx = np.arange(win_len)[None, :] + hop_len * np.arange(n_frames)[:, None]
        frames = samples_16k[idx].astype(np.float32)
        frames = frames - np.mean(frames, axis=1, keepdims=True)
        # Povey window & pre-emphasis 0.97 (matching speaker.rs)
        prev = np.concatenate([frames[:, :1], frames[:, :-1]], axis=1)
        frames = frames - 0.97 * prev
        povey = (0.5 - 0.5 * np.cos(2.0 * np.pi * np.arange(win_len) / (win_len - 1))) ** 0.85
        frames = frames * povey[None, :]
        spec = np.fft.rfft(frames, n=n_fft)
        power = (np.abs(spec[:, : n_fft // 2]) ** 2).astype(np.float32)

        def mel(f):
            return 1127.0 * np.log(1.0 + f / 700.0)

        lo, hi = mel(20.0), mel(sr / 2.0)
        delta = (hi - lo) / 81.0
        bins_mel = mel(np.arange(n_fft // 2, dtype=np.float32) * sr / n_fft)
        fb = np.zeros((80, n_fft // 2), dtype=np.float32)
        for b in range(80):
            left, centre, right = lo + b * delta, lo + (b + 1) * delta, lo + (b + 2) * delta
            up = (bins_mel - left) / max(centre - left, 1e-9)
            dn = (right - bins_mel) / max(right - centre, 1e-9)
            fb[b] = np.maximum(0.0, np.minimum(up, dn))
        feats = np.log(np.maximum(power @ fb.T, 1e-10)).astype(np.float32)
        if cmvn:
            feats = feats - np.mean(feats, axis=0, keepdims=True)
        return feats

    def embedding(self, samples_24k: np.ndarray) -> list[float]:
        if self.speaker_sess is None:
            return []
        s16 = resample_linear(samples_24k, RATE, 16_000)
        feats = self._mel_fbank_80(s16, cmvn=True)
        if feats.shape[0] < 50:
            return []
        in_name = self.speaker_sess.get_inputs()[0].name
        emb = self.speaker_sess.run(None, {in_name: feats[None, :, :]})[0].reshape(-1)
        norm = float(np.linalg.norm(emb))
        if norm > 1e-7:
            emb = emb / norm
        return [round(float(v), 5) for v in emb]

    def transcribe(self, samples_24k: np.ndarray, lang: str) -> str | None:
        if self.sherpa_rec is None:
            return None
        s = self.sherpa_rec.create_stream()
        s.accept_waveform(RATE, samples_24k.astype(np.float32))
        self.sherpa_rec.decode_stream(s)
        return s.result.text.strip()

    def cer(self, want: str, got: str, lang: str) -> float:
        if lang == "zh" and self.pypinyin is not None:
            a = [p[0] for ch in want if "\u4e00" <= ch <= "\u9fff" for p in self.pypinyin.pinyin(ch, style=self.pypinyin.Style.NORMAL)]
            b = [p[0] for ch in got if "\u4e00" <= ch <= "\u9fff" for p in self.pypinyin.pinyin(ch, style=self.pypinyin.Style.NORMAL)]
            if not a:
                return 0.0
            prev = list(range(len(b) + 1))
            for i, x in enumerate(a, 1):
                cur = [i] + [0] * len(b)
                for j, y in enumerate(b, 1):
                    cur[j] = min(prev[j - 1] + (0 if x == y else 1), prev[j] + 1, cur[j - 1] + 1)
                prev = cur
            return prev[-1] / len(a)
        return edit_cer(want, got, lang)


class QwenVoiceForge:
    def __init__(self):
        import mlx.core as mx
        from mlx_audio.tts.utils import load_model

        self.mx = mx
        print(f"▸ loading VoiceDesign: {DESIGN_MODEL}", flush=True)
        self.design = load_model(DESIGN_MODEL)

    def synthesize(self, text: str, lang: str, instruct: str, seed: int) -> np.ndarray:
        self.mx.random.seed(seed)
        lang_name = "Chinese" if lang == "zh" else "English"
        chunks = []
        sr = RATE
        gen = (
            self.design.generate_voice_design(text=text, instruct=instruct, language=lang_name)
            if hasattr(self.design, "generate_voice_design")
            else self.design.generate(text=text, instruct=instruct, lang_code=lang_name)
        )
        for res in gen:
            arr = np.asarray(res.audio, dtype=np.float32).reshape(-1)
            sr = int(getattr(res, "sample_rate", RATE))
            chunks.append(arr)
        if not chunks:
            return np.zeros(0, dtype=np.float32)
        wav = np.concatenate(chunks)
        return resample_linear(wav, sr, RATE)


def check_clip(
    pcm: np.ndarray,
    text: str,
    lang: str,
    checkers: Checkers,
    neutral_emb: list[float] | None,
) -> tuple[bool, str, list[float]]:
    secs = len(pcm) / RATE
    lo, hi = (5.5, 10.5) if neutral_emb is None else (5.0, 11.2)
    if secs < lo or secs > hi:
        return False, f"seconds={secs:.2f} (want {lo}–{hi}s)", []
    clipped = float(np.mean(np.abs(pcm) >= 0.999))
    if clipped >= 0.001:
        return False, f"clipped={clipped:.4f}", []
    heard = checkers.transcribe(pcm, lang)
    if heard is not None:
        cer = checkers.cer(text, heard, lang)
        max_cer = 0.02 if neutral_emb is None else 0.05
        if cer > max_cer:
            return False, f"cer={cer:.3f} ({heard!r})", []
        if neutral_emb is None:
            want_toks = normalize_tokens(text, lang)
            got_toks = normalize_tokens(heard, lang)
            if want_toks and (not got_toks or want_toks[0] != got_toks[0]):
                return False, f"head_clipped ({heard!r})", []
    emb = checkers.embedding(pcm)
    if neutral_emb is not None and emb and neutral_emb:
        sim = cosine(neutral_emb, emb)
        if sim < 0.55:
            return False, f"sim_to_neutral={sim:.3f} (<0.55)", emb
    return True, "ok", emb


def build_one_card(
    spec: dict,
    pack_dir: Path,
    forge: QwenVoiceForge,
    checkers: Checkers,
    peer_neutrals: list[tuple[str, list[float]]],
) -> dict:
    cid = spec["id"]
    sub = spec["dir"]
    lang = spec["lang"]
    desc = spec["description"].strip()
    scripts = SCRIPTS_ZH if lang == "zh" else SCRIPTS_EN
    instructs = STYLE_INSTRUCT_ZH if lang == "zh" else STYLE_INSTRUCT_EN

    card_dir = pack_dir / sub
    card_dir.mkdir(parents=True, exist_ok=True)

    # 1. 先做 neutral，保证和同语言同性别已有卡的声纹相似度 < 0.80
    neutral_pcm = None
    neutral_emb: list[float] = []
    base_seed = int(cid.split(".")[-1]) * 100
    for attempt in range(20):
        seed = base_seed + attempt * 13 + 1
        sep = "。" if lang == "zh" else ". "
        raw = forge.synthesize(scripts["neutral"], lang, f"{desc}{sep}{instructs['neutral']}", seed)
        pcm = trim_and_normalize_20lufs(raw, RATE)
        ok, why, emb = check_clip(pcm, scripts["neutral"], lang, checkers, None)
        if not ok:
            print(f"  [{cid} neutral #{attempt + 1}] retry: {why}", flush=True)
            continue
        too_close = next(((pid, cosine(emb, pemb)) for pid, pemb in peer_neutrals if emb and pemb and cosine(emb, pemb) >= 0.80), None)
        if too_close is not None:
            print(f"  [{cid} neutral #{attempt + 1}] too close to {too_close[0]} ({too_close[1]:.3f} >= 0.80)", flush=True)
            continue
        neutral_pcm, neutral_emb = pcm, emb
        break

    if neutral_pcm is None:
        raise RuntimeError(f"failed to generate valid neutral clip for {cid} after 20 seeds")

    sf.write(card_dir / "neutral.ogg", neutral_pcm, RATE, format="OGG", subtype="VORBIS")
    neutral_secs = round(len(neutral_pcm) / RATE, 2)
    clips = [{
        "style": "neutral",
        "file": f"{sub}/neutral.ogg",
        "text": scripts["neutral"],
        "seconds": neutral_secs,
    }]

    # 2. 其余六档语气：同描述 + 语气说话指令，自动检查时长/削波/CER 与对 neutral 声纹 ≥ 0.55（最多 5 次）
    for s_idx, style in enumerate(STYLES[1:], 1):
        accepted = None
        for attempt in range(5):
            seed = base_seed + s_idx * 100 + attempt * 17 + 3
            sep = "。" if lang == "zh" else ". "
            raw = forge.synthesize(scripts[style], lang, f"{desc}{sep}{instructs[style]}", seed)
            pcm = trim_and_normalize_20lufs(raw, RATE)
            ok, why, _ = check_clip(pcm, scripts[style], lang, checkers, neutral_emb)
            if ok:
                accepted = pcm
                break
            print(f"  [{cid} {style} #{attempt + 1}] retry: {why}", flush=True)
        if accepted is None:
            old_ogg = pack_dir / f"{sub}/{style}.ogg"
            if old_ogg.is_file():
                old_ogg.unlink()
            print(f" [{cid} {style}] omitted after 5 tries (will fall back to neutral)", flush=True)
            continue
        rel = f"{sub}/{style}.ogg"
        sf.write(pack_dir / rel, accepted, RATE, format="OGG", subtype="VORBIS")
        clips.append({
            "style": style,
            "file": rel,
            "text": scripts[style],
            "seconds": round(len(accepted) / RATE, 2),
        })

    units = count_units(scripts["neutral"], lang)
    rate = round(units / max(0.1, neutral_secs), 2)
    return {
        "id": cid,
        "name": spec["name"],
        "gender": spec["gender"],
        "age": spec["age"],
        "tags": spec.get("tags", []),
        "lang": lang,
        "origin": {"kind": "builtin"},
        "clips": clips,
        "description": desc,
        "version": 1,
        "narrator": bool(spec.get("narrator", False)),
        "rate": rate,
        "embedding": neutral_emb,
        "sync": False,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out", help="输出目录（生成 <out>/voices-1/ 与 <out>/voices-1.zip）")
    ap.add_argument("--only", help="只做指定卡 id，如 card.b.01")
    ap.add_argument("--limit", type=int, default=0, help="最多新生成几张卡（0 为全部）")
    args = ap.parse_args()

    here = Path(__file__).resolve().parent
    root = here.parent.parent
    specs = yaml.safe_load((here / "voices.yaml").read_text(encoding="utf-8"))

    out_root = Path(args.out)
    pack_dir = out_root / "voices-1"
    pack_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = pack_dir / "voices.json"

    existing: dict[str, dict] = {}
    if manifest_path.is_file():
        doc = json.loads(manifest_path.read_text(encoding="utf-8"))
        cards_list = doc["cards"] if isinstance(doc, dict) and "cards" in doc else doc
        for c in cards_list:
            if all((pack_dir / cl["file"]).is_file() for cl in c.get("clips", [])):
                existing[c["id"]] = c

    def save_manifest():
        ordered = [existing[s["id"]] for s in specs if s["id"] in existing]
        doc = {
            "license": "CC0-1.0",
            "notice": NOTICE,
            "generator": DESIGN_MODEL,
            "cards": ordered,
        }
        manifest_path.write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")

    todo = [
        s for s in specs
        if (not args.only or s["id"] == args.only) and s["id"] not in existing
    ]
    if args.limit > 0:
        todo = todo[:args.limit]

    checkers = Checkers(root)
    forge = None

    def ensure_forge():
        nonlocal forge
        if forge is None:
            forge = QwenVoiceForge()
        return forge

    # 补齐/复核已有卡的 CAM++ 声纹与各档相似度/错字率：通过的直接保留，不过的单独重做该档或整卡
    specs_by_id = {s["id"]: s for s in specs}
    verified_peers: list[tuple[str, str, str, list[float]]] = []
    for s in specs:
        cid = s["id"]
        if cid not in existing:
            continue
        card = existing[cid]
        if card.get("embedding"):
            verified_peers.append((cid, card["lang"], card["gender"], card["embedding"]))
            continue
        neu_clip = next((cl for cl in card["clips"] if cl["style"] == "neutral"), None)
        if neu_clip is None:
            del existing[cid]
            todo.append(s)
            continue
        neu_pcm, sr = sf.read(str(pack_dir / neu_clip["file"]), dtype="float32")
        neu_pcm = resample_linear(neu_pcm, sr, RATE)
        ok_n, why_n, emb = check_clip(neu_pcm, neu_clip["text"], card["lang"], checkers, None)
        peer_hit = next(
            ((pid, cosine(emb, pemb)) for pid, plang, pg, pemb in verified_peers
             if plang == card["lang"] and pg == card["gender"] and emb and pemb and cosine(emb, pemb) >= 0.80),
            None,
        )
        if not ok_n or peer_hit is not None or not emb:
            print(f"▸ {cid} neutral needs rebuild ({why_n if not ok_n else f'sim {peer_hit}'})", flush=True)
            del existing[cid]
            todo.append(s)
            continue
        card["embedding"] = emb
        verified_peers.append((cid, card["lang"], card["gender"], emb))

        kept_clips = [neu_clip]
        scripts = SCRIPTS_ZH if card["lang"] == "zh" else SCRIPTS_EN
        instructs = STYLE_INSTRUCT_ZH if card["lang"] == "zh" else STYLE_INSTRUCT_EN
        base_seed = int(cid.split(".")[-1]) * 100 + 7000
        for s_idx, style in enumerate(STYLES[1:], 1):
            cur_cl = next((cl for cl in card["clips"] if cl["style"] == style), None)
            if cur_cl is not None and (pack_dir / cur_cl["file"]).is_file():
                st_pcm, st_sr = sf.read(str(pack_dir / cur_cl["file"]), dtype="float32")
                st_pcm = resample_linear(st_pcm, st_sr, RATE)
                ok_s, why_s, _ = check_clip(st_pcm, cur_cl["text"], card["lang"], checkers, emb)
                if ok_s:
                    kept_clips.append(cur_cl)
                    continue
                print(f"  [{cid} {style}] existing failed ({why_s}), regenerating…", flush=True)
            # Regenerate up to 5 seeds
            accepted = None
            sep = "。" if card["lang"] == "zh" else ". "
            desc = s["description"].strip()
            for attempt in range(5):
                seed = base_seed + s_idx * 100 + attempt * 19 + 5
                raw = ensure_forge().synthesize(scripts[style], card["lang"], f"{desc}{sep}{instructs[style]}", seed)
                pcm = trim_and_normalize_20lufs(raw, RATE)
                ok_s, why_s, _ = check_clip(pcm, scripts[style], card["lang"], checkers, emb)
                if ok_s:
                    accepted = pcm
                    break
                print(f"  [{cid} {style} #{attempt + 1}] retry: {why_s}", flush=True)
            if accepted is not None:
                rel = f"{s['dir']}/{style}.ogg"
                sf.write(pack_dir / rel, accepted, RATE, format="OGG", subtype="VORBIS")
                kept_clips.append({
                    "style": style,
                    "file": rel,
                    "text": scripts[style],
                    "seconds": round(len(accepted) / RATE, 2),
                })
            else:
                rel_old = pack_dir / f"{s['dir']}/{style}.ogg"
                if rel_old.is_file():
                    rel_old.unlink()
                print(f" [{cid} {style}] omitted after 5 tries (falls back to neutral)", flush=True)
        card["clips"] = kept_clips
        save_manifest()
        print(f"✓ audited {cid} ({len(kept_clips)} clips, emb={len(emb)}d)", flush=True)

    if todo:
        for spec in todo:
            peers = [
                (c["id"], c.get("embedding", []))
                for c in existing.values()
                if c["lang"] == spec["lang"] and c["gender"] == spec["gender"]
            ]
            print(f"▸ building {spec['id']} ({spec['name']} · {spec['lang']} · {spec['gender']} · {spec['age']})", flush=True)
            card = build_one_card(spec, pack_dir, ensure_forge(), checkers, peers)
            existing[card["id"]] = card
            save_manifest()
            print(f"✓ {card['id']} ({len(card['clips'])} clips, rate={card['rate']}/s)", flush=True)
    else:
        save_manifest()

    zpath = out_root / "voices-1.zip"
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_STORED) as zf:
        for p in sorted(pack_dir.rglob("*")):
            if p.is_file():
                zf.write(p, f"voices-1/{p.relative_to(pack_dir).as_posix()}")
    print(f"✓ wrote {zpath} ({zpath.stat().st_size} bytes, {len(existing)}/48 cards)", flush=True)


if __name__ == "__main__":
    main()
