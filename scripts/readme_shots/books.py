"""The 14-book public-domain shelf for README screenshots.

Seven Chinese books and seven English books. Texts are vendored in
fixtures/readme-shelf (Wikisource, converted to simplified Chinese, and
Project Gutenberg). Great Gatsby and Pride and Prejudice keep the cover
art already in fixtures/books.
"""

from __future__ import annotations

import hashlib
import json
import posixpath
import re
import shutil
import zipfile
from pathlib import Path

from readme_shots.covers import Cover, render

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "fixtures" / "books"
SHELF = ROOT / "fixtures" / "readme-shelf"

ANCHOR = "花开了，就像花睡醒了似的。"

CONTAINER = """<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
"""

STYLE = """body { margin: 0; }
h1, h2 { text-align: center; }
p { text-indent: 2em; margin: 0 0 0.6em; }
p.verse { text-indent: 0; text-align: center; }
.author { text-align: center; text-indent: 0; }
"""

# filename, cover. None means copy the fixture epub and keep its cover.
CATALOG: list[tuple[str, Cover | None]] = [
    ("yecao.json", Cover("bigchar", "野草", "鲁迅", "#2A2A28", "#EDE6D8")),
    ("zhaohua.json", Cover("bigchar", "朝花夕拾", "鲁迅", "#D8D5C6", "#2C241C", pair=("朝花", "夕拾"))),
    ("hulan.json", Cover("cloth", "呼兰河传", "萧红", "#243028", "#E6E0D2")),
    ("fusheng.json", Cover("horizon", "浮生六记", "沈复", "#E6D6C8", "#3A2C28")),
    ("renjian.json", Cover("horizon", "人间词话", "王国维", "#EFE8DC", "#2C241C")),
    ("zhimo.json", Cover("mark", "志摩的诗", "徐志摩", "#E8D4C4", "#2C241C", mark="诗")),
    ("shishuo.json", Cover("horizon", "世说新语", "刘义庆", "#D3D6D2", "#1E2824")),
    ("great-gatsby.epub", None),
    ("pride-and-prejudice.epub", None),
    ("dalloway.json", Cover("latin", "Mrs Dalloway", "Virginia Woolf", "#4A3034", "#F0E6D8", band="#5A3A3E")),
    ("walden.json", Cover("latin", "Walden", "Henry David Thoreau", "#24332A", "#E8E2D4", band="#2E4436")),
    ("meditations.json", Cover("latin", "Meditations", "Marcus Aurelius", "#2C3038", "#E8E2D6", english="George Long")),
    ("dubliners.json", Cover("latin", "Dubliners", "James Joyce", "#D4D2CE", "#1C2430")),
    ("prophet.json", Cover("latin", "The Prophet", "Kahlil Gibran", "#E6D3C0", "#3A2C28")),
]


def esc(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def xhtml(title: str, lang: str, body: str) -> str:
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="{lang}" lang="{lang}">\n'
        f"<head><title>{esc(title)}</title>\n"
        '<link rel="stylesheet" type="text/css" href="../style.css"/>\n</head>\n'
        f"<body>\n{body}\n</body></html>\n"
    )


def write_epub(path: Path, files: list[tuple[str, bytes | str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        path.unlink()
    with zipfile.ZipFile(path, "w") as zf:
        info = zipfile.ZipInfo("mimetype")
        info.compress_type = zipfile.ZIP_STORED
        zf.writestr(info, "application/epub+zip")
        for name, data in files:
            raw = data if isinstance(data, bytes) else data.encode("utf-8")
            zf.writestr(name, raw, compress_type=zipfile.ZIP_DEFLATED)


def _verse(text: str) -> bool:
    lines = [ln.strip() for ln in text.split("\n") if ln.strip()]
    if len(lines) < 4:
        return False
    short = sum(1 for ln in lines if len(ln) < 18)
    return short >= len(lines) * 0.6


def _normalize(text: str, lang: str) -> str:
    text = text.replace("\u200b", "").strip()
    if lang == "en":
        # A drop cap is stored as "F\\nROM ...".
        match = re.match(r"^([A-Z])\n([A-Z]+)([\s\S]*)", text)
        if match:
            word = (match.group(1) + match.group(2)).capitalize()
            text = word + match.group(3)
        text = re.sub(r"\s*\n\s*", " ", text)
        return re.sub(r" {2,}", " ", text).strip()
    if _verse(text):
        return "\n".join(ln.strip() for ln in text.split("\n") if ln.strip())
    return "".join(ln.strip() for ln in text.split("\n") if ln.strip())


def _paragraph_html(text: str) -> str:
    if "\n" in text:
        inner = "<br/>".join(esc(ln) for ln in text.split("\n"))
        return f'<p class="verse">{inner}</p>\n'
    return f"<p>{esc(text)}</p>\n"


def load_text(name: str) -> tuple[dict, list[tuple[str, list[str]]]]:
    data = json.loads((SHELF / name).read_text(encoding="utf-8"))
    lang = data["lang"]
    sections: list[tuple[str, list[str]]] = []
    for sec in data["sections"]:
        paras = []
        for para in sec["paragraphs"]:
            cleaned = _normalize(para, lang)
            if cleaned:
                paras.append(cleaned)
        if paras:
            sections.append((sec["heading"], paras))
    if not sections:
        raise SystemExit(f"{name} 没有正文")
    if name == "hulan.json":
        blob = "\n".join(p for _, paras in sections for p in paras)
        if ANCHOR not in blob:
            raise SystemExit("呼兰河传里没有「花开了，就像花睡醒了似的」。")
    if name == "meditations.json" and "good morals" not in "\n".join(p for _, paras in sections for p in paras):
        raise SystemExit("Meditations 不是 George Long 的译文")
    return data, sections


def build_book(path: Path, data: dict, sections: list[tuple[str, list[str]]], cover: Cover) -> None:
    title = data["title"]
    author = data["author"]
    lang = data["lang"]
    ident = "urn:yuyue:readme:" + path.stem
    items = [
        ("cover", "cover.png", "image/png", "cover-image"),
        ("css", "style.css", "text/css", None),
        ("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
        ("ncx", "toc.ncx", "application/x-dtbncx+xml", None),
    ]
    spine = []
    bodies = []
    toc = []
    for i, (heading, paragraphs) in enumerate(sections, start=1):
        sid = f"c{i}"
        href = f"text/{sid}.xhtml"
        items.append((sid, href, "application/xhtml+xml", None))
        spine.append(sid)
        toc.append((heading, href))
        paras = "".join(_paragraph_html(p) for p in paragraphs)
        if i == 1:
            head = f"<h1>{esc(title)}</h1>\n<p class=\"author\">{esc(author)}</p>\n<h2>{esc(heading)}</h2>\n{paras}"
        else:
            head = f"<h2>{esc(heading)}</h2>\n{paras}"
        bodies.append((f"OEBPS/{href}", xhtml(heading, lang, head)))
    manifest = "\n".join(
        f'    <item id="{i}" href="{h}" media-type="{m}"' + (f' properties="{p}"' if p else "") + "/>"
        for i, h, m, p in items
    )
    spine_xml = "\n".join(f'    <itemref idref="{i}"/>' for i in spine)
    source = data.get("source") or ""
    translator = data.get("translator") or ""
    extra = f"\n    <dc:contributor>{esc(translator)}</dc:contributor>" if translator else ""
    opf = f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid" xml:lang="{lang}">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="uid">{ident}</dc:identifier>
    <dc:title>{esc(title)}</dc:title>
    <dc:creator>{esc(author)}</dc:creator>{extra}
    <dc:language>{lang}</dc:language>
    <dc:source>{esc(source)}</dc:source>
    <meta name="cover" content="cover"/>
    <meta property="dcterms:modified">2026-11-27T00:00:00Z</meta>
  </metadata>
  <manifest>
{manifest}
  </manifest>
  <spine toc="ncx">
{spine_xml}
  </spine>
</package>
"""
    nav_items = "\n".join(f"      <li><a href=\"{h}\">{esc(t)}</a></li>" for t, h in toc)
    nav = xhtml("目录", lang, f"<nav><h1>目录</h1><ol>\n{nav_items}\n</ol></nav>").replace(
        '<link rel="stylesheet" type="text/css" href="../style.css"/>\n', ""
    )
    points = "\n".join(
        f'    <navPoint id="p{n}" playOrder="{n}"><navLabel><text>{esc(t)}</text></navLabel><content src="{h}"/></navPoint>'
        for n, (t, h) in enumerate(toc, 1)
    )
    ncx = f"""<?xml version="1.0" encoding="utf-8"?>
<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
  <head><meta name="dtb:uid" content="{ident}"/></head>
  <docTitle><text>{esc(title)}</text></docTitle>
  <navMap>
{points}
  </navMap>
</ncx>
"""
    files: list[tuple[str, bytes | str]] = [
        ("META-INF/container.xml", CONTAINER),
        ("OEBPS/content.opf", opf),
        ("OEBPS/nav.xhtml", nav),
        ("OEBPS/toc.ncx", ncx),
        ("OEBPS/style.css", STYLE),
        ("OEBPS/cover.png", render(cover)),
        *bodies,
    ]
    write_epub(path, files)


def _item_href(opf: str, item_id: str) -> str | None:
    match = re.search(rf'<item\b[^>]*\bid="{re.escape(item_id)}"[^>]*\bhref="([^"]+)"', opf)
    if match:
        return match.group(1)
    match = re.search(rf'<item\b[^>]*\bhref="([^"]+)"[^>]*\bid="{re.escape(item_id)}"', opf)
    return match.group(1) if match else None


def epub_cover_bytes(path: Path) -> bytes:
    with zipfile.ZipFile(path) as zf:
        opf_name = next(n for n in zf.namelist() if n.endswith(".opf"))
        opf = zf.read(opf_name).decode("utf-8", "replace")
        href = None
        meta = re.search(r'<meta\b[^>]*\bname="cover"[^>]*\bcontent="([^"]+)"', opf)
        if not meta:
            meta = re.search(r'<meta\b[^>]*\bcontent="([^"]+)"[^>]*\bname="cover"', opf)
        if meta:
            href = _item_href(opf, meta.group(1))
        if href is None:
            match = re.search(r'<item\b[^>]*\bhref="([^"]+)"[^>]*\bproperties="[^"]*cover-image', opf)
            if not match:
                match = re.search(r'<item\b[^>]*\bproperties="[^"]*cover-image[^"]*"[^>]*\bhref="([^"]+)"', opf)
            href = match.group(1) if match else None
        if not href:
            raise SystemExit(f"{path.name} 没有封面")
        target = posixpath.normpath(str(Path(opf_name).parent / href))
        return zf.read(target)


def prepare(out: Path) -> list[Path]:
    """Write exactly 14 EPUBs. Returns them in shelf order."""
    out.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []
    for name, cover in CATALOG:
        if name.endswith(".epub"):
            dest = out / name
            shutil.copyfile(FIXTURES / name, dest)
            written.append(dest)
            continue
        data, sections = load_text(name)
        dest = out / (Path(name).stem + ".epub")
        if cover is None:
            raise SystemExit(name)
        build_book(dest, data, sections, cover)
        written.append(dest)
    if len(written) != 14:
        raise SystemExit(f"演示书架应该是 14 本，实际写出 {len(written)} 本")
    return written


def shelf_covers() -> list[tuple[str, bytes]]:
    """Title and PNG/JPEG bytes for all 14 covers, originals included."""
    items: list[tuple[str, bytes]] = []
    for name, cover in CATALOG:
        if cover is not None:
            items.append((cover.title, render(cover)))
            continue
        title = "The Great Gatsby" if "gatsby" in name else "Pride and Prejudice"
        items.append((title, epub_cover_bytes(FIXTURES / name)))
    return items


def same_file(a: Path, b: Path) -> bool:
    return hashlib.sha256(a.read_bytes()).digest() == hashlib.sha256(b.read_bytes()).digest()
