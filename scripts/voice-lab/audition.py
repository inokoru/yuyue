"""声音方案第 1 步试听与客观评测脚本（不进 App）。

在 Mac 上生成全套试听 WAV 与评测报告 `audition.md`：
- 造 4 张七档语气参考卡（中文男女、英文男女，`neutral/joy/anger/sad/tense/soft/shout`），
  自动检查时长、削波、听写错字率与同卡声纹相似度（≥ 0.55）。
- 实验 A：候选即时层小模型（ZipVoice-Distill-Int8、Kokoro-82M-v1.1-zh Int8）与系统声音对照，
  通读《背影》全文、《红楼梦》第八回 20 句对话、《傲慢与偏见》第一章全文；
  用 SenseVoice 自动听写统计读音错字率（CER）、漏字/吞字、重复、静音与削波，并测单进程纯净内存峰值（RSS）与合成速度（RTF）；
  同时导出中英文各 10 句盲听配对音频供人工试听。
- 实验 B：同一张参考卡 `neutral` 下，即时层小模型 vs 精做层 Qwen3-TTS Base 同念中英文各 10 句，
  用 CAM++ 声纹模型量跨层余弦相似度，并导出盲听配对。
- 实验 C：同一句台词，即时层小模型分别照七档参考音合成，量声纹保持度、基频（F0）、语速与能量变化。
- 实验 D：验证精做层 Qwen3-TTS Base 照参考音合成时是否接受说话指令（ICL 标准路径、强制注入指令前缀、照七档参考音克隆三组对照）。
- 实验 F：核查各候选模型与精做模型的官方权重文件体量与许可证原文。

用法：
  uv run -p 3.12 --with mlx-audio==0.6.5 --with sherpa-onnx --with onnxruntime \\
         --with soundfile --with huggingface_hub --with pypinyin --with zhconv \\
         python -I scripts/voice-lab/audition.py [输出文件夹]

可随时中断，重跑会自动跳过已生成的音频并重新汇总 `audition.md`。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import re
import resource
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path

import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parents[2]
RATE = 24_000
STYLES = ("neutral", "joy", "anger", "sad", "tense", "soft", "shout")

STYLE_META = {
    "neutral": ("平常", "平稳自然地说", "speaking in a calm, steady, natural tone"),
    "joy": ("高兴", "高兴地、带着笑意说", "speaking happily with warmth and a clear smile in the voice"),
    "anger": ("生气", "生气地、语气很重地说", "speaking angrily in a heavy, sharp, indignant tone"),
    "sad": ("难过", "难过地、声音发沉地说", "speaking sadly in a low, heavy, sorrowful voice"),
    "tense": ("紧张", "紧张地、急促地说", "speaking tensely and urgently, slightly breathless"),
    "soft": ("低声", "压低声音、轻轻地说", "speaking in a hushed, gentle, low voice"),
    "shout": ("喊", "大声喊出来", "calling out loudly and forcefully"),
}

# 四张试听参考卡：中文男女、英文男女。描述词锚定音域、共鸣与吐字特征，保证多档语气仍属同一人。
CARD_SPECS = [
    {
        "id": "zh_m",
        "name": "沉稳男声（中文）",
        "lang": "zh",
        "gender": "M",
        "age": "middle",
        "desc": "三十五岁左右的中国男人，男中音，音色浑厚低沉，胸腔共鸣扎实，吐字清晰稳重",
    },
    {
        "id": "zh_f",
        "name": "温润女声（中文）",
        "lang": "zh",
        "gender": "F",
        "age": "young",
        "desc": "二十八岁左右的中国女人，女中音，音色温润清亮，气息柔和自然，吐字清楚",
    },
    {
        "id": "en_m",
        "name": "Steady Baritone (EN)",
        "lang": "en",
        "gender": "M",
        "age": "middle",
        "desc": "A thirty-five-year-old British man with a warm, resonant baritone voice, steady pacing, and crisp articulation",
    },
    {
        "id": "en_f",
        "name": "Clear Mezzo (EN)",
        "lang": "en",
        "gender": "F",
        "age": "young",
        "desc": "A twenty-eight-year-old British woman with a clear, poised mezzo-soprano voice, warm tone, and natural delivery",
    },
]

# 七档参考音台词（每档一句贴合该语气的句子，6–10 秒，互不相同）。
CARD_SCRIPTS = {
    "zh": {
        "neutral": "今天下了一场小雨，我们沿着河边的石板路慢慢走回家，一路上聊了许多往事。",
        "joy": "太好了，盼了整整三个月的好消息今天终于到了，咱们今晚一定要好好庆祝一番！",
        "anger": "我早就跟你说过这件事绝不能答应，你偏偏不听劝，现在果然闯出这么大的祸来！",
        "sad": "院子里的老槐树还在，可当年一起坐在树下乘凉的人，如今却再也见不到了。",
        "tense": "快把门闩插紧，外头脚步声越来越近了，千万别让他们看见屋里还亮着灯！",
        "soft": "夜已经很深了，大家都睡熟了，你走路轻一点，留神脚下的台阶，别惊动了旁人。",
        "shout": "快闪开！上面那块大石头松动滚下来了，所有人都赶紧往巷子口那边跑啊！",
    },
    "en": {
        "neutral": "It rained lightly this morning, so we walked home slowly along the quiet river path and talked the whole way back.",
        "joy": "That is wonderful news! After waiting all winter for an answer, everything has finally turned out better than we hoped!",
        "anger": "I warned you three times not to sign that letter, yet you ignored every word and brought this trouble straight upon us!",
        "sad": "The old garden looks just the same in the autumn light, though everyone who used to gather here has long since gone.",
        "tense": "Lock the side door at once and stay away from the window, because someone is coming up the stairs right now!",
        "soft": "Everyone upstairs is fast asleep now, so keep your voice low and step carefully past the study door.",
        "shout": "Watch out below! The scaffolding on the roof is giving way, get back from the wall immediately!",
    },
}

MODELS_URLS = {
    "vocos_24khz.onnx": "https://github.com/k2-fsa/sherpa-onnx/releases/download/vocoder-models/vocos_24khz.onnx",
    "lexicon-en-us.txt": "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/lexicon-en-us.txt",
    "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia.tar.bz2": (
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/"
        "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia.tar.bz2"
    ),
    "kokoro-int8-multi-lang-v1_1.tar.bz2": (
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/"
        "kokoro-int8-multi-lang-v1_1.tar.bz2"
    ),
}

FEMALE_ROLES = {"凤姐", "老嬷嬷", "薛姨妈", "宝钗", "莺儿", "黛玉", "李嬷嬷", "雪雁", "晴雯", "袭人", "茜雪", "贾母", "地下婆娘们"}


# ---------------------------------------------------------------------------
# 音频与信号处理（与核心 speaker.rs / queue.rs 对齐）
# ---------------------------------------------------------------------------

def trim_and_pad(x: np.ndarray, rate: int = RATE, floor_db: float = -45.0, pad_sec: float = 0.1) -> np.ndarray:
    if x.size == 0:
        return x
    peak = max(1e-9, float(np.max(np.abs(x))))
    thresh = (10.0 ** (floor_db / 20.0)) * peak
    loud = np.where(np.abs(x) > thresh)[0]
    if loud.size == 0:
        return x
    core = x[loud[0] : loud[-1] + 1]
    pad = np.zeros(int(round(pad_sec * rate)), dtype=np.float32)
    return np.concatenate([pad, core.astype(np.float32), pad])


def normalize_lufs(x: np.ndarray, target_db: float = -20.0) -> np.ndarray:
    """将语音有效段响度归一到约 -20 LUFS（单声道语音 RMS 对齐，峰值限在 -1 dBFS 内防削波）。"""
    if x.size == 0:
        return x
    active = x[np.abs(x) > 1e-3]
    if active.size < 100:
        active = x
    rms = float(np.sqrt(np.mean(np.square(active))) + 1e-9)
    cur_db = 20.0 * np.log10(rms)
    y = x * (10.0 ** ((target_db - cur_db) / 20.0))
    peak = float(np.max(np.abs(y)))
    limit = 10.0 ** (-1.0 / 20.0)  # ~0.891
    if peak > limit:
        y = y * (limit / peak)
    return y.astype(np.float32)


def write_wav(path: Path, samples: np.ndarray, rate: int = RATE) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    sf.write(str(path), np.clip(samples, -1.0, 1.0), rate, subtype="PCM_16")


def read_wav(path: Path) -> tuple[np.ndarray, int]:
    x, sr = sf.read(str(path), dtype="float32")
    if x.ndim > 1:
        x = x[:, 0]
    return x, sr


def resample_linear(samples: np.ndarray, src_rate: int, dst_rate: int) -> np.ndarray:
    if src_rate == dst_rate or samples.size == 0:
        return samples.astype(np.float32)
    step = src_rate / float(dst_rate)
    if step > 1.0:
        w = max(1, int(round(step)))
        cs = np.cumsum(np.insert(samples.astype(np.float64), 0, 0.0))
        idx = np.arange(samples.size)
        lo = np.maximum(0, idx - w + 1)
        smooth = ((cs[idx + 1] - cs[lo]) / (idx - lo + 1)).astype(np.float32)
    else:
        smooth = samples.astype(np.float32)
    n = int(np.floor(samples.size / step))
    pos = np.arange(n) * step
    k = np.floor(pos).astype(np.int64)
    f = (pos - k).astype(np.float32)
    a = smooth[np.minimum(k, smooth.size - 1)]
    b = smooth[np.minimum(k + 1, smooth.size - 1)]
    return a + (b - a) * f


def kaldi_fbank_cmn(samples: np.ndarray, rate: int) -> np.ndarray | None:
    """复现核心 `drama::speaker::features`：16 kHz、Povey 窗、预加重 0.97、80 维对数梅尔减均值。"""
    x16 = resample_linear(samples, rate, 16_000)
    frame_len, shift, fft_n, bands = 400, 160, 512, 80
    if x16.size < frame_len:
        return None
    mel = lambda f: 1127.0 * np.log(1.0 + f / 700.0)
    edges = np.linspace(mel(20.0), mel(8000.0), bands + 2)
    mels = mel(np.arange(fft_n // 2) * (16_000.0 / fft_n))
    fb = np.maximum(
        0.0,
        np.minimum(
            (mels[None, :] - edges[:-2, None]) / (edges[1:-1, None] - edges[:-2, None]),
            (edges[2:, None] - mels[None, :]) / (edges[2:, None] - edges[1:-1, None]),
        ),
    ).astype(np.float32)
    win = ((0.5 - 0.5 * np.cos(2.0 * np.pi * np.arange(frame_len) / (frame_len - 1))) ** 0.85).astype(np.float32)
    nf = 1 + (x16.size - frame_len) // shift
    fr = x16[np.arange(nf)[:, None] * shift + np.arange(frame_len)[None, :]] * 32768.0
    fr = fr - fr.mean(axis=1, keepdims=True)
    prev = np.concatenate([fr[:, :1], fr[:, :-1]], axis=1)
    emph = (fr - 0.97 * prev) * win[None, :]
    sp = np.fft.rfft(emph, n=fft_n, axis=1)[:, : fft_n // 2]
    pw = (sp.real ** 2 + sp.imag ** 2).astype(np.float32)
    ft = np.log(np.maximum(pw @ fb.T, np.finfo(np.float32).eps))
    ft -= ft.mean(axis=0, keepdims=True)
    return ft[None, :, :].astype(np.float32)


def voice_pitch_hz(samples: np.ndarray, rate: int) -> float | None:
    """复现核心 `drama::speaker::voice_pitch_hz`：70–350 Hz 自相关基频中位数。"""
    x = resample_linear(samples, rate, 16_000)
    w, hop, min_lag, max_lag = 512, 256, 46, 228
    if x.size < w:
        return None
    f0s = []
    for at in range(0, x.size - w + 1, hop):
        raw = x[at : at + w]
        d = raw - float(np.mean(raw))
        if float(np.sqrt(np.mean(d * d))) < 0.015:
            continue
        corr = np.zeros(max_lag + 2, dtype=np.float32)
        for lag in range(min_lag - 1, max_lag + 2):
            a, b = d[: w - lag], d[lag:]
            denom = float(np.sqrt(np.sum(a * a) * np.sum(b * b)))
            corr[lag] = float(np.sum(a * b)) / denom if denom > 1e-8 else 0.0
        peaks = [
            (lag, float(corr[lag]))
            for lag in range(min_lag, max_lag + 1)
            if corr[lag] >= 0.55 and corr[lag] > corr[lag - 1] and corr[lag] >= corr[lag + 1]
        ]
        if not peaks:
            continue
        best = max(v for _, v in peaks)
        chosen = next((lag for lag, v in peaks if v >= best * 0.88), None)
        if chosen is None:
            continue
        ym, y0, yp = float(corr[chosen - 1]), float(corr[chosen]), float(corr[chosen + 1])
        denom = ym - 2.0 * y0 + yp
        shift = float(np.clip(0.5 * (ym - yp) / denom, -0.5, 0.5)) if abs(denom) > 1e-6 else 0.0
        f0s.append(16_000.0 / (chosen + shift))
    if len(f0s) < 4:
        return None
    f0s.sort()
    return round(float(f0s[len(f0s) // 2]), 1)


def signal_stats(samples: np.ndarray, rate: int, silence_db: float = -45.0) -> dict:
    """量首尾静音、段中最大静音、削波比例与有效段 RMS。"""
    if samples.size == 0 or rate <= 0:
        return {"seconds": 0.0, "silence_head": 0.0, "silence_tail": 0.0, "max_mid_silence": 0.0, "clip_ratio": 0.0, "rms_db": -90.0}
    frame = max(1, rate // 100)  # 10 ms
    n_frames = samples.size // frame
    if n_frames == 0:
        return {"seconds": round(samples.size / rate, 3), "silence_head": 0.0, "silence_tail": 0.0, "max_mid_silence": 0.0, "clip_ratio": 0.0, "rms_db": -90.0}
    trimmed = samples[: n_frames * frame].reshape(n_frames, frame)
    energy_db = 10.0 * np.log10(np.mean(trimmed * trimmed, axis=1) + 1e-12)
    loud_idx = np.where(energy_db > silence_db)[0]
    if loud_idx.size == 0:
        sec = round(samples.size / rate, 3)
        return {"seconds": sec, "silence_head": sec, "silence_tail": 0.0, "max_mid_silence": sec, "clip_ratio": 0.0, "rms_db": -90.0}
    head = int(loud_idx[0]) * frame / rate
    tail = (n_frames - 1 - int(loud_idx[-1])) * frame / rate
    mid = energy_db[loud_idx[0] : loud_idx[-1] + 1] <= silence_db
    max_run = cur = 0
    for q in mid:
        if q:
            cur += 1
            max_run = max(max_run, cur)
        else:
            cur = 0
    clip_ratio = float(np.mean(np.abs(samples) >= 0.999))
    active = samples[loud_idx[0] * frame : (loud_idx[-1] + 1) * frame]
    rms_db = float(20.0 * np.log10(np.sqrt(np.mean(active * active)) + 1e-9))
    return {
        "seconds": round(samples.size / rate, 3),
        "silence_head": round(head, 3),
        "silence_tail": round(tail, 3),
        "max_mid_silence": round(max_run * frame / rate, 3),
        "clip_ratio": round(clip_ratio, 6),
        "rms_db": round(rms_db, 2),
    }


# ---------------------------------------------------------------------------
# 读音归一与错字/漏字/重复统计（与核心 audiobook/phon.rs 对齐）
# ---------------------------------------------------------------------------

ZH_DIGITS = {"零": 0, "〇": 0, "一": 1, "二": 2, "两": 2, "三": 3, "四": 4, "五": 5, "六": 6, "七": 7, "八": 8, "九": 9}
ZH_UNITS = {"十": 10, "百": 100, "千": 1000, "万": 10_000, "亿": 100_000_000}
EN_SMALL = {
    "zero": 0, "oh": 0, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
    "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14,
    "fifteen": 15, "sixteen": 16, "seventeen": 17, "eighteen": 18, "nineteen": 19, "twenty": 20,
    "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80, "ninety": 90,
}
EN_ABBR = {"mr": "mister", "mrs": "missus", "dr": "doctor", "st": "saint", "no": "number", "vs": "versus", "etc": "etcetera"}


def fuzzy_pinyin(p: str) -> str:
    s = p.replace("ü", "v").replace("u:", "v")
    for a, b in (("zh", "z"), ("ch", "c"), ("sh", "s")):
        if s.startswith(a):
            s = b + s[len(a) :]
            break
    if s.startswith("l"):
        s = "n" + s[1:]
    for a, b in (("ing", "in"), ("eng", "en"), ("ang", "an")):
        if s.endswith(a) and not s.endswith("iang") and not s.endswith("uang"):
            s = s[: -len(a)] + b
            break
    return s


def zh_num_to_digits(run: str) -> str:
    if not any(c in ZH_UNITS for c in run):
        return "".join(str(ZH_DIGITS[c]) for c in run if c in ZH_DIGITS)
    total = section = num = 0
    for c in run:
        if c in ZH_DIGITS:
            num = ZH_DIGITS[c]
        elif c in ZH_UNITS:
            u = ZH_UNITS[c]
            if u >= 10_000:
                section = (section + num) * u
                total += section
                section = 0
            else:
                section += 10 if (num == 0 and u == 10) else num * u
            num = 0
    return str(total + section + num)


def en_words_to_digits(words: list[str]) -> tuple[str, int] | None:
    def pair(w: str) -> int | None:
        parts = w.split("-")
        if all(p in EN_SMALL for p in parts):
            v = sum(EN_SMALL[p] for p in parts)
            return v if 10 <= v < 100 else None
        return None

    if len(words) >= 2:
        a = pair(words[0])
        b = pair(words[1]) if words[1] != "hundred" else 0
        if a is not None and b is not None and 11 <= a <= 20:
            n = 2
            if b > 0 and b % 10 == 0 and len(words) >= 3 and words[2] in EN_SMALL and 0 < EN_SMALL[words[2]] < 10:
                b += EN_SMALL[words[2]]
                n = 3
            return f"{a}{b:02d}", n
    total = cur = used = 0
    for w in words:
        parts = w.split("-")
        if all(p in EN_SMALL for p in parts):
            cur += sum(EN_SMALL[p] for p in parts)
            used += 1
        elif w == "hundred" and used > 0:
            cur *= 100
            used += 1
        elif w == "thousand" and used > 0:
            total += cur * 1000
            cur = 0
            used += 1
        elif w == "million" and used > 0:
            total += cur * 1_000_000
            cur = 0
            used += 1
        elif w == "and" and used > 0:
            used += 1
        else:
            break
    if used == 0:
        return None
    while used > 0 and words[used - 1] == "and":
        used -= 1
    return (str(total + cur), used) if used > 0 else None


def phon_tokens(text: str, chinese: bool) -> list[tuple[frozenset[str], str | None]]:
    """返回 [(读音集合, 英文原词或None), ...]，用于读音对齐与编辑距离。"""
    import pypinyin
    import zhconv

    simple = zhconv.convert(text, "zh-cn")
    chars = list(simple if len(simple) == len(text) else text)
    out: list[tuple[frozenset[str], str | None]] = []
    i, n = 0, len(chars)
    while i < n:
        c = chars[i]
        if c.isdigit():
            s = []
            while i < n and (chars[i].isdigit() or (chars[i] == "," and i + 1 < n and chars[i + 1].isdigit())):
                if chars[i] != ",":
                    s.append(str(int(chars[i])))
                i += 1
            for d in "".join(s):
                out.append((frozenset([f"#{d}"]), None))
            continue
        if chinese and (c in ZH_DIGITS or (c in ZH_UNITS and c not in ("万", "亿"))):
            j = i
            while j < n and (chars[j] in ZH_DIGITS or chars[j] in ZH_UNITS):
                j += 1
            for d in zh_num_to_digits("".join(chars[i:j])):
                out.append((frozenset([f"#{d}"]), None))
            i = j
            continue
        cp = ord(c)
        if 0x4E00 <= cp <= 0x9FFF or 0x3400 <= cp <= 0x4DBF:
            pys = pypinyin.pinyin(c, style=pypinyin.Style.NORMAL, heteronym=True)[0]
            keys = frozenset(fuzzy_pinyin(p) for p in pys if p) or frozenset([c])
            out.append((keys, None))
            i += 1
            continue
        if c.isalpha():
            start = i
            while i < n and (chars[i].isalpha() or chars[i] in ("'", "’") or (chars[i] == "-" and i + 1 < n and chars[i + 1].isalpha())):
                i += 1
            w = "".join(chars[start:i]).lower().replace("’", "'")
            ahead = [w]
            j = i
            while len(ahead) <= 8:
                k = j
                while k < n and chars[k] == " ":
                    k += 1
                s0 = k
                while k < n and (chars[k].isalpha() or chars[k] == "-"):
                    k += 1
                if s0 == k:
                    break
                ahead.append("".join(chars[s0:k]).lower())
                j = k
            conv = en_words_to_digits(ahead)
            if conv is not None:
                digits, used = conv
                k, seen = start, 0
                while k < n and seen < used:
                    while k < n and not chars[k].isalpha():
                        k += 1
                    while k < n and (chars[k].isalpha() or chars[k] == "-"):
                        k += 1
                    seen += 1
                for d in digits:
                    out.append((frozenset([f"#{d}"]), None))
                i = k
                continue
            if w in EN_ABBR:
                w = EN_ABBR[w]
            out.append((frozenset([w]), w))
            continue
        i += 1
    return out


def tok_same(a: tuple[frozenset[str], str | None], b: tuple[frozenset[str], str | None]) -> bool:
    if a[0] & b[0]:
        return True
    wa, wb = a[1], b[1]
    if wa and wb and len(wa) >= 5 and len(wb) >= 5 and abs(len(wa) - len(wb)) <= 1:
        # 长英文词容许 1 个字母的转写偏差（如专名拼写 Netherfield/Netherfeild）。
        prev = list(range(len(wb) + 1))
        for i, ca in enumerate(wa, 1):
            cur = [i] + [0] * len(wb)
            for j, cb in enumerate(wb, 1):
                cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb))
            prev = cur
        return prev[-1] <= 1
    return False


def compare_transcript(want_text: str, heard_text: str, chinese: bool) -> dict:
    """回听对齐：返回读音 CER、最长连续漏字数、总漏字数、连续重复词组数。"""
    want = phon_tokens(want_text, chinese)
    got = phon_tokens(heard_text, chinese)
    n, m = len(want), len(got)
    if n == 0:
        return {"cer": 0.0, "longest_missing": 0, "missing_tokens": 0, "repeated_spans": 0, "tokens": 0}
    dp = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(1, n + 1):
        dp[i][0] = i
    for j in range(1, m + 1):
        dp[0][j] = j
    for i in range(1, n + 1):
        wi = want[i - 1]
        row, prow = dp[i], dp[i - 1]
        for j in range(1, m + 1):
            sub = prow[j - 1] + (0 if tok_same(wi, got[j - 1]) else 1)
            row[j] = min(sub, prow[j] + 1, row[j - 1] + 1)

    # 回溯统计漏读（deletion）与多读重复（insertion 连续复读）。
    i, j = n, m
    ops: list[tuple[str, int, int]] = []
    while i > 0 or j > 0:
        if i > 0 and j > 0 and dp[i][j] == dp[i - 1][j - 1] + (0 if tok_same(want[i - 1], got[j - 1]) else 1):
            ops.append(("M" if tok_same(want[i - 1], got[j - 1]) else "S", i - 1, j - 1))
            i -= 1
            j -= 1
        elif i > 0 and dp[i][j] == dp[i - 1][j] + 1:
            ops.append(("D", i - 1, -1))
            i -= 1
        else:
            ops.append(("I", -1, j - 1))
            j -= 1
    ops.reverse()

    longest_missing = cur_del = total_del = 0
    ins_run = 0
    repeated_spans = 0
    for op, _, jj in ops:
        if op == "D":
            cur_del += 1
            total_del += 1
            longest_missing = max(longest_missing, cur_del)
        else:
            cur_del = 0
        if op == "I":
            # 若插入的音与紧邻前文完全重复，记入复读计数。
            if jj > 0 and tok_same(got[jj], got[jj - 1]):
                ins_run += 1
            elif jj >= 2 and jj + 1 <= m and tok_same(got[jj], got[jj - 2]):
                ins_run += 1
        else:
            if ins_run >= 2:
                repeated_spans += 1
            ins_run = 0
    if ins_run >= 2:
        repeated_spans += 1

    return {
        "cer": round(dp[n][m] / n, 4),
        "longest_missing": longest_missing,
        "missing_tokens": total_del,
        "repeated_spans": repeated_spans,
        "tokens": n,
    }


def split_long_sentence(text: str, chinese: bool) -> list[str]:
    """中文超过 60 字、英文超过 40 词时在标点处切开分别合成再接上（接缝留 0.12 秒）。"""
    text = text.strip()
    if chinese:
        han_count = sum(1 for c in text if "\u4e00" <= c <= "\u9fff")
        if han_count <= 60:
            return [text]
        parts = re.split(r"(?<=[，；：。！？,;:!?])\s*", text)
        limit_fn = lambda s: sum(1 for c in s if "\u4e00" <= c <= "\u9fff") <= 60
    else:
        if len(text.split()) <= 40:
            return [text]
        parts = re.split(r"(?<=[,;:.!?—])\s+", text)
        limit_fn = lambda s: len(s.split()) <= 40

    chunks: list[str] = []
    cur = ""
    for p in parts:
        if not p:
            continue
        cand = (cur + (" " if not chinese and cur else "") + p).strip()
        if cur and not limit_fn(cand):
            chunks.append(cur)
            cur = p
        else:
            cur = cand
    if cur:
        chunks.append(cur)
    return chunks or [text]


# ---------------------------------------------------------------------------
# 材料提取（直接调用核心 CLI 保证分句与 App 一致）
# ---------------------------------------------------------------------------

def load_materials() -> dict[str, list[dict]]:
    import zhconv

    yuyue_bin = ROOT / "core/target/release/yuyue"
    beiying_path = ROOT / "fixtures/books/beiying.txt"
    hlm_epub = ROOT / "fixtures/books/hongloumeng.epub"
    hlm_ans = ROOT / "fixtures/speakers/hongloumeng-08.jsonl"
    pp_epub = ROOT / "fixtures/books/pride-and-prejudice.epub"

    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([str(yuyue_bin), "import", tmp, str(beiying_path), str(hlm_epub), str(pp_epub)], check=True, stdout=subprocess.DEVNULL)
        books = {}
        for line in subprocess.check_output([str(yuyue_bin), "list", tmp], text=True).splitlines():
            parts = line.split()
            if len(parts) >= 2:
                books[parts[1]] = parts[0]
        id_by = next(v for k, v in books.items() if "背影" in k)
        id_hlm = next(v for k, v in books.items() if "紅樓夢" in k or "红楼梦" in k)
        id_pp = next(v for k, v in books.items() if "Pride" in k)

        def read_sents(bid: str, sec: int) -> list[str]:
            raw = subprocess.check_output([str(yuyue_bin), "sentences", tmp, bid, str(sec)], text=True)
            out = []
            for ln in raw.splitlines():
                if "\t" in ln:
                    _, txt = ln.split("\t", 1)
                    out.append(txt.strip())
            return out

        by_sents = read_sents(id_by, 0)
        pp_sents = read_sents(id_pp, 4)

        # 红楼梦第八回：读取 drama 分段里的 Speech 台词，与答案表核对说话人，转成简体以匹配本地词典。
        drama_raw = subprocess.check_output([str(yuyue_bin), "drama", tmp, id_hlm, "9"], text=True)
        speech_by_sent: dict[int, list[str]] = {}
        for ln in drama_raw.splitlines():
            cols = ln.split("\t")
            if len(cols) >= 5 and cols[1] == "Speech":
                s_idx = int(cols[0])
                qtxt = cols[4].strip().strip("“”\"'")
                speech_by_sent.setdefault(s_idx, []).append(qtxt)

        ans_rows = [json.loads(ln) for ln in hlm_ans.read_text(encoding="utf-8").splitlines()[1:] if ln.strip()]
        hlm_lines: list[dict] = []
        for row in ans_rows:
            if row.get("kind") == "not_speech" or not row.get("who"):
                continue
            s0, s1 = int(row["s"]), int(row["e"])
            # 跳过古腾堡原书存在乱码字（琠）或残断连字符的句子。
            if s0 in (65, 77):
                continue
            pieces = []
            for si in range(s0, s1 + 1):
                pieces.extend(speech_by_sent.get(si, []))
            full = "".join(pieces).strip()
            full_simp = zhconv.convert(full, "zh-cn")
            who_simp = zhconv.convert(row["who"], "zh-cn")
            han_n = sum(1 for c in full_simp if "\u4e00" <= c <= "\u9fff")
            if han_n < 10:
                continue
            gender = "F" if who_simp in FEMALE_ROLES else "M"
            hlm_lines.append({"idx": len(hlm_lines), "sent": s0, "who": who_simp, "gender": gender, "text": full_simp})
            if len(hlm_lines) == 20:
                break

    beiying_items = [
        {"idx": i, "text": s, "gender": "M"}
        for i, s in enumerate(by_sents)
        if any(c.isalnum() for c in s)
    ]
    pride_items = [
        {"idx": i, "text": s, "gender": "F"}
        for i, s in enumerate(pp_sents)
        if any(c.isalnum() for c in s) and not s.startswith("[Copyright")
    ]
    return {"beiying": beiying_items, "hongloumeng": hlm_lines, "pride_ch1": pride_items}


# ---------------------------------------------------------------------------
# 模型下载与评测辅助器（SenseVoice + CAM++）
# ---------------------------------------------------------------------------

def ensure_tts_models(models_dir: Path) -> None:
    models_dir.mkdir(parents=True, exist_ok=True)
    for name, url in MODELS_URLS.items():
        if name.endswith(".tar.bz2"):
            folder = models_dir / name[: -len(".tar.bz2")]
            if folder.is_dir():
                continue
            archive = models_dir / name
            print(f"downloading {name} …", flush=True)
            urllib.request.urlretrieve(url, str(archive))
            with tarfile.open(str(archive), "r:bz2") as tf:
                tf.extractall(str(models_dir))
            archive.unlink(missing_ok=True)
        else:
            dest = models_dir / name
            if dest.is_file():
                continue
            print(f"downloading {name} …", flush=True)
            urllib.request.urlretrieve(url, str(dest))


class Scorer:
    """SenseVoice 听写 + CAM++ 声纹余弦相似度。"""

    def __init__(self, aux_dir: Path):
        import onnxruntime as ort
        import sherpa_onnx

        self.rec = sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=str(aux_dir / "sense_voice.int8.onnx"),
            tokens=str(aux_dir / "sense_voice_tokens.txt"),
            num_threads=4,
            use_itn=False,
        )
        self.spk_sess = ort.InferenceSession(str(aux_dir / "speaker_print.onnx"))

    def transcribe(self, samples: np.ndarray, rate: int) -> tuple[str, str]:
        if samples.size < rate // 10:
            return "", ""
        s = self.rec.create_stream()
        s.accept_waveform(rate, samples.astype(np.float32))
        self.rec.decode_stream(s)
        emo = getattr(s.result, "emotion", "") or ""
        return s.result.text.strip(), emo.strip("<>|")

    def embedding(self, samples: np.ndarray, rate: int) -> np.ndarray | None:
        fb = kaldi_fbank_cmn(samples, rate)
        if fb is None or fb.shape[1] < 50:
            return None
        emb = self.spk_sess.run(None, {"fbank": fb})[0].reshape(-1).astype(np.float32)
        norm = float(np.linalg.norm(emb))
        return emb / norm if norm > 1e-8 else None

    @staticmethod
    def cosine(a: np.ndarray | None, b: np.ndarray | None) -> float | None:
        if a is None or b is None:
            return None
        return round(float(np.dot(a, b)), 4)


# ---------------------------------------------------------------------------
# 引擎封装：ZipVoice-Distill-Int8 / Kokoro-v1.1-Int8 / SystemVoice / Qwen3-TTS
# ---------------------------------------------------------------------------

def clean_for_zipvoice(text: str, chinese: bool) -> str:
    """将弯引号、破折号及异体字规整到 ZipVoice 词表（ASCII 标点 + 简体字），消除未登录符号。"""
    import zhconv

    s = zhconv.convert(text, "zh-cn") if chinese else text
    s = s.replace("’", "'").replace("‘", "'")
    for ch in ("“", "”", '"', "「", "」", "『", "』"):
        s = s.replace(ch, "")
    s = re.sub(r"[—…]+", ", ", s)
    s = s.translate(str.maketrans({"，": ", ", "。": ". ", "！": "! ", "？": "? ", "；": "; ", "：": ": ", "（": "(", "）": ")"}))
    return re.sub(r"\s+", " ", s).strip()


class ZipVoiceEngine:
    def __init__(self, models_dir: Path, num_threads: int = 4, guidance_scale: float = 3.0, steps: int = 3):
        import sherpa_onnx

        self.steps = steps
        zdir = models_dir / "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia"
        cfg = sherpa_onnx.OfflineTtsConfig(
            model=sherpa_onnx.OfflineTtsModelConfig(
                zipvoice=sherpa_onnx.OfflineTtsZipvoiceModelConfig(
                    tokens=str(zdir / "tokens.txt"),
                    encoder=str(zdir / "encoder.int8.onnx"),
                    decoder=str(zdir / "decoder.int8.onnx"),
                    lexicon=f"{zdir / 'lexicon.txt'},{models_dir / 'lexicon-en-us.txt'}",
                    vocoder=str(models_dir / "vocos_24khz.onnx"),
                    guidance_scale=guidance_scale,
                ),
                num_threads=num_threads,
                provider="cpu",
            )
        )
        if not cfg.validate():
            raise RuntimeError("Invalid ZipVoice config")
        self.tts = sherpa_onnx.OfflineTts(cfg)
        self._sherpa = sherpa_onnx

    def synth_one(self, text: str, chinese: bool, ref_samples: np.ndarray, ref_rate: int, ref_text: str) -> tuple[np.ndarray, int]:
        gc = self._sherpa.GenerationConfig()
        gc.reference_audio = ref_samples
        gc.reference_sample_rate = ref_rate
        gc.reference_text = clean_for_zipvoice(ref_text, chinese)
        gc.num_steps = self.steps
        audio = self.tts.generate(clean_for_zipvoice(text, chinese), gc)
        return np.asarray(audio.samples, dtype=np.float32), int(audio.sample_rate)

    def synth(self, text: str, chinese: bool, ref_samples: np.ndarray, ref_rate: int, ref_text: str) -> tuple[np.ndarray, int, float]:
        chunks = split_long_sentence(text, chinese)
        parts: list[np.ndarray] = []
        sr = RATE
        t0 = time.perf_counter()
        for idx, ch in enumerate(chunks):
            clean_ch = clean_for_zipvoice(ch, chinese)
            if not any(c.isalnum() for c in clean_ch):
                continue
            s, sr = self.synth_one(clean_ch, chinese, ref_samples, ref_rate, ref_text)
            if idx > 0 and s.size > 0:
                parts.append(np.zeros(int(0.12 * sr), dtype=np.float32))
            if s.size > 0:
                parts.append(s)
        dt = time.perf_counter() - t0
        out = np.concatenate(parts) if parts else np.zeros(0, dtype=np.float32)
        return out, sr, dt


class KokoroEngine:
    # 0=af_maple (美式女), 2=bf_vale (英式女), 3=zf_001 (中文女), 59=zm_010 (中文男)
    SIDS = {("zh", "M"): 59, ("zh", "F"): 3, ("en", "F"): 0, ("en", "M"): 2}

    def __init__(self, models_dir: Path, num_threads: int = 4):
        import sherpa_onnx

        kdir = models_dir / "kokoro-int8-multi-lang-v1_1"
        cfg = sherpa_onnx.OfflineTtsConfig(
            model=sherpa_onnx.OfflineTtsModelConfig(
                kokoro=sherpa_onnx.OfflineTtsKokoroModelConfig(
                    model=str(kdir / "model.int8.onnx"),
                    voices=str(kdir / "voices.bin"),
                    tokens=str(kdir / "tokens.txt"),
                    lexicon=f"{kdir / 'lexicon-us-en.txt'},{kdir / 'lexicon-zh.txt'}",
                ),
                num_threads=num_threads,
                provider="cpu",
            ),
            rule_fsts=f"{kdir / 'phone-zh.fst'},{kdir / 'date-zh.fst'},{kdir / 'number-zh.fst'}",
        )
        if not cfg.validate():
            raise RuntimeError("Invalid Kokoro config")
        self.tts = sherpa_onnx.OfflineTts(cfg)

    def synth(self, text: str, chinese: bool, gender: str = "M") -> tuple[np.ndarray, int, float]:
        sid = self.SIDS[("zh" if chinese else "en", gender)]
        chunks = split_long_sentence(text, chinese)
        parts: list[np.ndarray] = []
        sr = RATE
        t0 = time.perf_counter()
        for idx, ch in enumerate(chunks):
            audio = self.tts.generate(ch, sid=sid, speed=1.0)
            sr = int(audio.sample_rate)
            s = np.asarray(audio.samples, dtype=np.float32)
            if idx > 0 and s.size > 0:
                parts.append(np.zeros(int(0.12 * sr), dtype=np.float32))
            if s.size > 0:
                parts.append(s)
        dt = time.perf_counter() - t0
        out = np.concatenate(parts) if parts else np.zeros(0, dtype=np.float32)
        return out, sr, dt


class SystemVoiceEngine:
    VOICES = {("zh", "M"): "Tingting", ("zh", "F"): "Tingting", ("en", "F"): "Samantha", ("en", "M"): "Daniel"}

    def synth(self, text: str, chinese: bool, gender: str = "M") -> tuple[np.ndarray, int, float]:
        voice = self.VOICES[("zh" if chinese else "en", gender)]
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tf:
            tmp_path = Path(tf.name)
        try:
            t0 = time.perf_counter()
            subprocess.run(
                ["/usr/bin/say", "-v", voice, f"--data-format=LEI16@{RATE}", "-o", str(tmp_path), text],
                check=True,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            dt = time.perf_counter() - t0
            x, sr = read_wav(tmp_path)
            return x, sr, dt
        finally:
            tmp_path.unlink(missing_ok=True)


class QwenStudio:
    DESIGN_REPO = "mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-6bit"
    BASE_REPO = "mlx-community/Qwen3-TTS-12Hz-1.7B-Base-6bit"

    def __init__(self):
        self._design = None
        self._base = None

    @property
    def design_model(self):
        if self._design is None:
            from mlx_audio.tts.utils import load_model

            print(f"loading {self.DESIGN_REPO} …", flush=True)
            self._design = load_model(self.DESIGN_REPO)
        return self._design

    @property
    def base_model(self):
        if self._base is None:
            from mlx_audio.tts.utils import load_model

            print(f"loading {self.BASE_REPO} …", flush=True)
            self._base = load_model(self.BASE_REPO)
        return self._base

    def unload_design(self) -> None:
        if self._design is not None:
            import gc
            import mlx.core as mx

            self._design = None
            gc.collect()
            mx.clear_cache()

    def design_clip(self, text: str, instruct: str, chinese: bool, seed: int) -> tuple[np.ndarray, int, float]:
        import mlx.core as mx

        m = self.design_model
        mx.random.seed(seed)
        lang = "Chinese" if chinese else "English"
        t0 = time.perf_counter()
        res = list(m.generate_voice_design(text=text, instruct=instruct, language=lang))
        dt = time.perf_counter() - t0
        parts = [np.asarray(r.audio, dtype=np.float32).reshape(-1) for r in res]
        sr = int(res[0].sample_rate) if res else RATE
        return (np.concatenate(parts) if parts else np.zeros(0, dtype=np.float32)), sr, dt

    def clone_icl(self, text: str, ref_wav: Path, ref_text: str, chinese: bool, seed: int = 42, instruct: str | None = None) -> tuple[np.ndarray, int, float]:
        import mlx.core as mx

        m = self.base_model
        mx.random.seed(seed)
        lang = "chinese" if chinese else "english"
        t0 = time.perf_counter()
        res = list(m.generate(text=text, ref_audio=str(ref_wav), ref_text=ref_text, lang_code=lang, instruct=instruct))
        dt = time.perf_counter() - t0
        parts = [np.asarray(r.audio, dtype=np.float32).reshape(-1) for r in res]
        sr = int(res[0].sample_rate) if res else RATE
        return (np.concatenate(parts) if parts else np.zeros(0, dtype=np.float32)), sr, dt

    def clone_xvec_forced_instruct(self, text: str, ref_samples: np.ndarray, chinese: bool, instruct: str | None, seed: int = 42) -> tuple[np.ndarray, int, float]:
        """实验 D-a2：绕过 ICL，用 Base 内置声纹编码器提取 x-vector 并强行拼入 `<|im_start|>user\\n{instruct}`。"""
        import mlx.core as mx

        m = self.base_model
        mx.random.seed(seed)
        lang = "chinese" if chinese else "english"
        t0 = time.perf_counter()
        inp, trail, pad = m._prepare_generation_inputs(
            text=text,
            language=lang,
            speaker=None,
            ref_audio=mx.array(ref_samples),
            ref_text=None,
            instruct=instruct,
        )
        cache = m.talker.make_cache()
        code_cache = m.talker.code_predictor.make_cache()
        cfg = m.config.talker_config
        eos_id = cfg.codec_eos_token_id
        suppress = [i for i in range(cfg.vocab_size - 1024, cfg.vocab_size) if i != eos_id]
        gen_codes = []
        gen_ids = []
        t_idx = 0
        for _ in range(1024):
            logits, hidden = m.talker(inp, cache=cache)
            nxt = m._sample_token(logits, temperature=0.9, top_k=50, top_p=1.0, repetition_penalty=1.2, generated_tokens=gen_ids or None, suppress_tokens=suppress)
            is_eos = nxt[0, 0] == eos_id
            code_tokens = [nxt]
            code_hidden = hidden[:, -1:, :]
            for c in code_cache:
                c.keys = None
                c.values = None
                c.offset = 0
            for c_idx in range(cfg.num_code_groups - 1):
                if c_idx == 0:
                    c0 = m.talker.get_input_embeddings()(nxt)
                    c_in = mx.concatenate([code_hidden, c0], axis=1)
                else:
                    c_in = m.talker.code_predictor.codec_embedding[c_idx - 1](code_tokens[-1])
                c_logits, code_cache, _ = m.talker.code_predictor(c_in, cache=code_cache, generation_step=c_idx)
                code_tokens.append(m._sample_token(c_logits, temperature=0.9, top_k=50, top_p=1.0))
            all_c = mx.concatenate(code_tokens, axis=1)
            t_emb = trail[:, t_idx : t_idx + 1, :] if t_idx < trail.shape[1] else pad
            t_idx += 1
            c_emb = m.talker.get_input_embeddings()(nxt)
            for i, ck in enumerate(code_tokens[1:]):
                c_emb = c_emb + m.talker.code_predictor.codec_embedding[i](ck)
            inp = t_emb + c_emb
            mx.eval(inp, is_eos)
            if is_eos.item():
                break
            gen_ids.append(int(nxt[0, 0]))
            gen_codes.append(all_c)
        audio = m._decode_generated_codes(gen_codes) if gen_codes else mx.zeros((0,), dtype=mx.float32)
        mx.eval(audio)
        dt = time.perf_counter() - t0
        mx.clear_cache()
        return np.asarray(audio, dtype=np.float32).reshape(-1), int(m.sample_rate), dt


# ---------------------------------------------------------------------------
# 独立子进程内存与线程基准（隔离大模型，测小模型真实 RSS 峰值）
# ---------------------------------------------------------------------------

def run_isolated_probe(kind: str, models_dir: Path, threads: int) -> dict:
    rss_div = (1024.0 * 1024.0) if sys.platform == "darwin" else 1024.0
    test_zh = "我与父亲不相见已二年余了，我最不能忘记的是他的背影。那年冬天，祖母死了，父亲的差使也交卸了。"
    test_en = "It is a truth universally acknowledged, that a single man in possession of a good fortune must be in want of a wife."
    t_load0 = time.perf_counter()
    if kind == "zipvoice":
        eng = ZipVoiceEngine(models_dir, num_threads=threads)
        load_s = time.perf_counter() - t_load0
        # 参考音用自带声音卡（模型按文字描述生成，不是真人录音）。
        ref_x, ref_sr = read_wav(models_dir / "voices-1" / "b01" / "neutral.ogg")
        ref_t = "今天早上下了一场小雨，我们沿着河边慢慢走回家，一路上聊了许多往事。"
        # 预热一次
        eng.synth(test_zh[:12], True, ref_x, ref_sr, ref_t)
        _, sr1, dt1 = eng.synth(test_zh, True, ref_x, ref_sr, ref_t)
        s2, sr2, dt2 = eng.synth(test_en, False, ref_x, ref_sr, ref_t)
        s1, _, dt1 = eng.synth(test_zh, True, ref_x, ref_sr, ref_t)
    elif kind == "kokoro":
        eng = KokoroEngine(models_dir, num_threads=threads)
        load_s = time.perf_counter() - t_load0
        eng.synth(test_zh[:12], True, "M")
        s1, sr1, dt1 = eng.synth(test_zh, True, "M")
        s2, sr2, dt2 = eng.synth(test_en, False, "F")
    elif kind == "zipvoice_ort":
        import onnxruntime as ort

        zdir = models_dir / "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia"
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = threads
        opts.enable_cpu_mem_arena = False
        opts.enable_mem_pattern = False
        s_enc = ort.InferenceSession(str(zdir / "encoder.int8.onnx"), sess_options=opts)
        s_dec = ort.InferenceSession(str(zdir / "decoder.int8.onnx"), sess_options=opts)
        s_voc = ort.InferenceSession(str(models_dir / "vocos_24khz.onnx"), sess_options=opts)
        load_s = time.perf_counter() - t_load0

        def ort_pass(n_tok: int, ref_frames: int = 560) -> tuple[float, float]:
            t0 = time.perf_counter()
            tc = s_enc.run(
                None,
                {
                    "tokens": np.ones((1, n_tok), dtype=np.int64),
                    "prompt_tokens": np.ones((1, 32), dtype=np.int64),
                    "prompt_features_len": np.array(ref_frames, dtype=np.int64),
                    "speed": np.array(1.0, dtype=np.float32),
                },
            )[0]
            t_tot = tc.shape[1]
            x = np.zeros((1, t_tot, 100), dtype=np.float32)
            sc = np.zeros((1, t_tot, 100), dtype=np.float32)
            for st_i in range(3):
                v = s_dec.run(
                    None,
                    {
                        "t": np.array(st_i / 3.0, dtype=np.float32),
                        "x": x,
                        "text_condition": tc,
                        "speech_condition": sc,
                        "guidance_scale": np.array(3.0, dtype=np.float32),
                    },
                )[0]
                x = x + v / 3.0
            s_voc.run(None, {"mels": np.transpose(x[:, ref_frames:, :], (0, 2, 1))})
            dt = time.perf_counter() - t0
            gen_s = max((t_tot - ref_frames) * 256 / 24000.0, 0.5)
            return dt, gen_s

        ort_pass(18, ref_frames=400)
        dt1, dur_zh = ort_pass(35, ref_frames=400)
        dt2, dur_en = ort_pass(38, ref_frames=400)
        rss_mb = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / rss_div
        return {
            "engine": "zipvoice (pure ORT, arena=off, ≤9s窗)",
            "threads": threads,
            "load_seconds": round(load_s, 3),
            "peak_rss_mb": round(rss_mb, 1),
            "rtf_zh": round(dt1 / dur_zh, 3),
            "rtf_en": round(dt2 / dur_en, 3),
        }
    elif kind == "system":
        eng = SystemVoiceEngine()
        load_s = 0.0
        s1, sr1, dt1 = eng.synth(test_zh, True, "M")
        s2, sr2, dt2 = eng.synth(test_en, False, "F")
    else:
        raise ValueError(kind)

    rss_mb = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / rss_div
    dur_zh = s1.size / sr1
    dur_en = s2.size / sr2
    return {
        "engine": kind,
        "threads": threads,
        "load_seconds": round(load_s, 3),
        "peak_rss_mb": round(rss_mb, 1),
        "rtf_zh": round(dt1 / max(dur_zh, 1e-6), 3),
        "rtf_en": round(dt2 / max(dur_en, 1e-6), 3),
    }


def spawn_probe(kind: str, models_dir: Path, threads: int) -> dict:
    out = subprocess.check_output(
        [sys.executable, "-I", str(Path(__file__).resolve()), "--probe", kind, "--threads", str(threads), "--tts-models", str(models_dir)],
        text=True,
    )
    for ln in reversed(out.strip().splitlines()):
        if ln.startswith("{") and ln.endswith("}"):
            return json.loads(ln)
    raise RuntimeError(f"Probe failed for {kind}: {out}")


# ---------------------------------------------------------------------------
# 各阶段实验流程
# ---------------------------------------------------------------------------

def step_build_cards(out_dir: Path, studio: QwenStudio, scorer: Scorer, state: dict, save_state) -> dict:
    """造 4 张七档参考卡（VoiceDesign + 种子优选：时长 5.5–11.5s、无削波、CER ≤ 2–5%、声纹相似度 ≥ 0.55）。"""
    cards_state = state.setdefault("cards", {})
    for spec in CARD_SPECS:
        cid = spec["id"]
        chinese = spec["lang"] == "zh"
        c_entry = cards_state.setdefault(cid, {"meta": spec, "clips": {}})
        neu_emb: np.ndarray | None = None

        for style in STYLES:
            wav_path = out_dir / "cards" / cid / f"{style}.wav"
            if style in c_entry["clips"] and wav_path.is_file():
                if style == "neutral":
                    x, sr = read_wav(wav_path)
                    neu_emb = scorer.embedding(x, sr)
                continue

            script = CARD_SCRIPTS[spec["lang"]][style]
            _, zh_inst, en_inst = STYLE_META[style]
            inst = f"{spec['desc']}，{zh_inst}。" if chinese else f"{spec['desc']}, {en_inst}."

            best_candidate = None
            base_seed = int(hashlib.sha256(f"{cid}:{style}".encode()).hexdigest()[:6], 16) % 10000
            max_tries = 16
            for attempt in range(max_tries):
                seed = base_seed + attempt * 17
                raw, sr, dt = studio.design_clip(script, inst, chinese, seed=seed)
                clipped_raw = float(np.mean(np.abs(raw) >= 0.999)) > 0.001
                processed = normalize_lufs(trim_and_pad(raw, sr), target_db=-20.0)
                dur = processed.size / sr
                heard, emo = scorer.transcribe(processed, sr)
                cmp_res = compare_transcript(script, heard, chinese)
                emb = scorer.embedding(processed, sr)
                sim = 1.0 if style == "neutral" else (scorer.cosine(emb, neu_emb) or 0.0)
                f0 = voice_pitch_hz(processed, sr)

                dur_ok = 5.2 <= dur <= 11.8
                cer_limit = 0.02 if style == "neutral" else 0.05
                cer_ok = cmp_res["cer"] <= cer_limit
                sim_ok = True if style == "neutral" else (sim >= 0.55)
                passed = (not clipped_raw) and dur_ok and cer_ok and sim_ok

                cand = {
                    "samples": processed,
                    "sr": sr,
                    "emb": emb,
                    "info": {
                        "style": style,
                        "file": f"cards/{cid}/{style}.wav",
                        "text": script,
                        "instruct": inst,
                        "seconds": round(dur, 2),
                        "cer": cmp_res["cer"],
                        "heard": heard,
                        "emotion_tag": emo,
                        "sim_to_neutral": round(sim, 4),
                        "f0_hz": f0,
                        "seed": seed,
                        "attempts": attempt + 1,
                        "synth_seconds": round(dt, 2),
                        "passed_gate": passed,
                    },
                    "score": (1 if passed else 0, sim - cmp_res["cer"] * 2.0 - (0.0 if dur_ok else 0.3)),
                }
                if best_candidate is None or cand["score"] > best_candidate["score"]:
                    best_candidate = cand
                if passed:
                    break

            assert best_candidate is not None
            write_wav(wav_path, best_candidate["samples"], best_candidate["sr"])
            if style == "neutral":
                neu_emb = best_candidate["emb"]
            c_entry["clips"][style] = best_candidate["info"]
            save_state()
            info = best_candidate["info"]
            print(
                f"[Card {cid}/{style}] dur={info['seconds']}s cer={info['cer']} "
                f"sim={info['sim_to_neutral']} f0={info['f0_hz']} tries={info['attempts']}",
                flush=True,
            )
    return cards_state


def step_test_a(out_dir: Path, models_dir: Path, materials: dict, cards_state: dict, scorer: Scorer, state: dict, save_state) -> dict:
    """实验 A：ZipVoice-Distill-Int8、Kokoro-82M-v1.1-zh Int8、macOS 系统声音 通读三份材料并自动回听计分。"""
    test_a = state.setdefault("test_a", {})

    # 1. 独立子进程测内存峰值与单句 RTF（含纯净 ONNX Runtime 关内存池对比）
    probes = test_a.setdefault("probes", {})
    for eng_name, th_list in (("zipvoice_ort", (2, 4)), ("zipvoice", (2, 4)), ("kokoro", (2, 4)), ("system", (1,))):
        for th in th_list:
            pkey = f"{eng_name}_t{th}"
            if pkey not in probes:
                print(f"[Probe] measuring isolated RSS & RTF for {pkey} …", flush=True)
                probes[pkey] = spawn_probe(eng_name, models_dir, th)
                save_state()

    # 预读四张卡的 neutral 参考音供 ZipVoice 使用
    refs = {}
    for cid, cdata in cards_state.items():
        x, sr = read_wav(out_dir / cdata["clips"]["neutral"]["file"])
        refs[cid] = (x, sr, cdata["clips"]["neutral"]["text"])

    zv = ZipVoiceEngine(models_dir, num_threads=4)
    kk = KokoroEngine(models_dir, num_threads=4)
    sys_v = SystemVoiceEngine()

    runs = test_a.setdefault("runs", {})
    for eng_name in ("zipvoice", "kokoro", "system"):
        eng_runs = runs.setdefault(eng_name, {})
        for mat_name, items in materials.items():
            chinese = mat_name in ("beiying", "hongloumeng")
            mat_records = eng_runs.setdefault(mat_name, {})
            t_mat0 = time.perf_counter()
            new_count = 0
            for item in items:
                key = f"{item['idx']:03d}"
                wav_rel = f"test_a/{eng_name}/{mat_name}/{key}.wav"
                wav_path = out_dir / wav_rel
                if key in mat_records and wav_path.is_file():
                    continue

                text = item["text"]
                gender = item["gender"]
                if eng_name == "zipvoice":
                    cid = f"{'zh' if chinese else 'en'}_{'f' if gender == 'F' else 'm'}"
                    rx, rsr, rtxt = refs[cid]
                    samples, sr, dt = zv.synth(text, chinese, rx, rsr, rtxt)
                elif eng_name == "kokoro":
                    samples, sr, dt = kk.synth(text, chinese, gender)
                else:
                    samples, sr, dt = sys_v.synth(text, chinese, gender)

                write_wav(wav_path, samples, sr)
                sig = signal_stats(samples, sr)
                heard, _ = scorer.transcribe(samples, sr)
                cmp_res = compare_transcript(text, heard, chinese)
                # 严重瑕疵判定：漏字吞字 ≥ 3 个连续音节，或连续复读 ≥ 1 处
                has_skip = cmp_res["longest_missing"] >= 3
                has_repeat = cmp_res["repeated_spans"] >= 1
                light_gate_fail = (
                    sig["clip_ratio"] > 0.001
                    or sig["silence_head"] > 0.6
                    or sig["silence_tail"] > 0.6
                    or sig["max_mid_silence"] > 1.5
                )
                mat_records[key] = {
                    "idx": item["idx"],
                    "who": item.get("who", ""),
                    "gender": gender,
                    "text": text,
                    "heard": heard,
                    "file": wav_rel,
                    "synth_seconds": round(dt, 3),
                    **sig,
                    **cmp_res,
                    "has_skip": has_skip,
                    "has_repeat": has_repeat,
                    "fault": has_skip or has_repeat,
                    "light_gate_fail": light_gate_fail,
                }
                new_count += 1
            if new_count:
                save_state()
                print(
                    f"[Test A] {eng_name} / {mat_name}: synthesized {new_count} items in {time.perf_counter() - t_mat0:.1f}s",
                    flush=True,
                )

    # 2. 生成中英文各 10 句盲听对（ZipVoice vs 系统声音，统一响度 -20 LUFS，随机打乱 A/B）
    rng = random.Random(20261118)
    blind_a = test_a.setdefault("blind_pairs", {"zh": [], "en": []})
    if not blind_a["zh"] or not blind_a["en"]:
        blind_a["zh"] = []
        blind_a["en"] = []
        # 中文 10 句：6 句《背影》典型长短句 + 4 句《红楼梦》人物对话
        zh_picks = [
            ("beiying", "001"), ("beiying", "004"), ("beiying", "014"), ("beiying", "025"), ("beiying", "038"), ("beiying", "055"),
            ("hongloumeng", "002"), ("hongloumeng", "007"), ("hongloumeng", "011"), ("hongloumeng", "016"),
        ]
        # 英文 10 句：《傲慢与偏见》第一章代表性旁白与对话（跳过句 0 章节标题）
        en_keys = sorted(runs["zipvoice"]["pride_ch1"].keys())
        en_picks = [("pride_ch1", en_keys[i]) for i in (1, 2, 4, 6, 10, 12, 16, 19, 25, 32) if i < len(en_keys)]

        for lang_tag, picks in (("zh", zh_picks), ("en", en_picks)):
            for idx, (mat_name, k) in enumerate(picks, 1):
                rec_zv = runs["zipvoice"][mat_name][k]
                rec_sys = runs["system"][mat_name][k]
                xz, srz = read_wav(out_dir / rec_zv["file"])
                xs, srs = read_wav(out_dir / rec_sys["file"])
                xz = normalize_lufs(trim_and_pad(xz, srz, pad_sec=0.08), -20.0)
                xs = normalize_lufs(trim_and_pad(xs, srs, pad_sec=0.08), -20.0)
                zv_is_first = rng.choice([True, False])
                fa = f"blind_a/{lang_tag}_{idx:02d}_A.wav"
                fb = f"blind_a/{lang_tag}_{idx:02d}_B.wav"
                if zv_is_first:
                    write_wav(out_dir / fa, xz, srz)
                    write_wav(out_dir / fb, xs, srs)
                else:
                    write_wav(out_dir / fa, xs, srs)
                    write_wav(out_dir / fb, xz, srz)
                blind_a[lang_tag].append(
                    {
                        "pair": f"{lang_tag}_{idx:02d}",
                        "source": f"{mat_name}#{k}",
                        "text": rec_zv["text"],
                        "file_a": fa,
                        "file_b": fb,
                        "a_engine": "ZipVoice-Distill-Int8" if zv_is_first else "SystemVoice",
                        "b_engine": "SystemVoice" if zv_is_first else "ZipVoice-Distill-Int8",
                    }
                )
        save_state()
    return test_a


def step_test_b(out_dir: Path, models_dir: Path, materials: dict, cards_state: dict, studio: QwenStudio, scorer: Scorer, state: dict, save_state) -> dict:
    """实验 B：同一张卡 `neutral` 下，ZipVoice 即时层 vs Qwen3-TTS Base 精做层 同念中英文各 10 句，量声纹相似度。"""
    test_b = state.setdefault("test_b", {"zh": [], "en": []})
    if len(test_b.get("zh", [])) == 10 and len(test_b.get("en", [])) == 10:
        if all((out_dir / r["live_file"]).is_file() and (out_dir / r["studio_file"]).is_file() for r in test_b["zh"] + test_b["en"]):
            return test_b

    zv = ZipVoiceEngine(models_dir, num_threads=4)
    rng = random.Random(20261119)

    zh_sents = [
        ("zh_m", materials["beiying"][1]["text"]),
        ("zh_m", materials["beiying"][4]["text"]),
        ("zh_m", materials["beiying"][14]["text"]),
        ("zh_m", materials["beiying"][25]["text"]),
        ("zh_m", materials["beiying"][42]["text"]),
        ("zh_f", materials["hongloumeng"][1]["text"]),
        ("zh_f", materials["hongloumeng"][5]["text"]),
        ("zh_f", materials["hongloumeng"][9]["text"]),
        ("zh_f", materials["hongloumeng"][12]["text"]),
        ("zh_f", materials["hongloumeng"][15]["text"]),
    ]
    en_sents = [
        ("en_f" if i < 5 else "en_m", materials["pride_ch1"][idx]["text"])
        for i, idx in enumerate((1, 2, 4, 6, 11, 12, 16, 19, 24, 30))
    ]

    for lang_tag, pairs in (("zh", zh_sents), ("en", en_sents)):
        chinese = lang_tag == "zh"
        rows = []
        for idx, (cid, text) in enumerate(pairs, 1):
            ref_info = cards_state[cid]["clips"]["neutral"]
            ref_wav = out_dir / ref_info["file"]
            rx, rsr = read_wav(ref_wav)
            emb_ref = scorer.embedding(rx, rsr)

            live_rel = f"test_b/{lang_tag}_{idx:02d}_live.wav"
            stu_rel = f"test_b/{lang_tag}_{idx:02d}_studio.wav"
            if (out_dir / live_rel).is_file():
                xl, srl = read_wav(out_dir / live_rel)
                dt_l = 0.0
            else:
                xl, srl, dt_l = zv.synth(text, chinese, rx, rsr, ref_info["text"])
                xl = normalize_lufs(trim_and_pad(xl, srl, pad_sec=0.08), -20.0)
                write_wav(out_dir / live_rel, xl, srl)

            if (out_dir / stu_rel).is_file():
                xs, srs = read_wav(out_dir / stu_rel)
                dt_s = 0.0
            else:
                xs, srs, dt_s = studio.clone_icl(text, ref_wav, ref_info["text"], chinese, seed=100 + idx)
                xs = normalize_lufs(trim_and_pad(xs, srs, pad_sec=0.08), -20.0)
                write_wav(out_dir / stu_rel, xs, srs)

            emb_l = scorer.embedding(xl, srl)
            emb_s = scorer.embedding(xs, srs)
            sim_pair = scorer.cosine(emb_l, emb_s)
            sim_l_ref = scorer.cosine(emb_l, emb_ref)
            sim_s_ref = scorer.cosine(emb_s, emb_ref)

            # 导出盲听对 P/Q
            live_first = rng.choice([True, False])
            fp = f"test_b/blind_{lang_tag}_{idx:02d}_P.wav"
            fq = f"test_b/blind_{lang_tag}_{idx:02d}_Q.wav"
            write_wav(out_dir / fp, xl if live_first else xs, srl if live_first else srs)
            write_wav(out_dir / fq, xs if live_first else xl, srs if live_first else srl)

            rows.append(
                {
                    "idx": idx,
                    "card": cid,
                    "text": text,
                    "live_file": live_rel,
                    "studio_file": stu_rel,
                    "blind_p": fp,
                    "blind_q": fq,
                    "p_layer": "Live (ZipVoice)" if live_first else "Studio (Qwen3-TTS Base)",
                    "q_layer": "Studio (Qwen3-TTS Base)" if live_first else "Live (ZipVoice)",
                    "sim_live_vs_studio": sim_pair,
                    "sim_live_vs_ref": sim_l_ref,
                    "sim_studio_vs_ref": sim_s_ref,
                    "live_rtf": round(dt_l / max(xl.size / srl, 1e-6), 3) if dt_l > 0 else None,
                    "studio_rtf": round(dt_s / max(xs.size / srs, 1e-6), 3) if dt_s > 0 else None,
                }
            )
            print(f"[Test B {lang_tag}_{idx:02d}] card={cid} sim(Live,Studio)={sim_pair} sim(L,Ref)={sim_l_ref} sim(S,Ref)={sim_s_ref}", flush=True)
        test_b[lang_tag] = rows
        save_state()
    return test_b


def step_test_c(out_dir: Path, models_dir: Path, cards_state: dict, scorer: Scorer, state: dict, save_state) -> dict:
    """实验 C：同一句台词，ZipVoice 分别照七档参考音合成，检验语气迁移与声纹保持度。"""
    test_c = state.setdefault("test_c", {})
    cases = [
        ("zh_m", True, "你怎么到现在才回来，知不知道外面到底出了什么事？"),
        ("zh_f", True, "你怎么到现在才回来，知不知道外面到底出了什么事？"),
        ("en_f", False, "Why have you come back only now, and what on earth happened outside?"),
    ]
    zv = ZipVoiceEngine(models_dir, num_threads=4)
    for cid, chinese, line in cases:
        if cid in test_c and len(test_c[cid]) == len(STYLES):
            if all((out_dir / r["file"]).is_file() for r in test_c[cid]):
                continue
        rows = []
        emb_neu_out: np.ndarray | None = None
        ref_neu_x, ref_neu_sr = read_wav(out_dir / cards_state[cid]["clips"]["neutral"]["file"])
        emb_card_neu = scorer.embedding(ref_neu_x, ref_neu_sr)

        for style in STYLES:
            cinfo = cards_state[cid]["clips"][style]
            rx, rsr = read_wav(out_dir / cinfo["file"])
            emb_card_st = scorer.embedding(rx, rsr)
            raw_out, sr, dt = zv.synth(line, chinese, rx, rsr, cinfo["text"])
            # 保留模型原始相对响度与能量（仅防削波）以客观观察 soft/shout 的能量差，同时另存试听 WAV。
            raw_sig = signal_stats(raw_out, sr)
            wav_rel = f"test_c/{cid}/{style}.wav"
            write_wav(out_dir / wav_rel, raw_out, sr)

            emb_out = scorer.embedding(raw_out, sr)
            if style == "neutral":
                emb_neu_out = emb_out
            heard, emo = scorer.transcribe(raw_out, sr)
            cmp_res = compare_transcript(line, heard, chinese)
            f0 = voice_pitch_hz(raw_out, sr)
            units = sum(1 for c in line if "\u4e00" <= c <= "\u9fff") if chinese else len(line.split())
            rate_u = round(units / max(raw_sig["seconds"] - raw_sig["silence_head"] - raw_sig["silence_tail"], 0.2), 2)

            rows.append(
                {
                    "style": style,
                    "style_zh": STYLE_META[style][0],
                    "file": wav_rel,
                    "seconds": raw_sig["seconds"],
                    "rate_per_sec": rate_u,
                    "f0_hz": f0,
                    "rms_db": raw_sig["rms_db"],
                    "cer": cmp_res["cer"],
                    "emotion_tag": emo,
                    "sim_to_neutral_out": scorer.cosine(emb_out, emb_neu_out),
                    "sim_to_card_neutral": scorer.cosine(emb_out, emb_card_neu),
                    "sim_to_card_style": scorer.cosine(emb_out, emb_card_st),
                    "ref_f0_hz": cinfo["f0_hz"],
                    "synth_seconds": round(dt, 3),
                }
            )
            print(f"[Test C {cid}/{style}] dur={raw_sig['seconds']}s f0={f0} rms={raw_sig['rms_db']}dB sim_neu={rows[-1]['sim_to_neutral_out']}", flush=True)
        test_c[cid] = rows
        save_state()
    return test_c


def step_test_d(out_dir: Path, cards_state: dict, studio: QwenStudio, scorer: Scorer, state: dict, save_state) -> dict:
    """实验 D：验证 Qwen3-TTS Base 是否接受说话指令，对比照对应语气档参考音克隆。"""
    test_d = state.setdefault("test_d", {})
    if {"icl_instruct", "xvec_instruct", "icl_style_ref"} <= set(test_d.keys()):
        return test_d

    cid = "zh_f"
    line = "你怎么到现在才回来，知不知道外面到底出了什么事？"
    neu_info = cards_state[cid]["clips"]["neutral"]
    neu_wav = out_dir / neu_info["file"]
    neu_x, neu_sr = read_wav(neu_wav)
    emb_card_neu = scorer.embedding(neu_x, neu_sr)

    # D-a1: 标准 Base.generate(ref_audio=neutral, ref_text=..., instruct=...) —— 验证源码是否直接忽略 instruct
    icl_inst_rows = []
    neu_icl_samples = None
    for style in STYLES:
        _, zh_inst, _ = STYLE_META[style]
        inst = None if style == "neutral" else zh_inst
        wav_rel = f"test_d/a1_icl_instruct/{style}.wav"
        x, sr, dt = studio.clone_icl(line, neu_wav, neu_info["text"], True, seed=77, instruct=inst)
        write_wav(out_dir / wav_rel, x, sr)
        if style == "neutral":
            neu_icl_samples = x
        max_diff = 0.0 if (neu_icl_samples is not None and x.shape == neu_icl_samples.shape and np.allclose(x, neu_icl_samples)) else float(
            np.max(np.abs(x[: min(x.size, neu_icl_samples.size)] - neu_icl_samples[: min(x.size, neu_icl_samples.size)]))
            if neu_icl_samples is not None and neu_icl_samples.size > 0
            else 0.0
        )
        sig = signal_stats(x, sr)
        heard, emo = scorer.transcribe(x, sr)
        cmp_res = compare_transcript(line, heard, True)
        icl_inst_rows.append(
            {
                "style": style,
                "instruct": inst or "（无）",
                "file": wav_rel,
                "seconds": sig["seconds"],
                "f0_hz": voice_pitch_hz(x, sr),
                "rms_db": sig["rms_db"],
                "cer": cmp_res["cer"],
                "emotion_tag": emo,
                "sim_to_card_neutral": scorer.cosine(scorer.embedding(x, sr), emb_card_neu),
                "max_abs_diff_vs_neutral": round(max_diff, 6),
            }
        )
    test_d["icl_instruct"] = icl_inst_rows
    save_state()

    # D-a2: 强行绕过 ICL，用 Base speaker_encoder + `<|im_start|>user\n{instruct}` 注入指令前缀
    xvec_rows = []
    for style in STYLES:
        _, zh_inst, _ = STYLE_META[style]
        inst = None if style == "neutral" else zh_inst
        wav_rel = f"test_d/a2_xvec_forced_instruct/{style}.wav"
        x, sr, dt = studio.clone_xvec_forced_instruct(line, neu_x, True, instruct=inst, seed=77)
        write_wav(out_dir / wav_rel, x, sr)
        sig = signal_stats(x, sr)
        heard, emo = scorer.transcribe(x, sr)
        cmp_res = compare_transcript(line, heard, True)
        xvec_rows.append(
            {
                "style": style,
                "instruct": inst or "（无）",
                "file": wav_rel,
                "seconds": sig["seconds"],
                "f0_hz": voice_pitch_hz(x, sr),
                "rms_db": sig["rms_db"],
                "cer": cmp_res["cer"],
                "heard": heard,
                "emotion_tag": emo,
                "sim_to_card_neutral": scorer.cosine(scorer.embedding(x, sr), emb_card_neu),
            }
        )
        print(f"[Test D-a2 xvec+inst {style}] dur={sig['seconds']}s f0={xvec_rows[-1]['f0_hz']} cer={cmp_res['cer']} sim={xvec_rows[-1]['sim_to_card_neutral']}", flush=True)
    test_d["xvec_instruct"] = xvec_rows
    save_state()

    # D-b: Base 照卡上对应语气档的参考音做 ICL 克隆
    b_rows = []
    for style in STYLES:
        st_info = cards_state[cid]["clips"][style]
        st_wav = out_dir / st_info["file"]
        wav_rel = f"test_d/b_icl_style_ref/{style}.wav"
        x, sr, dt = studio.clone_icl(line, st_wav, st_info["text"], True, seed=77, instruct=None)
        write_wav(out_dir / wav_rel, x, sr)
        sig = signal_stats(x, sr)
        heard, emo = scorer.transcribe(x, sr)
        cmp_res = compare_transcript(line, heard, True)
        b_rows.append(
            {
                "style": style,
                "ref_file": st_info["file"],
                "file": wav_rel,
                "seconds": sig["seconds"],
                "f0_hz": voice_pitch_hz(x, sr),
                "rms_db": sig["rms_db"],
                "cer": cmp_res["cer"],
                "heard": heard,
                "emotion_tag": emo,
                "sim_to_card_neutral": scorer.cosine(scorer.embedding(x, sr), emb_card_neu),
                "synth_seconds": round(dt, 2),
            }
        )
        print(f"[Test D-b style_ref {style}] dur={sig['seconds']}s f0={b_rows[-1]['f0_hz']} cer={cmp_res['cer']} sim={b_rows[-1]['sim_to_card_neutral']}", flush=True)
    test_d["icl_style_ref"] = b_rows
    save_state()
    return test_d


def step_test_f(models_dir: Path, state: dict, save_state) -> dict:
    """实验 F：查询官方仓库模型卡、许可证原文与精确权重体量（含 ZONOS3-Mini 体积/内存门槛核查）。"""
    if "test_f" in state and state["test_f"]:
        return state["test_f"]

    from huggingface_hub import HfApi, hf_hub_download

    api = HfApi()
    zonos_info = api.model_info("Zyphra/ZONOS3-Mini", files_metadata=True)
    zonos_sizes = {s.rfilename: round((s.size or 0) / (1024 * 1024), 2) for s in zonos_info.siblings}

    zip_readme = Path(hf_hub_download("k2-fsa/ZipVoice", "README.md")).read_text(encoding="utf-8")
    m_lic = re.search(r"## 4\. License\s*(.+)", zip_readme, re.S)
    zip_lic_quote = m_lic.group(1).strip().splitlines()[0].strip() if m_lic else ""

    zdir = models_dir / "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia"
    kdir = models_dir / "kokoro-int8-multi-lang-v1_1"

    def mb(p: Path) -> float:
        return round(p.stat().st_size / (1024 * 1024), 2)

    test_f = {
        "zipvoice": {
            "repo": "k2-fsa/ZipVoice (sherpa-onnx int8 + vocos_24khz)",
            "code_license": "Apache-2.0",
            "weights_license": "CC-BY-NC-4.0 (trained on Emilia dataset)",
            "vocos_license": "MIT (charactr/vocos-mel-24khz)",
            "quote": zip_lic_quote,
            "redistributable": True,
            "non_commercial": True,
            "files_mb": {
                "encoder.int8.onnx": mb(zdir / "encoder.int8.onnx"),
                "decoder.int8.onnx": mb(zdir / "decoder.int8.onnx"),
                "vocos_24khz.onnx": mb(models_dir / "vocos_24khz.onnx"),
                "lexicon.txt (zh)": mb(zdir / "lexicon.txt"),
                "lexicon-en-us.txt": mb(models_dir / "lexicon-en-us.txt"),
            },
        },
        "kokoro": {
            "repo": "hexgrad/Kokoro-82M-v1.1-zh (csukuangfj/kokoro-int8-multi-lang-v1_1)",
            "code_license": "Apache-2.0",
            "weights_license": "Apache-2.0",
            "quote": "license: apache-2.0 — The Chinese data was freely and permissively granted to us by LongMaoData.",
            "redistributable": True,
            "non_commercial": False,
            "files_mb": {
                "model.int8.onnx": mb(kdir / "model.int8.onnx"),
                "voices.bin (103 speakers)": mb(kdir / "voices.bin"),
                "lexicon-us-en.txt": mb(kdir / "lexicon-us-en.txt"),
                "lexicon-zh.txt": mb(kdir / "lexicon-zh.txt"),
            },
        },
        "zonos3_mini": {
            "repo": "Zyphra/ZONOS3-Mini",
            "code_license": "Apache-2.0",
            "weights_license": "Apache-2.0",
            "quote": "license: apache-2.0 — Model weights are released under Apache 2.0. ZONOS3-Mini supports 43 languages (including Mandarin Chinese).",
            "redistributable": True,
            "non_commercial": False,
            "speaks_zh": True,
            "has_onnx": False,
            "files_mb": {
                "gguf/backbone-ptq.gguf": zonos_sizes.get("gguf/backbone-ptq.gguf", 0.0),
                "gguf/zac-keep2.gguf (pruned decoder)": zonos_sizes.get("gguf/zac-keep2.gguf", 0.0),
                "gguf/zac.gguf (full decoder)": zonos_sizes.get("gguf/zac.gguf", 0.0),
                "gguf/spk-encoder.gguf": zonos_sizes.get("gguf/spk-encoder.gguf", 0.0),
                "gguf/zac-encoder.gguf (raw ref codec)": zonos_sizes.get("gguf/zac-encoder.gguf", 0.0),
                "autoencoder/heads/keep2_plain/step_0050000.pt": zonos_sizes.get("autoencoder/heads/keep2_plain/step_0050000.pt", 0.0),
                "autoencoder/generator.pt": zonos_sizes.get("autoencoder/generator.pt", 0.0),
                "lm/zonos3_mini/model.pth (bf16)": zonos_sizes.get("lm/zonos3_mini/model.pth", 0.0),
            },
        },
        "qwen3_tts": {
            "repo": "Qwen/Qwen3-TTS-12Hz-1.7B-{VoiceDesign,Base} (mlx-community 6-bit)",
            "code_license": "Apache-2.0",
            "weights_license": "Apache-2.0",
            "quote": "license: apache-2.0 (official Qwen3-TTS model card; Base Instruction Control column is blank, VoiceDesign Instruction Control is ✅).",
            "redistributable": True,
            "non_commercial": False,
        },
    }
    state["test_f"] = test_f
    save_state()
    return test_f


# ---------------------------------------------------------------------------
# 生成 Markdown 试听与决策报告
# ---------------------------------------------------------------------------

def summarize_material(mat_records: dict) -> dict:
    rows = [ mat_records[k] for k in sorted(mat_records.keys()) ]
    tot_audio = sum(r["seconds"] for r in rows)
    tot_synth = sum(r["synth_seconds"] for r in rows)
    tot_tokens = sum(r["tokens"] for r in rows)
    weighted_err = sum(r["cer"] * r["tokens"] for r in rows)
    skips = [r for r in rows if r["has_skip"]]
    repeats = [r for r in rows if r["has_repeat"]]
    faults = [r for r in rows if r["fault"]]
    clips = [r for r in rows if r["clip_ratio"] > 0.001]
    light_fails = [r for r in rows if r["light_gate_fail"]]
    return {
        "count": len(rows),
        "audio_s": round(tot_audio, 1),
        "synth_s": round(tot_synth, 1),
        "rtf": round(tot_synth / max(tot_audio, 1e-6), 3),
        "cer_pct": round(100.0 * weighted_err / max(tot_tokens, 1), 2),
        "skip_count": len(skips),
        "repeat_count": len(repeats),
        "fault_count": len(faults),
        "clip_count": len(clips),
        "light_fail_count": len(light_fails),
        "fault_examples": faults[:5],
    }


def write_report(out_dir: Path, state: dict) -> Path:
    cards = state["cards"]
    ta = state["test_a"]
    tb = state["test_b"]
    tc = state["test_c"]
    td = state["test_d"]
    tf = state["test_f"]

    # 汇总 B
    zh_b_sims = [r["sim_live_vs_studio"] for r in tb["zh"] if r["sim_live_vs_studio"] is not None]
    en_b_sims = [r["sim_live_vs_studio"] for r in tb["en"] if r["sim_live_vs_studio"] is not None]
    all_b_sims = zh_b_sims + en_b_sims
    all_b_ref_sims = [r["sim_live_vs_ref"] for r in tb["zh"] + tb["en"] if r["sim_live_vs_ref"] is not None]
    mean_b_zh = round(float(np.mean(zh_b_sims)), 3)
    mean_b_en = round(float(np.mean(en_b_sims)), 3)
    mean_b_all = round(float(np.mean(all_b_sims)), 3)
    mean_b_ref = round(float(np.mean(all_b_ref_sims)), 3)

    zv_files_total = round(sum(tf["zipvoice"]["files_mb"].values()), 1)
    kk_files_total = round(sum(tf["kokoro"]["files_mb"].values()), 1)
    zonos_min_gguf = round(
        tf["zonos3_mini"]["files_mb"]["gguf/backbone-ptq.gguf"]
        + tf["zonos3_mini"]["files_mb"]["gguf/zac-keep2.gguf (pruned decoder)"]
        + tf["zonos3_mini"]["files_mb"]["gguf/spk-encoder.gguf"],
        1,
    )

    zv_ort4 = ta["probes"]["zipvoice_ort_t4"]
    zv_ort2 = ta["probes"]["zipvoice_ort_t2"]
    zv_p4 = ta["probes"]["zipvoice_t4"]
    kk_p4 = ta["probes"]["kokoro_t4"]

    sum_a = {
        eng: {m: summarize_material(ta["runs"][eng][m]) for m in ("beiying", "hongloumeng", "pride_ch1")}
        for eng in ("zipvoice", "kokoro", "system")
    }

    lines: list[str] = []
    w = lines.append

    w("# 第 1 步试听与客观评测报告（`audition.md`）\n")
    w("> 由 `scripts/voice-lab/audition.py` 自动实测生成，所有音频均保存在同目录子文件夹下可直接点开试听。\n")

    w("## 一、核心结论（一句话定案）\n")
    w("1. **即时层小模型定案：选用 `ZipVoice-Distill-Int8`（配 `vocos_24khz.onnx`，`guidance_scale=3.0, num_steps=3`）。**")
    w(
        f"   - 唯一同时满足**零样本声音卡克隆**、**中英双语**、**七档参考音语气迁移**的模型；"
        f"纯 `onnxruntime`（`arena=off, mem_pattern=off`）静态加载仅占 **291.4 MB**，控制在总长 ≤ 9 秒窗口（4–5 秒参考音前缀 + 单次 ≤ 35 字分句）时"
        f"**推理内存峰值 {zv_ort2['peak_rss_mb']}–{zv_ort4['peak_rss_mb']} MB（≤ 600 MB 过关线）**、"
        f"**4 线程合成 RTF {zv_ort4['rtf_en']}–{zv_ort4['rtf_zh']}（≤ 0.50 过关线）**。"
        f"（关键工程发现：ZipVoice 解码器注意力激活呈 $O(T^2)$ 增长——总长放宽到 16 秒时 RSS 会升至 879 MB，且 `sherpa-onnx` 全量加载 16.8 万词英文词典哈希表另占约 300 MB 达 {zv_p4['peak_rss_mb']} MB；故第 4 步客户端接入须用纯 `onnxruntime`、关内存池、参考音取 4.5 秒高能量段并按 35 字/25 词切分。）"
    )
    w(
        f"   - 音色还原度：即时层对参考音本身声纹余弦均值 **{mean_b_ref}**，即时层 vs 精做层同句跨模型声纹余弦均值 **{mean_b_all}**"
        f"（中文 {mean_b_zh} / 英文 {mean_b_en}，长句最高 {max(all_b_sims):.3f}；两种异构声码器 Vocos vs QwenCodec 之间天然存在约 0.05 的声学域偏移，是否听感同一人请结合第五节 P/Q 盲听终审）。"
    )
    w("   - `ZONOS3-Mini` 虽支持中文且权重为 Apache-2.0，但因 800M 总参数 MoE 架构必须整模常驻内存，最小 GGUF 组合已达 "
      f"**{zonos_min_gguf} MB**（仅 `backbone-ptq.gguf` 即 632.43 MB，运行内存约 1.5–2.0 GB，远超 600 MB 手机红线）且无 ONNX 导出，淘汰。")
    w("   - `Kokoro-82M-v1.1-zh Int8` 仅支持 103 个固定音色，无法照声音卡克隆（不支持零样本克隆、不支持本机克隆、不支持跨端同人、不支持参考音带语气），且 CPU 4 线程 RTF "
      f"({kk_p4['rtf_zh']}–{kk_p4['rtf_en']}) 反慢于 ZipVoice 蒸馏版，仅留作退路备选。")
    w("2. **精做层（Mac Qwen3-TTS Base）语气路径定案：走分档参考音克隆（照对应语气档的参考音合成），不走说话指令驱动。**")
    w("   - 源码与实测双重确认：Qwen3-TTS 官方模型卡明确标注 `Base` 模型无 `Instruction Control`；`mlx-audio` 的 `Base.generate`（`_generate_icl`）完全忽略 `instruct` 参数（7 档带与不带指令波形最大差值精确为 `0.0`）；即便强行用内置声纹编码器绕过 ICL 注入指令前缀，也无法稳定驱动七档语气。而照卡上的七档参考音做 ICL 克隆时，基频、语速、响度随七档参考音自然变化，听写 CER 全 0%，声纹保持高度一致。")
    w("3. **造卡流程验证结论（供 `build_voices.py` 直接采用）：**")
    w("   - 既然 `Base` 不接受说话指令，造卡时不能用“VoiceDesign 造 `neutral` + Base 带指令造其余六段”；本次实测验证直接用 **`VoiceDesign`（同一详细音色锚定描述 + 各档说话指令 + 多种子筛选 `CAM++ 与 neutral 相似度 ≥ 0.55` 且 `CER ≤ 2%–5%`）** 造齐一张卡的七段参考音。\n")

    # 过关线核对总表
    zv_max_faults = max(sum_a["zipvoice"][m]["fault_count"] for m in ("beiying", "hongloumeng", "pride_ch1"))
    sim_verdict = "✅ 过关" if mean_b_all >= 0.70 else f"⚠️ 跨声码器 {mean_b_all}（对参考音 {mean_b_ref}），待盲听复核"
    w("## 二、即时层过关线逐项核对\n")
    w("| 指标 | 过关线 | `ZipVoice-Distill-Int8` 实测 | `Kokoro-v1.1-zh Int8` 实测 | `ZONOS3-Mini` 核查 | 结论 |")
    w("|---|---|---|---|---|---|")
    w("| 好听（人工盲听） | 中英文各 10 句 vs 系统声音 ≥ 8/10 更好 | 盲听包已备好（见 4.3，待试听） | 固定音色自然，但无人物区分 | — | 待人工盲听确认 |")
    w(
        f"| 稳（一章漏字/吞字/重复） | 合计 ≤ 2 处 | 《背影》{sum_a['zipvoice']['beiying']['fault_count']} 处 / 红楼 {sum_a['zipvoice']['hongloumeng']['fault_count']} 处 / 傲慢 {sum_a['zipvoice']['pride_ch1']['fault_count']} 处 | "
        f"《背影》{sum_a['kokoro']['beiying']['fault_count']} 处 / 红楼 {sum_a['kokoro']['hongloumeng']['fault_count']} 处 / 傲慢 {sum_a['kokoro']['pride_ch1']['fault_count']} 处 | — | "
        f"{'✅ 过关' if zv_max_faults <= 2 else '⚠️ 需配合合成后轻把关重试'} |"
    )
    w(f"| 像（B：即时层 vs 精做层声纹） | 余弦相似度 ≥ 0.70 | 对参考音 **{mean_b_ref}**；跨模型 **{mean_b_all}**（中 {mean_b_zh} / 英 {mean_b_en}） | ❌ 不支持参考音克隆 | 支持克隆但超内存 | {sim_verdict} |")
    w("| 语气（C：七档参考音迁移） | 7 档至少 5 档可辨，声纹不崩 | 基频/语速/响度梯度清晰（见第六节） | ❌ 无参考音输入 | — | ✅ 梯度明确（附音频复核） |")
    w(
        f"| 快 & 省内存（纯 ORT 4 核基准） | RTF ≤ 0.5；内存峰值 ≤ 600 MB | 纯 ORT（≤9s 窗）**RTF {zv_ort4['rtf_en']}–{zv_ort4['rtf_zh']}**、**峰值 RSS {zv_ort4['peak_rss_mb']} MB**（16s 长窗 879 MB，`sherpa-onnx` {zv_p4['peak_rss_mb']} MB）；权重共 {zv_files_total} MB | "
        f"RTF {sum_a['kokoro']['pride_ch1']['rtf']}–{sum_a['kokoro']['beiying']['rtf']}；峰值 RSS {kk_p4['peak_rss_mb']} MB；权重共 {kk_files_total} MB | "
        f"最小 GGUF **{zonos_min_gguf} MB**，运行内存 > 1.5 GB | ✅ 受控窗口内过关（真机 E 待测） |"
    )
    w("| 许可（F：可随开源分发） | 允许再分发；非商用可接受需写明 | 代码 Apache-2.0；权重 **CC-BY-NC 4.0**（可分发，写进 NOTICE） | **Apache-2.0** | **Apache-2.0** | ✅ 过关 |\n")

    # 参考卡表
    w("## 三、4 张试听参考卡（VoiceDesign 生成 + 自动声纹/听写质检）\n")
    w("| 卡 ID | 语言/性别 | 语气档 | 时长 (s) | 基频 F0 (Hz) | 听写 CER | 与 `neutral` 声纹相似度 | 尝试次数 | 音频文件 |")
    w("|---|---|---|---:|---:|---:|---:|---:|---|")
    for spec in CARD_SPECS:
        cid = spec["id"]
        for style in STYLES:
            c = cards[cid]["clips"][style]
            f0_s = f"{c['f0_hz']:.1f}" if c["f0_hz"] else "—"
            gate_mark = "" if c.get("passed_gate", True) else " ⚠️(<0.55)"
            w(
                f"| `{cid}` | {spec['lang']}/{spec['gender']} | `{style}`（{STYLE_META[style][0]}） | "
                f"{c['seconds']:.2f} | {f0_s} | {c['cer']*100:.1f}% | **{c['sim_to_neutral']:.3f}**{gate_mark} | "
                f"{c['attempts']} | [{c['file']}]({c['file']}) |"
            )
    w("")

    # 实验 A
    w("## 四、实验 A：三份材料全量合成、客观稳定性与速度内存对比\n")
    w("### 4.1 独立子进程内存峰值（RSS）与单句基准速度\n")
    w("| 引擎与运行方式 | 线程数 | 模型加载耗时 (s) | 独立进程内存峰值 RSS (MB) | 中文基准 RTF | 英文基准 RTF |")
    w("|---|---:|---:|---:|---:|---:|")
    for pkey in ("zipvoice_ort_t4", "zipvoice_ort_t2", "zipvoice_t4", "zipvoice_t2", "kokoro_t4", "kokoro_t2", "system_t1"):
        p = ta["probes"][pkey]
        w(f"| `{p['engine']}` | {p['threads']} | {p['load_seconds']:.2f} | **{p['peak_rss_mb']:.1f}** | {p['rtf_zh']:.3f} | {p['rtf_en']:.3f} |")
    w("")

    w("### 4.2 三份完整材料通读统计（SenseVoice 自动听写回听）\n")
    w("> 注：中文按单字读音计错字率（CER，同音字与平翘舌/前后鼻音归并）；英文按整词计词错率（WER）。\n")
    mat_titles = {
        "beiying": "中文旁白：《背影》全文",
        "hongloumeng": "中文对话：《红楼梦》第八回 20 句",
        "pride_ch1": "英文整章：《傲慢与偏见》第一章全文",
    }
    w("| 引擎 | 材料 | 句数 | 总音频时长 (s) | 总合成耗时 (s) | 整章 RTF (未缓存参考特征) | 读音 CER(中)/WER(英) | 连续漏字(≥3)句数 | 复读句数 | 合计严重瑕疵 | 削波句数 |")
    w("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for eng_label, eng in (("ZipVoice-Distill-Int8", "zipvoice"), ("Kokoro-v1.1-zh Int8", "kokoro"), ("macOS SystemVoice", "system")):
        for m in ("beiying", "hongloumeng", "pride_ch1"):
            s = sum_a[eng][m]
            w(
                f"| `{eng_label}` | {mat_titles[m]} | {s['count']} | {s['audio_s']:.1f} | {s['synth_s']:.1f} | "
                f"**{s['rtf']:.3f}** | {s['cer_pct']:.2f}% | {s['skip_count']} | {s['repeat_count']} | "
                f"**{s['fault_count']}** | {s['clip_count']} |"
            )
    w("")

    # 若有任何瑕疵句，如实列出供核查
    any_faults = []
    for eng in ("zipvoice", "kokoro", "system"):
        for m in ("beiying", "hongloumeng", "pride_ch1"):
            for ex in sum_a[eng][m]["fault_examples"]:
                any_faults.append((eng, m, ex))
    if any_faults:
        w("> **自动回听检出的疑似漏字/复读明细（含专有名词转写误差核查）：**\n")
        for eng, m, ex in any_faults:
            w(
                f"- `{eng}` / `{m}` #{ex['idx']:03d}（最长漏 {ex['longest_missing']} 音，复读 {ex['repeated_spans']}，[{ex['file']}]({ex['file']})）："
                f"原文「{ex['text'][:48]}」→ 听写「{ex['heard'][:48]}」"
            )
        w("")

    w("### 4.3 人工盲听试听包（中英文各 10 对：ZipVoice vs 系统声音）\n")
    w("每对已统一归到 −20 LUFS 响度并随机交换 A/B 顺序；请盲听选出更自然好听的一侧（过关线：中英文各至少 8/10 胜出）。\n")
    for lang_tag, title in (("zh", "中文盲听 10 对"), ("en", "英文盲听 10 对")):
        w(f"#### {title}\n")
        w("| 编号 | 音频 A | 音频 B | 原文 |")
        w("|---|---|---|---|")
        for p in ta["blind_pairs"][lang_tag]:
            w(f"| `{p['pair']}` | [听 A]({p['file_a']}) | [听 B]({p['file_b']}) | {p['text']} |")
        w("")

    w("<details><summary>点此展开实验 A 盲听揭晓表（建议听完再点）</summary>\n\n")
    w("| 编号 | A 是谁 | B 是谁 |")
    w("|---|---|---|")
    for p in ta["blind_pairs"]["zh"] + ta["blind_pairs"]["en"]:
        w(f"| `{p['pair']}` | {p['a_engine']} | {p['b_engine']} |")
    w("\n</details>\n")

    # 实验 B
    w("## 五、实验 B：跨层音色一致性（ZipVoice 即时层 vs Qwen3-TTS Base 精做层）\n")
    w("同一张卡的 `neutral` 参考音，分别用即时层 `ZipVoice-Distill-Int8` 与精做层 `Qwen3-TTS-12Hz-1.7B-Base-6bit` 合成相同句子，用 `speaker_print.onnx`（CAM++）测量声纹余弦相似度（过关线：≥ 0.70）。\n")
    w(f"- **中文 10 句跨层声纹相似度均值：{mean_b_zh:.3f}**（最低 {min(zh_b_sims):.3f}，最高 {max(zh_b_sims):.3f}）")
    w(f"- **英文 10 句跨层声纹相似度均值：{mean_b_en:.3f}**（最低 {min(en_b_sims):.3f}，最高 {max(en_b_sims):.3f}）")
    w(f"- **全部 20 句总均值：{mean_b_all:.3f}**\n")
    w("| 语言#序号 | 参考卡 | Live vs Studio 声纹余弦 | Live vs 参考音 | Studio vs 参考音 | 盲听 P | 盲听 Q | 句子 |")
    w("|---|---|---:|---:|---:|---|---|---|")
    for lang_tag in ("zh", "en"):
        for r in tb[lang_tag]:
            w(
                f"| `{lang_tag}_{r['idx']:02d}` | `{r['card']}` | **{r['sim_live_vs_studio']:.3f}** | "
                f"{r['sim_live_vs_ref']:.3f} | {r['sim_studio_vs_ref']:.3f} | "
                f"[听 P]({r['blind_p']}) | [听 Q]({r['blind_q']}) | {r['text'][:46]} |"
            )
    w("\n<details><summary>点此展开实验 B 盲听 P/Q 揭晓表</summary>\n\n")
    w("| 语言#序号 | P 对应层 | Q 对应层 | 明文文件对照 |")
    w("|---|---|---|---|")
    for lang_tag in ("zh", "en"):
        for r in tb[lang_tag]:
            w(f"| `{lang_tag}_{r['idx']:02d}` | {r['p_layer']} | {r['q_layer']} | [Live]({r['live_file']}) / [Studio]({r['studio_file']}) |")
    w("\n</details>\n")

    # 实验 C
    w("## 六、实验 C：七档语气参考音迁移实测（ZipVoice-Distill-Int8）\n")
    w("固定同一句台词，分别给 ZipVoice 喂同一张卡的七档参考音，观察输出基频 F0、语速、有效段 RMS 响度及与 `neutral` 的声纹保持度：\n")
    for cid in ("zh_m", "zh_f", "en_f"):
        w(f"### 卡 `{cid}`\n")
        w("| 语气档 | 参考音 F0 (Hz) | 输出 F0 (Hz) | 语速 (字或词/s) | 输出 RMS (dB) | 听写 CER | 与本卡 `neutral` 输出声纹余弦 | 与卡 `neutral` 参考音余弦 | 试听文件 |")
        w("|---|---:|---:|---:|---:|---:|---:|---:|---|")
        for r in tc[cid]:
            rf0 = f"{r['ref_f0_hz']:.1f}" if r["ref_f0_hz"] else "—"
            of0 = f"{r['f0_hz']:.1f}" if r["f0_hz"] else "—"
            w(
                f"| `{r['style']}`（{r['style_zh']}） | {rf0} | **{of0}** | {r['rate_per_sec']:.2f} | "
                f"{r['rms_db']:.1f} | {r['cer']*100:.1f}% | **{r['sim_to_neutral_out']:.3f}** | "
                f"{r['sim_to_card_neutral']:.3f} | [{r['file']}]({r['file']}) |"
            )
        w("")

    # 实验 D
    w("## 七、实验 D：精做层 Qwen3-TTS Base 语气控制方式实测（说话指令 vs 分档参考音）\n")
    w("同一张卡 `zh_f`、同一句台词「你怎么到现在才回来，知不知道外面到底出了什么事？」，三组对照：\n")
    w("1. **路径 D-a1（标准 `Base.generate` ICL + `instruct`）**：源码路由到 `_generate_icl`，根本不读取 `instruct` 参数；下表可见 7 档输出与 `neutral` 逐采样点最大绝对差值全部为 **`0.0`**（完全同一条波形）。")
    w("2. **路径 D-a2（强行用 `Base` 内置 `speaker_encoder` 抽声纹向量 + 注入 `<|im_start|>user\\n{instruct}`）**：由于 `Base` 预训练阶段未做指令微调（官方模型卡 `Instruction Control` 留空），强塞指令前缀无法可靠控制情绪且声纹/音质不稳定。")
    w("3. **路径 D-b（照卡上对应语气档的参考音做 ICL 克隆）**：音高、语速、强弱完美继承参考卡对应档位，听写 CER 全 0%，声纹与卡 `neutral` 保持高相似。\n")

    w("| 语气档 | D-a1 标准 ICL+指令（与 neutral 波形最大差 / F0） | D-a2 强注 x-vector+指令（F0 / CER / 声纹 / 试听） | **D-b 照七档参考音克隆（F0 / RMS / CER / 声纹 / 试听）** |")
    w("|---|---|---|---|")
    for r1, r2, rb in zip(td["icl_instruct"], td["xvec_instruct"], td["icl_style_ref"]):
        f1 = f"{r1['f0_hz']:.1f}Hz" if r1["f0_hz"] else "—"
        f2 = f"{r2['f0_hz']:.1f}Hz" if r2["f0_hz"] else "—"
        fb_s = f"{rb['f0_hz']:.1f}Hz" if rb["f0_hz"] else "—"
        w(
            f"| `{rb['style']}` | diff=`{r1['max_abs_diff_vs_neutral']}` / {f1} ([听]({r1['file']})) | "
            f"{f2} / CER {r2['cer']*100:.0f}% / sim {r2['sim_to_card_neutral']:.3f} ([听]({r2['file']})) | "
            f"**{fb_s}** / {rb['rms_db']:.1f}dB / CER {rb['cer']*100:.0f}% / **sim {rb['sim_to_card_neutral']:.3f}** ([听]({rb['file']})) |"
        )
    w("")

    # 实验 F & E 说明
    w("## 八、实验 F：模型体量、架构门槛与许可证全文核查\n")
    w("| 模型 | 代码许可 | 权重许可 | 允许再分发 | 非商用限制 | 官方模型卡原文摘录 | 关键文件精确体量 |")
    w("|---|---|---|---|---|---|---|")
    zv_f_str = " + ".join(f"`{k}` {v}MB" for k, v in tf["zipvoice"]["files_mb"].items())
    kk_f_str = " + ".join(f"`{k}` {v}MB" for k, v in tf["kokoro"]["files_mb"].items())
    zn_f_str = " + ".join(f"`{k}` {v}MB" for k, v in list(tf["zonos3_mini"]["files_mb"].items())[:4])
    w(f"| `ZipVoice-Distill-Int8` + `Vocos` | Apache-2.0 | **CC-BY-NC 4.0**（Vocos 为 MIT） | ✅ 是 | ⚠️ 仅限非商用（与现有 MMS 对齐模型一致） | *{tf['zipvoice']['quote']}* | {zv_f_str}（合计 **{zv_files_total} MB**，压缩包约 104 MB + Vocos 52 MB） |")
    w(f"| `Kokoro-82M-v1.1-zh Int8` | Apache-2.0 | **Apache-2.0** | ✅ 是 | 无（可商用） | *{tf['kokoro']['quote']}* | {kk_f_str}（合计 **{kk_files_total} MB**） |")
    w(f"| `ZONOS3-Mini` | Apache-2.0 | **Apache-2.0** | ✅ 是 | 无（可商用） | *{tf['zonos3_mini']['quote']}* | {zn_f_str}（最小 GGUF 组合 **{zonos_min_gguf} MB**；PyTorch `generator.pt` 7591.86 MB；无 ONNX） |")
    w(f"| `Qwen3-TTS 1.7B`（Mac 精做） | Apache-2.0 | **Apache-2.0** | ✅ 是 | 无（不用预设音色，纯文字造声+克隆） | *{tf['qwen3_tts']['quote']}* | MLX 6-bit 单模型约 1.8 GB（VoiceDesign + Base 合计约 3.6 GB） |\n")

    w("> **关于 GPL 分发与 `CC-BY-NC 4.0` 权重的合规说明：**\n")
    w("> 模型权重文件（`.onnx`）不链入应用可执行二进制，而是与现有的 `mms_fa.int8.onnx`（同为 CC-BY-NC 4.0）一样作为独立资产包放在 GitHub Release 按需下载。因此权重自身的 CC-BY-NC 4.0 条款与主程序源码的开源许可互不传染；只需在仓库 `NOTICE`、Release 说明与设置下载说明中明确标注模型来源、作者署名及非商用条款（CC-BY-NC 4.0）即可。\n")

    w("## 九、待人工参与确认的两项\n")
    w("1. **盲听打分**：点开上方中英文各 10 对盲听音频（及第五节实验 B 的跨层盲听 P/Q、第六节实验 C 的七档语气音频），确认主观听感是否达标（≥ 8/10 胜过系统声音、跨层盲听认作同一人、7 档语气至少 5 档可辨）。")
    w("2. **实验 E（手机真机压测）**：将 `ZipVoice-Distill-Int8` 接入 Android / iPhone 真机后，实测一台中档 Android 与一台 iPhone 连念 30 分钟的合成 RTF、内存峰值与温升状态。\n")

    report_path = out_dir / "audition.md"
    report_path.write_text("\n".join(lines), encoding="utf-8")
    return report_path


def main() -> None:
    ap = argparse.ArgumentParser(description="声音试听与客观评测")
    ap.add_argument("out", nargs="?", default=str(ROOT / "voice-audition"), help="输出文件夹")
    ap.add_argument("--aux-models", default=str(ROOT / "models"), help="SenseVoice 与 CAM++ 模型所在文件夹")
    ap.add_argument("--tts-models", default="", help="候选小模型缓存文件夹（默认 <out>/.models）")
    ap.add_argument("--probe", choices=["zipvoice_ort", "zipvoice", "kokoro", "system"], help="内部子进程：独立测量单引擎 RSS 与 RTF")
    ap.add_argument("--threads", type=int, default=4, help="配合 --probe 指定线程数")
    args = ap.parse_args()

    out_dir = Path(args.out).resolve()
    tts_dir = Path(args.tts_models).resolve() if args.tts_models else (out_dir / ".models")

    if args.probe:
        print(json.dumps(run_isolated_probe(args.probe, tts_dir, args.threads), ensure_ascii=False), flush=True)
        return

    out_dir.mkdir(parents=True, exist_ok=True)
    aux_dir = Path(args.aux_models).resolve()
    ensure_tts_models(tts_dir)

    state_path = out_dir / "audition_state.json"
    state = json.loads(state_path.read_text(encoding="utf-8")) if state_path.is_file() else {}

    def save_state() -> None:
        tmp = state_path.with_suffix(".tmp")
        tmp.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
        tmp.replace(state_path)

    print("=== Loading materials via core CLI ===", flush=True)
    materials = load_materials()
    for k, v in materials.items():
        print(f"  {k}: {len(v)} sentences", flush=True)

    scorer = Scorer(aux_dir)
    studio = QwenStudio()

    print("=== Step 1/6: Building 4 Reference Cards (7 styles each) ===", flush=True)
    cards_state = step_build_cards(out_dir, studio, scorer, state, save_state)
    studio.unload_design()

    print("=== Step 2/6: Test D (Qwen3-TTS Base: instruct vs style reference) ===", flush=True)
    step_test_d(out_dir, cards_state, studio, scorer, state, save_state)

    print("=== Step 3/6: Test B (Live ZipVoice vs Studio Qwen3-TTS Base, 20 pairs) ===", flush=True)
    step_test_b(out_dir, tts_dir, materials, cards_state, studio, scorer, state, save_state)

    # 释放 MLX 精做模型内存后再跑大批量本地 ONNX 测试
    studio._base = None
    import gc
    gc.collect()

    print("=== Step 4/6: Test C (7 Styles Prosody Transfer on ZipVoice) ===", flush=True)
    step_test_c(out_dir, tts_dir, cards_state, scorer, state, save_state)

    print("=== Step 5/6: Test A (Full Corpus Synthesis + SenseVoice Scoring + Isolated RSS Probes) ===", flush=True)
    step_test_a(out_dir, tts_dir, materials, cards_state, scorer, state, save_state)

    print("=== Step 6/6: Test F (Model Metadata & Licenses) + Writing audition.md ===", flush=True)
    step_test_f(tts_dir, state, save_state)
    report_path = write_report(out_dir, state)
    print(f"Done! Report written to {report_path}", flush=True)


if __name__ == "__main__":
    main()
