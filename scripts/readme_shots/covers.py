"""Typographic covers for the README demo shelf.

A quiet literary line: paper or cloth, warm ink, one layout per book.
Sans faces (PingFang, Heiti, YaHei, Noto Sans, Arial, WenQuanYi) are refused
so the shelf does not look like an interface mock.
"""

from __future__ import annotations

import os
import random
from dataclasses import dataclass
from pathlib import Path

W, H = 900, 1290

SANS_MARKERS = (
    "pingfang",
    "heiti",
    "stheiti",
    "yahei",
    "microsoft yahei",
    "noto sans",
    "notosans",
    "source han sans",
    "sourcehansans",
    "arial",
    "wqy",
    "microhei",
    "droid sans",
)

# Songti / Kaiti / Source Han Serif / Noto Serif CJK. TTC index 2 is the
# Simplified Chinese face of Noto Serif CJK.
CN_FONTS: list[tuple[str, int]] = [
    ("/System/Library/Fonts/Supplemental/Songti.ttc", 0),
    ("/System/Library/Fonts/STSong.ttf", 0),
    ("/System/Library/Fonts/Supplemental/Songti.ttc", 1),
    ("/System/Library/Fonts/Supplemental/STKaiti.ttf", 0),
    ("/System/Library/Fonts/STKaiti.ttf", 0),
    ("/Library/Fonts/Songti.ttc", 0),
    ("/System/Library/Fonts/Songti.ttc", 0),
    ("C:/Windows/Fonts/simsun.ttc", 0),
    ("C:/Windows/Fonts/STSONG.TTF", 0),
    ("C:/Windows/Fonts/simkai.ttf", 0),
    ("C:/Windows/Fonts/STKAITI.TTF", 0),
    ("/usr/share/fonts/opentype/source-han-serif/SourceHanSerifSC-Regular.otf", 0),
    ("/usr/share/fonts/opentype/noto/NotoSerifCJK-Regular.ttc", 2),
    ("/usr/share/fonts/opentype/noto/NotoSerifCJKsc-Regular.otf", 0),
    ("/System/Library/Fonts/Supplemental/Songti.ttc", 3),
]

EN_FONTS = [
    "/System/Library/Fonts/Supplemental/Baskerville.ttc",
    "/Library/Fonts/Baskerville.ttc",
    "/System/Library/Fonts/Palatino.ttc",
    "/System/Library/Fonts/Supplemental/Palatino.ttc",
    "/Library/Fonts/Georgia.ttf",
    "/usr/share/fonts/truetype/liberation/LiberationSerif-Regular.ttf",
    "/usr/share/fonts/truetype/liberation2/LiberationSerif-Regular.ttf",
    "C:/Windows/Fonts/georgia.ttf",
    "C:/Windows/Fonts/pala.ttf",
    "C:/Windows/Fonts/times.ttf",
    "/usr/share/fonts/truetype/noto/NotoSerif-Regular.ttf",
]

EN_ITALIC = [
    "/System/Library/Fonts/Supplemental/Baskerville.ttc",
    "/usr/share/fonts/truetype/liberation/LiberationSerif-Italic.ttf",
    "/usr/share/fonts/truetype/liberation2/LiberationSerif-Italic.ttf",
    "C:/Windows/Fonts/georgiai.ttf",
    "C:/Windows/Fonts/timesi.ttf",
    "/usr/share/fonts/truetype/noto/NotoSerif-Italic.ttf",
]


@dataclass
class Cover:
    layout: str
    title: str
    author: str = ""
    bg: str = "#EFE4D2"
    ink: str = "#2C241C"
    band: str = ""
    english: str = ""
    mark: str = ""
    # 朝花夕拾: two lines of two characters, instead of one vertical column.
    pair: tuple[str, str] | None = None


def _banned(path: str) -> bool:
    low = path.lower()
    return any(mark in low for mark in SANS_MARKERS)


def _first_font(candidates: list, size: int, italic: bool = False):
    from PIL import ImageFont

    env = os.environ.get("YY_SHOT_FONT")
    if env and not italic and not _banned(env) and Path(env).is_file():
        candidates = [(env, 0)] + list(candidates)
    for item in candidates:
        if isinstance(item, tuple):
            path, index = item
        else:
            path, index = item, 0
        if not path or _banned(path) or not Path(path).is_file():
            continue
        try:
            return ImageFont.truetype(path, size=size, index=index)
        except OSError:
            continue
    raise RuntimeError("没有找到可用的衬线字体。请安装宋体、思源宋体或 Noto Serif CJK。")


def cn_font(size: int):
    return _first_font(CN_FONTS, size)


def en_font(size: int, italic: bool = False):
    return _first_font(EN_ITALIC if italic else EN_FONTS, size, italic=italic)


def _hex(color: str) -> tuple[int, int, int]:
    color = color.lstrip("#")
    return tuple(int(color[i : i + 2], 16) for i in (0, 2, 4))


def _mix(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def _ground(title: str, bg: str, cloth: bool):
    from PIL import Image, ImageChops

    w, h = W, H
    base = _hex(bg)
    im = Image.new("RGB", (w, h), base)
    # A slight vertical wash, lighter at the head of the sheet.
    wash = Image.new("L", (1, h))
    for y in range(h):
        t = y / (h - 1)
        wash.putpixel((0, y), int(255 - (22 if cloth else 16) * t))
    wash = wash.resize((w, h))
    im = ImageChops.multiply(im, Image.merge("RGB", (wash, wash, wash)))
    rng = random.Random(title)
    noise = Image.new("L", (w, h))
    pix = noise.load()
    amp = 18 if cloth else 11
    for y in range(0, h, 2):
        for x in range(0, w, 2):
            v = 128 + rng.randint(-amp, amp)
            pix[x, y] = v
            if x + 1 < w:
                pix[x + 1, y] = v
            if y + 1 < h:
                pix[x, y + 1] = v
                if x + 1 < w:
                    pix[x + 1, y + 1] = v
    im = Image.blend(im, ImageChops.multiply(im, Image.merge("RGB", (noise, noise, noise))), 0.35 if cloth else 0.22)
    return im


def _rule(draw, cx: float, y: float, length: float, color: str, weight: int = 2) -> None:
    draw.line([(cx - length / 2, y), (cx + length / 2, y)], fill=color, width=weight)


def _vertical(draw, text: str, font, fill: str, cx: float, top: float, leading: float | None = None) -> float:
    step = leading if leading is not None else font.size * 1.02
    y = top
    last = top
    for ch in text:
        box = draw.textbbox((0, 0), ch, font=font)
        x = cx - (box[0] + box[2]) / 2
        draw.text((x, y - box[1]), ch, font=font, fill=fill)
        last = y - box[1] + box[3]
        y += step
    return last


def _double_frame(draw, color: str, inset: int = 54, gap: int = 10) -> None:
    draw.rectangle([inset, inset, W - inset, H - inset], outline=color, width=2)
    i2 = inset + gap
    draw.rectangle([i2, i2, W - i2, H - i2], outline=color, width=2)


def _center_text(draw, text: str, font, fill: str, y: float) -> float:
    box = draw.textbbox((0, 0), text, font=font)
    x = (W - (box[2] - box[0])) / 2 - box[0]
    draw.text((x, y - box[1]), text, font=font, fill=fill)
    return y - box[1] + box[3]


def render(spec: Cover) -> bytes:
    import io

    from PIL import ImageDraw

    im = _ground(spec.title, spec.bg, spec.layout == "cloth")
    draw = ImageDraw.Draw(im)
    layout = spec.layout
    if layout == "bigchar":
        _paint_bigchar(draw, spec)
    elif layout == "column":
        _paint_column(draw, spec)
    elif layout == "mark":
        _paint_mark(draw, spec)
    elif layout == "horizon":
        _paint_horizon(draw, spec)
    elif layout == "cloth":
        _paint_cloth(draw, spec)
    elif layout == "panel":
        _paint_panel(im, draw, spec)
    else:
        raise ValueError(spec.layout)
    buf = io.BytesIO()
    im.save(buf, format="PNG")
    return buf.getvalue()


def _paint_bigchar(draw, spec: Cover) -> None:
    if spec.pair:
        font = cn_font(188)
        gap = 28
        lines = spec.pair
        heights = []
        for line in lines:
            box = draw.textbbox((0, 0), line, font=font)
            heights.append(box)
        block = sum(b[3] - b[1] for b in heights) + gap
        y = (H * 0.42) - block / 2
        bottom = y
        for line, box in zip(lines, heights):
            bottom = _center_text(draw, line, font, spec.ink, y)
            y = bottom + gap
        _rule(draw, W / 2, bottom + 48, 56, spec.ink, 2)
        if spec.author:
            _center_text(draw, spec.author, cn_font(40), spec.ink, bottom + 88)
        return
    font = cn_font(250 if len(spec.title) <= 2 else 168)
    block_h = font.size * 1.02 * len(spec.title)
    top = (H * 0.46) - block_h / 2
    bottom = _vertical(draw, spec.title, font, spec.ink, W / 2, top)
    rule_y = bottom + 56
    _rule(draw, W / 2, rule_y, 48, spec.ink, 2)
    if spec.author:
        _center_text(draw, spec.author, cn_font(40), spec.ink, rule_y + 40)


def _paint_column(draw, spec: Cover) -> None:
    _double_frame(draw, spec.ink, 64, 12)
    font = cn_font(150)
    block_h = font.size * 1.08 * len(spec.title)
    top = (H * 0.42) - block_h / 2
    bottom = _vertical(draw, spec.title, font, spec.ink, W / 2, top, font.size * 1.12)
    rule_y = bottom + 48
    _rule(draw, W / 2, rule_y, 64, spec.ink, 2)
    if spec.author:
        _center_text(draw, spec.author, cn_font(36), spec.ink, rule_y + 36)


def _paint_mark(draw, spec: Cover) -> None:
    _double_frame(draw, spec.ink, 48, 10)
    mark_font = cn_font(340)
    mark_bottom = _center_text(draw, spec.mark, mark_font, spec.ink, 210)
    title_font = cn_font(78)
    title_bottom = _center_text(draw, spec.title, title_font, spec.ink, mark_bottom + 36)
    if spec.author:
        _center_text(draw, spec.author, cn_font(36), spec.ink, title_bottom + 28)


def _paint_horizon(draw, spec: Cover) -> None:
    ink = spec.ink
    margin = 78
    draw.line([(margin, 92), (W - margin, 92)], fill=ink, width=1)
    draw.line([(margin, H - 92), (W - margin, H - 92)], fill=ink, width=1)
    font = cn_font(92)
    tracking = 36
    boxes = [draw.textbbox((0, 0), ch, font=font) for ch in spec.title]
    widths = [b[2] - b[0] for b in boxes]
    total = sum(widths) + tracking * (len(spec.title) - 1)
    x = (W - total) / 2
    y = H * 0.40
    bottom = y
    for ch, box, width in zip(spec.title, boxes, widths):
        draw.text((x - box[0], y - box[1]), ch, font=font, fill=ink)
        bottom = max(bottom, y - box[1] + box[3])
        x += width + tracking
    rule_y = bottom + 42
    _rule(draw, W / 2, rule_y, 72, ink, 2)
    if spec.author:
        _center_text(draw, spec.author, cn_font(36), ink, rule_y + 36)


def _paint_cloth(draw, spec: Cover) -> None:
    ink = spec.ink
    draw.rectangle([58, 58, W - 58, H - 58], outline=ink, width=2)
    font = cn_font(108 if len(spec.title) >= 4 else 132)
    block_h = font.size * 1.08 * len(spec.title)
    top = (H * 0.46) - block_h / 2
    bottom = _vertical(draw, spec.title, font, ink, W / 2, top, font.size * 1.14)
    if spec.author:
        _center_text(draw, spec.author, cn_font(34), ink, max(bottom + 48, H - 150))


def _paint_panel(im, draw, spec: Cover) -> None:
    from PIL import ImageDraw

    band_top, band_bot = int(H * 0.30), int(H * 0.74)
    overlay = ImageDraw.Draw(im)
    overlay.rectangle([0, band_top, W, band_bot], fill=spec.band)
    draw = overlay
    author_font = cn_font(40)
    author_bottom = _center_text(draw, spec.author, author_font, spec.ink, band_top + 56)
    rule_y = author_bottom + 36
    _rule(draw, W / 2, rule_y, 56, spec.ink, 2)
    title_font = cn_font(86)
    _center_text(draw, spec.title, title_font, spec.ink, rule_y + 48)
    if spec.english:
        en = en_font(40, italic=True)
        _center_text(draw, spec.english, en, spec.ink, H - 118)


def contact_sheet(specs: list[Cover], out: Path) -> Path:
    from PIL import Image, ImageDraw

    import io

    thumbs = []
    for spec in specs:
        im = Image.open(io.BytesIO(render(spec))).convert("RGB")
        im.thumbnail((180, 260))
        thumbs.append(im)
    cols = 6
    pad = 18
    label = 28
    cell_w, cell_h = 180, 260
    rows = (len(thumbs) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * (cell_w + pad) + pad, rows * (cell_h + label + pad) + pad), (241, 235, 227))
    draw = ImageDraw.Draw(sheet)
    font = cn_font(16)
    for i, (im, spec) in enumerate(zip(thumbs, specs)):
        r, c = divmod(i, cols)
        x = pad + c * (cell_w + pad)
        y = pad + r * (cell_h + label + pad)
        sheet.paste(im, (x, y))
        draw.text((x, y + cell_h + 4), spec.title, font=font, fill=(44, 36, 28))
    out.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out, "PNG")
    return out
