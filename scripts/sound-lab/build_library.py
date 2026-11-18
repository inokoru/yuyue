"""做广播剧的声音素材库（sounds-1.zip）：环境底和音效全用 MOSS-SoundEffect v2.0（Apache-2.0）在本机生成，
成品按 CC0 发布，不含别人的录音。

用法（环境见 moss.py）：... python -I scripts/sound-lab/build_library.py <输出文件夹> [--only beds|spots] [--steps 50]
可以随时中断，再跑会跳过已经做好的。全部做完后打包 <输出文件夹>/sounds-1.zip。

- 环境底：键照核心 sound.rs 的 bed_keys（地点/时间、地点、indoor|outdoor/天气）。每种用两个种子各做一遍当左右声道
  （两次生成互不相关，听起来就是开阔的立体声），首尾 2 秒交叉淡化，可以无缝循环，28 秒。
  响度三档：安静 −34 dB、中等 −27 dB、热闹 −23 dB（RMS，和核心内置的同档声音一致）。
- 音效：核心 EVENT_KINDS 里除 other 外每一种，单声道，峰值 −3 dB，前后静音裁掉。常用的门、脚步、敲门做两种。
- 每个文件 Ogg Vorbis；library.json 记 id、key、文件、秒数、是否循环、许可、生成用的描述。
"""
import argparse
import json
import os
import re
import sys
import zipfile

import numpy as np
import soundfile as sf

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import moss  # noqa: E402

RATE = moss.RATE
QUIET, MEDIUM, BUSY = -34.0, -27.0, -23.0
LICENSE = "CC0 1.0（语阅用 MOSS-SoundEffect v2.0 生成）"
NO_VOICE = ", no music, no speech"

# (key, 描述, 响度)
BEDS = [
    ("居室/day", "quiet traditional room interior in daytime, faint birds outside the window, soft room tone", QUIET),
    ("居室/night", "quiet bedroom at night, very faint crickets outside the window, soft room tone", QUIET),
    ("厅堂", "large wooden hall interior, soft reverberant room tone, faint distant footsteps", QUIET),
    ("大殿", "vast stone palace hall, deep reverberant near silence, faint air movement", QUIET),
    ("书房", "quiet study room, soft ticking of an old clock, faint room tone", QUIET),
    ("厨房", "old kitchen, wood fire crackling in a stove, a pot simmering", MEDIUM),
    ("客栈酒馆", "busy old inn tavern, indistinct crowd murmur, cups and bowls clinking, laughter", BUSY),
    ("茶楼", "teahouse interior, soft indistinct chatter, teacups clinking, water being poured", MEDIUM),
    ("店铺", "small shop interior, street noise drifting in through the door", MEDIUM),
    ("牢房", "damp stone prison cell, water dripping slowly, faint echo", QUIET),
    ("山洞", "cave interior, water drops echoing, low hollow wind", QUIET),
    ("船舱", "wooden ship cabin, timbers creaking, water lapping against the hull", MEDIUM),
    ("教室", "classroom, quiet, pages turning, pencils writing, faint shuffling", QUIET),
    ("办公室", "office room, air conditioning hum, distant keyboard typing", QUIET),
    ("医院", "hospital corridor, quiet hum, distant soft footsteps, faint monitor beeps", QUIET),
    ("地下室", "basement, low hum of machinery, an occasional drip", QUIET),
    ("咖啡馆", "coffee shop, soft indistinct chatter, espresso machine, cups on saucers", MEDIUM),
    ("餐厅", "restaurant dining room, indistinct crowd chatter, cutlery on plates", MEDIUM),
    ("酒吧", "bar interior, indistinct crowd chatter, glasses clinking", BUSY),
    ("车内", "inside a car driving on a road, steady engine hum and tyre noise", MEDIUM),
    ("工厂", "factory floor, machines running rhythmically, metallic clanks", BUSY),
    ("机场", "airport terminal, crowd murmur, rolling suitcases, large hall", MEDIUM),
    ("地铁车站", "subway station platform, crowd murmur, distant train rumble", MEDIUM),
    ("车厢", "inside a train carriage, steady rhythmic rumble of wheels on rails", MEDIUM),
    ("庭院/day", "traditional courtyard garden in daytime, birds chirping, gentle breeze in leaves", MEDIUM),
    ("庭院/night", "courtyard garden at night, crickets chirping, gentle breeze", QUIET),
    ("街市/day", "busy old market street, indistinct crowd chatter, footsteps, wooden carts rolling", BUSY),
    ("街市/night", "quiet old street at night, a dog barking far away, distant footsteps", QUIET),
    ("村庄/day", "countryside village in daytime, roosters and chickens far away, birds", MEDIUM),
    ("村庄/night", "village at night, crickets and frogs, a dog barking far away", QUIET),
    ("田野", "open fields, wind over tall grass, skylarks singing high", MEDIUM),
    ("山林/day", "mountain forest in daytime, birds singing, wind in the trees, distant stream", MEDIUM),
    ("山林/night", "forest at night, an owl hooting, insects, wind in the trees", QUIET),
    ("竹林", "bamboo forest, wind rustling bamboo leaves, stalks creaking softly", MEDIUM),
    ("河边", "riverbank, river water flowing steadily, birds", BUSY),
    ("海边", "seashore, waves breaking and washing up the beach, seagulls far", BUSY),
    ("荒漠", "desert, dry wind blowing over sand", MEDIUM),
    ("雪地", "snowy field, soft cold wind, very quiet", QUIET),
    ("城墙", "top of a city wall, strong wind, flags flapping", MEDIUM),
    ("战场", "distant battlefield, war drums far away, crowds shouting far away, horses", BUSY),
    ("码头", "harbour dock, water lapping on wooden piles, ropes creaking, gulls", BUSY),
    ("桥上", "on a small bridge, a stream flowing below, light breeze", MEDIUM),
    ("墓地", "graveyard, eerie quiet wind, crows cawing far away", QUIET),
    ("夜路", "lonely road at night, crickets, wind", QUIET),
    ("城市街道/day", "modern city street, steady traffic, people walking past", BUSY),
    ("城市街道/night", "city street at night, sparse cars passing, distant hum", MEDIUM),
    ("indoor/rain", "rain on the roof heard from inside a quiet room, gentle and steady", MEDIUM),
    ("outdoor/rain", "steady rain falling outdoors on leaves, stones and puddles", BUSY),
    ("outdoor/wind", "strong gusty wind outdoors", MEDIUM),
    ("outdoor/snow", "snow falling outdoors, soft muffled wind, very quiet", QUIET),
    ("outdoor/day", "outdoors in daytime, birds, light breeze, open air", MEDIUM),
    ("outdoor/night", "outdoors at night, crickets, light breeze", QUIET),
    ("indoor/night", "quiet room at night, near silence, very faint room tone", QUIET),
]

# 写得更清楚的音效描述；其余由键名拼。
SPOT_WORDS = {
    "door_wood_open": "an old wooden door opening", "door_wood_close": "an old wooden door closing",
    "door_wood_knock": "knocking three times on a wooden door", "door_slam": "a door slammed shut hard",
    "door_creak": "a wooden door creaking slowly", "curtain_draw": "a cloth curtain drawn aside",
    "gate_iron": "a heavy iron gate opening", "lock_key": "a key turning in an old lock",
    "steps_wood": "footsteps walking on a wooden floor", "steps_stone": "footsteps walking on stone",
    "steps_grass": "footsteps walking on grass", "steps_snow": "footsteps crunching in snow",
    "steps_running": "someone running past", "steps_many": "many people walking together",
    "steps_stairs": "footsteps going up wooden stairs", "steps_heels": "high heels walking on a hard floor",
    "steps_mud": "footsteps squelching in mud", "sedan_chair": "a sedan chair carried, poles creaking, bearers' footsteps",
    "boat_oars": "wooden oars rowing in water", "bell_horse": "small bells on a trotting horse",
    "pour_tea": "tea poured into a porcelain cup", "pour_wine": "wine poured into a cup",
    "brush_write": "a brush writing on paper", "needle_cloth": "a needle pulled through cloth",
    "fan_open": "a folding paper fan snapped open", "jade_clink": "two jade pendants clinking",
    "mirror_set": "a bronze mirror set down on a table", "drum_war": "a large war drum beaten",
    "guqin": "a few notes plucked on a guqin zither", "flute": "a short phrase on a bamboo flute",
    "pipa": "a few notes plucked on a pipa lute", "music_far": "music playing far away",
    "insects_night": "insects chirping at night", "crowd_murmur": "a crowd murmuring indistinctly",
    "crowd_laugh": "a crowd laughing", "crowd_gasp": "a crowd gasping in surprise",
    "laugh_far": "someone laughing far away", "cry_far": "someone crying far away", "shout_far": "someone shouting far away",
    "phone_vibrate": "a phone vibrating on a table", "sword_draw": "a sword drawn from its sheath",
    "sword_sheath": "a sword slid back into its sheath", "fall_body": "a body falling to the ground",
}
PEOPLE = {"crowd_murmur", "crowd_laugh", "crowd_gasp", "baby_cry", "cough", "sneeze", "laugh_far", "cry_far", "shout_far", "kiss", "applause"}
MUSIC = {"guqin", "flute", "pipa", "music_far"}
TWICE = {"door_wood_open", "door_wood_close", "door_wood_knock", "steps_wood", "steps_stone", "knock_table"}


def event_kinds():
    """核心 sound.rs 里的 EVENT_KINDS，直接从源码读，免得两边不一致。"""
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "../../core/yuyue-core/src/drama/sound.rs")
    src = open(root, encoding="utf-8").read()
    body = src[src.index("pub const EVENT_KINDS"):]
    body = body[: body.index("];")]
    return [k for k in re.findall(r'"([a-z_]+)"', body) if k != "other"]


def seconds_for(kind):
    """照核心 seconds_for。"""
    if kind.startswith(("steps", "horse", "rain", "wind")) or "crowd" in kind or "music" in kind:
        return 4.0
    if kind in ("thunder", "bell_temple", "gong", "fireworks", "firecrackers", "train", "plane", "clock_strike"):
        return 5.0
    return 2.0


def rms_db(x):
    return 20 * np.log10(np.sqrt(np.mean(np.square(x))) + 1e-9)


def loopable(x, seam):
    """首尾交叉淡化：x 比要的长 seam，尾巴叠回开头，循环时接得上。"""
    n = len(x) - seam
    out = x[:n].copy()
    fade = np.linspace(0, 1, seam, dtype=np.float32)
    out[:seam] = x[n:] * (1 - fade) + x[:seam] * fade
    return out


def trim(x, floor_db=-50.0):
    """裁掉前后的静音（留 20 毫秒）。"""
    level = 10 ** (floor_db / 20) * max(1e-9, float(np.max(np.abs(x))))
    loud = np.where(np.abs(x) > level)[0]
    if loud.size == 0:
        return x
    pad = int(0.02 * RATE)
    return x[max(0, loud[0] - pad): loud[-1] + pad]


def usable(x):
    """不能用的：几乎无声、或者大段削波。"""
    return x.size > RATE // 4 and np.max(np.abs(x)) > 1e-3 and np.mean(np.abs(x) > 0.985) < 0.001


def write_ogg(path, x):
    sf.write(path, x, RATE, format="OGG", subtype="VORBIS")


def make_bed(key, words, target, steps):
    seam = 2 * RATE
    chans = []
    for seed in (1, 2):
        x = moss.make(words + NO_VOICE, 30.0, steps=steps, seed=seed)
        if not usable(x):
            x = moss.make(words + NO_VOICE, 30.0, steps=steps, seed=seed + 10)
        chans.append(loopable(x, seam))
    n = min(len(c) for c in chans)
    st = np.stack([c[:n] for c in chans], axis=1)
    st *= 10 ** ((target - rms_db(st)) / 20)
    peak = np.max(np.abs(st))
    if peak > 0.7:
        st *= 0.7 / peak
    return st.astype(np.float32)


def make_spot(kind, seed, steps):
    words = SPOT_WORDS.get(kind, kind.replace("_", " "))
    tail = "" if kind in PEOPLE or kind in MUSIC else NO_VOICE
    prompt = f"{words}, a single clear sound effect, close and dry{tail}"
    x = trim(moss.make(prompt, seconds_for(kind), steps=steps, seed=seed))
    if not usable(x):
        x = trim(moss.make(prompt, seconds_for(kind), steps=steps, seed=seed + 10))
    x *= 10 ** (-3 / 20) / max(1e-9, float(np.max(np.abs(x))))
    fade = min(len(x) // 4, int(0.01 * RATE))
    x[-fade:] *= np.linspace(1, 0, fade, dtype=np.float32)
    return x.astype(np.float32), prompt


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--only", choices=["beds", "spots"])
    ap.add_argument("--steps", type=int, default=50)
    a = ap.parse_args()
    lib = os.path.join(a.out, "sounds-1")
    os.makedirs(lib, exist_ok=True)
    index_path = os.path.join(lib, "library.json")
    entries = {e["id"]: e for e in json.load(open(index_path))} if os.path.exists(index_path) else {}

    def save():
        with open(index_path, "w", encoding="utf-8") as fh:
            json.dump(sorted(entries.values(), key=lambda e: e["id"]), fh, ensure_ascii=False, indent=1)

    def bed_id(key):
        return "bed_" + re.sub(r"[^0-9a-z]+", "_", key.encode("unicode_escape").decode().lower()).strip("_")

    if a.only != "spots":
        for key, words, target in BEDS:
            vid = bed_id(key)
            if vid in entries:
                continue
            st = make_bed(key, words, target, a.steps)
            write_ogg(os.path.join(lib, vid + ".ogg"), st)
            entries[vid] = {"id": vid, "key": key, "file": vid + ".ogg", "seconds": round(len(st) / RATE, 2), "loops": True, "license": LICENSE, "prompt": words}
            save()
            print("bed", key, round(rms_db(st), 1), "dB", flush=True)
    if a.only != "beds":
        for kind in event_kinds():
            for n in range(2 if kind in TWICE else 1):
                vid = f"{kind}_{n + 1}"
                if vid in entries:
                    continue
                x, prompt = make_spot(kind, n + 1, a.steps)
                write_ogg(os.path.join(lib, vid + ".ogg"), x)
                entries[vid] = {"id": vid, "key": kind, "file": vid + ".ogg", "seconds": round(len(x) / RATE, 2), "loops": False, "license": LICENSE, "prompt": prompt}
                save()
                print("spot", vid, round(len(x) / RATE, 2), "s", flush=True)
    save()
    zpath = os.path.join(a.out, "sounds-1.zip")
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_STORED) as z:
        for f in sorted(os.listdir(lib)):
            z.write(os.path.join(lib, f), f"sounds-1/{f}")
    print("wrote", zpath, os.path.getsize(zpath), "bytes", len(entries), "sounds", flush=True)


if __name__ == "__main__":
    main()
