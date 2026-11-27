"""Place real screenshots on a warm paper ground.

Raw captures win over the older docs/screenshots files. Windows, iPad and
Android are added when their raw files exist. Nothing here draws an app UI.
"""

from __future__ import annotations

import os
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[2]
LEGACY = ROOT / "docs" / "screenshots"
RAW = LEGACY / "raw"
OUT = LEGACY / "composed"

PAPER = (241, 235, 227, 255)
INK = (110, 107, 104, 255)
SHADOW = (58, 50, 40, 40)

BEGIN = "<!-- readme-shots:begin -->"
END = "<!-- readme-shots:end -->"


def _load(path: Path) -> Image.Image:
    return Image.open(path).convert("RGBA")


def _alpha_bbox(im: Image.Image) -> tuple[int, int, int, int]:
    bbox = im.getchannel("A").point(lambda a: 255 if a > 12 else 0).getbbox()
    return bbox or (0, 0, im.width, im.height)


def trim(im: Image.Image) -> Image.Image:
    box = _alpha_bbox(im)
    return im.crop(box)


def _corner_radius(im: Image.Image) -> int:
    px = im.load()
    w, h = im.size
    # How far the top edge stays transparent: the window's own corner.
    inset = 0
    y = min(2, h - 1)
    for x in range(w // 2):
        if px[x, y][3] > 20:
            inset = x
            break
    return max(18, min(inset, 80))


def _round(im: Image.Image, radius: int) -> Image.Image:
    if radius <= 0:
        return im
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, im.width - 1, im.height - 1), radius=radius, fill=255)
    out = im.copy()
    out.putalpha(ImageChops.darker(out.getchannel("A"), mask))
    return out


def crop_empty_shelf(im: Image.Image) -> Image.Image:
    """Cut the empty paper under a shelf grid, then round the bottom like the window."""
    im = trim(im)
    w, h = im.size
    radius = _corner_radius(im)
    paper = (247, 245, 241)
    px = im.load()
    left = int(w * 0.28)
    right = int(w * 0.96)
    # The window's own bottom edge is a non-paper line. Start above it,
    # otherwise the frame looks like content and the empty shelf stays.
    last = 0
    for y in range(h - radius - 16, int(h * 0.35), -2):
        varied = 0
        step = 6
        for x in range(left, right, step):
            r, g, b, a = px[x, y]
            if a < 200:
                continue
            if abs(r - paper[0]) > 14 or abs(g - paper[1]) > 14 or abs(b - paper[2]) > 14:
                varied += 1
        if varied > 6:
            last = y
            break
    if last <= 0:
        return im
    gap = h - last
    if gap < int(h * 0.16):
        return im
    keep = min(h, last + max(36, int(h * 0.045)))
    cropped = im.crop((0, 0, w, keep))
    return _round(cropped, radius)


def shadow(im: Image.Image, blur: int = 28, offset: tuple[int, int] = (0, 14)) -> Image.Image:
    pad = blur * 2
    canvas = Image.new("RGBA", (im.width + pad * 2, im.height + pad * 2), (0, 0, 0, 0))
    alpha = im.getchannel("A")
    shade = Image.new("RGBA", im.size, SHADOW)
    shade.putalpha(alpha.point(lambda a: min(255, int(a * 0.55))))
    canvas.paste(shade, (pad + offset[0], pad + offset[1]), shade)
    blurred = canvas.filter(ImageFilter.GaussianBlur(blur))
    blurred.paste(im, (pad, pad), im)
    return blurred


def _font(size: int):
    for path in (
        os.environ.get("YY_SHOT_FONT"),
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/System/Library/Fonts/Supplemental/Avenir.ttc",
        "/System/Library/Fonts/Helvetica.ttc",
        "C:/Windows/Fonts/segoeui.ttf",
        "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
    ):
        if path and Path(path).is_file():
            try:
                return ImageFont.truetype(path, size=size)
            except OSError:
                continue
    return ImageFont.load_default()


def _fit(im: Image.Image, height: int) -> Image.Image:
    if im.height == height:
        return im
    w = max(1, round(im.width * height / im.height))
    return im.resize((w, height), Image.Resampling.LANCZOS)


def plate(items: list[tuple[Image.Image, str | None]], gap: int = 36, margin: int = 56, label_h: int = 0) -> Image.Image:
    font = _font(22)
    tops = []
    for im, label in items:
        extra = 36 if label else 0
        tops.append(extra)
    height = max(im.height + extra for (im, _), extra in zip(items, tops))
    width = margin * 2 + sum(im.width for im, _ in items) + gap * (len(items) - 1)
    canvas = Image.new("RGBA", (width, height + margin * 2), PAPER)
    draw = ImageDraw.Draw(canvas)
    x = margin
    for im, label in items:
        extra = 36 if label else 0
        y = margin + (height - im.height - extra)
        if label:
            draw.text((x, y), label, font=font, fill=INK)
            y += extra
        canvas.paste(im, (x, y), im)
        x += im.width + gap
    return canvas


def prepare_window(path: Path, shelf: bool) -> Image.Image:
    im = trim(_load(path))
    if shelf:
        im = crop_empty_shelf(im)
    # Opaque phone/desktop grabs get a light corner so they sit with the Mac shots.
    if im.getextrema()[3][0] > 250:
        im = _round(im, max(28, im.width // 48))
    return shadow(im)


def first_existing(candidates: list[Path]) -> Path | None:
    for path in candidates:
        if path.is_file() and path.stat().st_size > 1000:
            return path
    return None


def mac_shelf() -> Path | None:
    return first_existing([RAW / "macos" / "01-shelf.png", LEGACY / "01-shelf.png"])


def mac_listen() -> Path | None:
    return first_existing([RAW / "macos" / "02-listening.png", LEGACY / "02-listening.png"])


def mac_notes() -> Path | None:
    return first_existing([RAW / "macos" / "03-notes.png", LEGACY / "03-notes.png"])


def mac_english() -> Path | None:
    return first_existing([RAW / "macos" / "04-english.png", LEGACY / "04-english.png"])


def win(name: str) -> Path | None:
    return first_existing([RAW / "windows" / name])


def phone(slot: str, legacy: str) -> Path | None:
    return first_existing([RAW / "iphone" / slot, LEGACY / legacy])


def android(name: str) -> Path | None:
    return first_existing([RAW / "android" / name])


def ipad(name: str) -> Path | None:
    return first_existing([RAW / "ipad" / name])


def save(im: Image.Image, name: str) -> Path:
    OUT.mkdir(parents=True, exist_ok=True)
    # Keep README images sharp but not enormous.
    max_w = 2800
    if im.width > max_w:
        h = round(im.height * max_w / im.width)
        im = im.resize((max_w, h), Image.Resampling.LANCZOS)
    path = OUT / name
    im.convert("RGB").save(path, "PNG", optimize=True)
    print(f"  {path.relative_to(ROOT)}  {im.width}×{im.height}")
    return path


def row(paths: list[tuple[Path, str | None]], height: int, shelf_flags: list[bool] | None = None) -> Image.Image:
    windows = []
    for i, (path, label) in enumerate(paths):
        shelf = bool(shelf_flags and shelf_flags[i])
        im = _fit(prepare_window(path, shelf), height)
        windows.append((im, label))
    return plate(windows)


def compose() -> dict[str, bool]:
    OUT.mkdir(parents=True, exist_ok=True)
    made: dict[str, bool] = {}
    shelf_parts: list[tuple[Path, str | None]] = []
    flags: list[bool] = []
    mac = mac_shelf()
    windows = win("01-shelf.png")
    if mac:
        shelf_parts.append((mac, "Mac" if windows else None))
        flags.append(True)
    if windows:
        shelf_parts.append((windows, "Windows"))
        flags.append(True)
    if shelf_parts:
        save(row(shelf_parts, 980 if windows else 1100, flags), "shelf.png")
        made["shelf"] = True
        made["windows_shelf"] = windows is not None
    else:
        made["shelf"] = False
        made["windows_shelf"] = False

    listen, notes = mac_listen(), mac_notes()
    if listen and notes:
        save(row([(listen, None), (notes, None)], 860), "reading.png")
        made["reading"] = True
    else:
        made["reading"] = False
    wlisten, wnotes = win("02-listening.png"), win("03-notes.png")
    if wlisten and wnotes:
        save(row([(wlisten, None), (wnotes, None)], 860), "reading-windows.png")
        made["reading_windows"] = True
    else:
        made["reading_windows"] = False

    eng = mac_english()
    if eng:
        save(row([(eng, None)], 860), "english.png")
        made["english"] = True
    else:
        made["english"] = False

    ipad_shelf = ipad("01-shelf.png")
    if ipad_shelf:
        save(row([(ipad_shelf, "iPad")], 900, [True]), "ipad.png")
        made["ipad"] = True
    else:
        made["ipad"] = False

    phones = [
        (phone("01-shelf.png", "06-mobile-shelf.png"), "iPhone"),
        (android("01-shelf.png"), "Android"),
        (phone("02-reading.png", "07-mobile-reading.png"), None),
        (phone("04-notes.png", "08-mobile-notes.png"), None),
    ]
    # Drop empty slots. Keep labels only when both phone platforms are present,
    # otherwise the legacy iPhone row stays unlabeled and quiet.
    present = [(p, lab) for p, lab in phones if p]
    both = android("01-shelf.png") is not None and phone("01-shelf.png", "06-mobile-shelf.png") is not None
    if not both:
        present = [(p, None) for p, _ in present]
    if present:
        # Three across reads better than four squeezed.
        first, rest = present[:3], present[3:]
        save(row(first, 780), "phones.png")
        made["phones"] = True
        if rest:
            save(row(rest, 780), "phones-rest.png")
    else:
        made["phones"] = False
    more = [
        phone("09-places.png", "09-mobile-places.png"),
        phone("10-podcast.png", "10-mobile-podcast.png"),
        phone("11-typeset.png", "11-mobile-typeset.png"),
    ]
    more = [p for p in more if p]
    if len(more) == 3:
        save(row([(p, None) for p in more], 780), "phones-more.png")
        made["phones_more"] = True
    else:
        made["phones_more"] = False
    made["android"] = android("01-shelf.png") is not None
    return made


def readme_block(made: dict[str, bool]) -> str:
    parts = ['<p align="center">', '  <img src="docs/screenshots/composed/shelf.png" alt="语阅书架" width="100%" />', "</p>"]
    if made.get("windows_shelf"):
        parts.append('<p align="center">Mac 与 Windows，同一套演示书库。</p>')
    else:
        parts.append(
            '<p align="center">Mac 书架。Windows 用 <code>pwsh scripts/capture-readme.ps1</code> 拍好后，再跑一次合成，会排在旁边。</p>'
        )
    if made.get("reading"):
        parts += [
            '<p align="center">',
            '  <img src="docs/screenshots/composed/reading.png" alt="听书与笔记" width="100%" />',
            "</p>",
        ]
    if made.get("reading_windows"):
        parts += [
            '<p align="center">',
            '  <img src="docs/screenshots/composed/reading-windows.png" alt="Windows 听书与笔记" width="100%" />',
            "</p>",
        ]
    if made.get("english"):
        parts += [
            '<p align="center">',
            '  <img src="docs/screenshots/composed/english.png" alt="外文原著" width="78%" />',
            "</p>",
        ]
    if made.get("ipad"):
        parts += [
            '<p align="center">',
            '  <img src="docs/screenshots/composed/ipad.png" alt="iPad 书架" width="78%" />',
            "</p>",
        ]
    return "\n".join(parts)


def phone_block(made: dict[str, bool]) -> str:
    lines = []
    if made.get("phones"):
        lines += [
            '<p align="center">',
            '  <img src="docs/screenshots/composed/phones.png" alt="手机上的语阅" width="100%" />',
            "</p>",
        ]
    if made.get("android"):
        lines.append('<p align="center">iPhone 与 Android。</p>')
    else:
        lines.append(
            '<p align="center">iPhone。Android 模拟器拍完后，同一张图里会并排放上。</p>'
        )
    if made.get("phones_more"):
        lines += [
            '<p align="center">',
            '  <img src="docs/screenshots/composed/phones-more.png" alt="手机上的书架、播客与排版" width="100%" />',
            "</p>",
        ]
    return "\n".join(lines)


def rewrite_readme(made: dict[str, bool]) -> None:
    path = ROOT / "README.md"
    text = path.read_text(encoding="utf-8")
    block = readme_block(made) + "\n"
    if BEGIN not in text or END not in text:
        raise SystemExit("README.md 缺少 readme-shots 标记")
    pre, rest = text.split(BEGIN, 1)
    _, post = rest.split(END, 1)
    text = pre + BEGIN + "\n" + block + END + post
    # Phone block is the second marked region.
    b2, e2 = "<!-- readme-shots:phones:begin -->", "<!-- readme-shots:phones:end -->"
    if b2 in text and e2 in text:
        pre, rest = text.split(b2, 1)
        _, post = rest.split(e2, 1)
        text = pre + b2 + "\n" + phone_block(made) + "\n" + e2 + post
    path.write_text(text, encoding="utf-8")
    print(f"README 截图区已更新")
