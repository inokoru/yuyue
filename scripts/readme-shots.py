#!/usr/bin/env python3
"""Demo library, README composition, and a small self-check.

  python3 scripts/readme-shots.py books --out build/readme-shots/books
  python3 scripts/readme-shots.py seed --library build/readme-shots/library
  python3 scripts/readme-shots.py focus --library build/readme-shots/library --mode listen
  python3 scripts/readme-shots.py compose
  python3 scripts/readme-shots.py self-check
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))

from readme_shots.books import prepare  # noqa: E402
from readme_shots.seed import apply_plan, focus, write_windows_prefs  # noqa: E402

SCHEMA = """
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE ops (
    device TEXT NOT NULL, seq INTEGER NOT NULL, hlc TEXT NOT NULL,
    entity TEXT NOT NULL, field TEXT NOT NULL, value TEXT NOT NULL,
    PRIMARY KEY (device, seq));
CREATE TABLE registers (
    entity TEXT NOT NULL, field TEXT NOT NULL, value TEXT NOT NULL, hlc TEXT NOT NULL,
    PRIMARY KEY (entity, field));
CREATE TABLE clock (device TEXT PRIMARY KEY, seq INTEGER NOT NULL);
CREATE TABLE conflicts (
    entity TEXT NOT NULL, field TEXT NOT NULL,
    a_value TEXT NOT NULL, a_hlc TEXT NOT NULL, b_value TEXT NOT NULL, b_hlc TEXT NOT NULL,
    PRIMARY KEY (entity, field));
"""


def _real_library(path: Path) -> None:
    resolved = path.resolve()
    banned = []
    home = Path.home()
    banned.append(home / "Library" / "Application Support" / "Yuyue")
    local = Path.home() / "AppData" / "Local" / "Yuyue"
    banned.append(local)
    for bad in banned:
        try:
            resolved.relative_to(bad.resolve())
        except ValueError:
            continue
        raise SystemExit(f"拒绝使用真实书库：{resolved}")


def _need_pillow() -> None:
    try:
        import PIL  # noqa: F401
    except ImportError:
        raise SystemExit(
            "缺少 Pillow，封面和合成图都要用它。\n"
            "  python3 -m pip install --user pillow\n"
            "  macOS 上若 pip 不可用：brew install pillow\n"
            "  Windows：py -m pip install pillow"
        )


def cmd_books(args: argparse.Namespace) -> None:
    _need_pillow()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    paths = prepare(out)
    print(f"{len(paths)} 本书 → {out}")


def cmd_seed(args: argparse.Namespace) -> None:
    library = Path(args.library)
    _real_library(library)
    apply_plan(library)


def cmd_focus(args: argparse.Namespace) -> None:
    library = Path(args.library)
    _real_library(library)
    focus(library, args.mode)


def cmd_prefs(args: argparse.Namespace) -> None:
    library = Path(args.library)
    _real_library(library)
    write_windows_prefs(library, args.open_book, args.width, args.height)


def cmd_compose(_: argparse.Namespace) -> None:
    _need_pillow()
    from readme_shots.compose import compose, rewrite_readme

    made = compose()
    rewrite_readme(made)


def cmd_covers(args: argparse.Namespace) -> None:
    _need_pillow()
    from readme_shots.books import shelf_covers
    from readme_shots.covers import contact_sheet_images

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    items = shelf_covers()
    for title, raw in items:
        ext = "jpg" if raw[:3] == b"\xff\xd8\xff" else "png"
        (out / f"{title}.{ext}").write_bytes(raw)
    sheet = contact_sheet_images(items, out / "sheet.png")
    print(f"{len(items)} 张封面 → {sheet}")


def cmd_self_check(_: argparse.Namespace) -> None:
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        books = prepare(root / "books")
        names = {p.name for p in books}
        assert len(books) == 14, len(books)
        assert "beiying.epub" not in names and "hongloumeng.epub" not in names and "guwen.epub" not in names
        from readme_shots.books import ANCHOR, FIXTURES, same_file
        from readme_shots.seed import NOTE_TEXT
        assert same_file(root / "books" / "great-gatsby.epub", FIXTURES / "great-gatsby.epub")
        assert same_file(root / "books" / "pride-and-prejudice.epub", FIXTURES / "pride-and-prejudice.epub")
        import zipfile
        with zipfile.ZipFile(root / "books" / "hulan.epub") as zf:
            opf = zf.read("OEBPS/content.opf").decode("utf-8")
            assert "<dc:title>呼兰河传</dc:title>" in opf
            body = "\n".join(
                zf.read(n).decode("utf-8") for n in zf.namelist() if n.endswith(".xhtml")
            )
            assert ANCHOR in body
        with zipfile.ZipFile(root / "books" / "walden.epub") as zf:
            assert "Walden" in zf.read("OEBPS/content.opf").decode("utf-8")
        with zipfile.ZipFile(root / "books" / "yecao.epub") as zf:
            assert "<dc:title>野草</dc:title>" in zf.read("OEBPS/content.opf").decode("utf-8")
        titles = []
        for path in books:
            with zipfile.ZipFile(path) as zf:
                opf_name = next(n for n in zf.namelist() if n.endswith(".opf"))
                opf = zf.read(opf_name).decode("utf-8", "replace")
                title = opf.split("<dc:title>", 1)[1].split("</dc:title>", 1)[0]
                titles.append(title)
        assert "背影" not in titles and "红楼梦" not in titles

        lib = root / "library"
        book = lib / "books" / "0123456789abcdef0123456789abcdef"
        (book / "s").mkdir(parents=True)
        sentences = [
            "太阳在园子里是特大的。",
            "花开了，就像花睡醒了似的。",
        ]
        (book / "s" / "0.json").write_text(json.dumps(sentences, ensure_ascii=False), encoding="utf-8")
        (book / "s" / "1.json").write_text(
            json.dumps(["鸟飞了，就像鸟上天了似的。"], ensure_ascii=False),
            encoding="utf-8",
        )
        (book / "book.json").write_text(json.dumps({"title": "呼兰河传", "authors": ["萧红"]}), encoding="utf-8")
        db = lib / "library.db"
        conn = sqlite3.connect(db)
        conn.executescript(SCHEMA)
        conn.execute("INSERT INTO meta (key, value) VALUES ('device', '0011223344556677')")
        conn.execute("INSERT INTO meta (key, value) VALUES ('hlc', '0000000000001.0000.0011223344556677')")
        conn.commit()
        conn.close()
        apply_plan(lib)
        check = sqlite3.connect(db)
        status = check.execute(
            "SELECT value FROM registers WHERE entity=? AND field='status'",
            ("book/0123456789abcdef0123456789abcdef",),
        ).fetchone()
        assert status and "reading" in status[0], status
        marks = check.execute("SELECT count(*) FROM registers WHERE entity LIKE 'mark/%' AND field='text'").fetchone()[0]
        assert marks == 1, marks
        note = check.execute("SELECT value FROM registers WHERE entity LIKE 'mark/%' AND field='note'").fetchone()
        assert note and NOTE_TEXT in note[0], note
        focus(lib, "listen")
        pos = json.loads((lib / "position.json").read_text(encoding="utf-8"))
        assert pos["listen"]["sentence"] == len(sentences) - 1
        assert ANCHOR in pos["same"]["text"]
        assert ANCHOR in pos["listen"]["text"]
        assert pos["note"] == NOTE_TEXT
        write_windows_prefs(lib, pos["bookId"], 1320, 900)
        prefs = json.loads((lib / "windows-prefs.json").read_text(encoding="utf-8"))
        assert prefs["ThemeMode"] == "light"
        assert prefs["LastOpenTabs"][0]["Kind"] == "reader"
    print("self-check 通过")


def main() -> None:
    parser = argparse.ArgumentParser(description="语阅 README 截图与演示书库")
    sub = parser.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("books")
    p.add_argument("--out", required=True)
    p.set_defaults(func=cmd_books)
    p = sub.add_parser("seed")
    p.add_argument("--library", required=True)
    p.set_defaults(func=cmd_seed)
    p = sub.add_parser("focus")
    p.add_argument("--library", required=True)
    p.add_argument("--mode", choices=("shelf", "listen", "same", "notes"), required=True)
    p.set_defaults(func=cmd_focus)
    p = sub.add_parser("windows-prefs")
    p.add_argument("--library", required=True)
    p.add_argument("--open-book", default=None)
    p.add_argument("--width", type=int, default=1320)
    p.add_argument("--height", type=int, default=900)
    p.set_defaults(func=cmd_prefs)
    p = sub.add_parser("covers")
    p.add_argument("--out", default="build/readme-shots/covers")
    p.set_defaults(func=cmd_covers)
    p = sub.add_parser("compose")
    p.set_defaults(func=cmd_compose)
    p = sub.add_parser("self-check")
    p.set_defaults(func=cmd_self_check)
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
