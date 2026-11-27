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
    from readme_shots.books import all_covers
    from readme_shots.covers import contact_sheet, render

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    specs = all_covers()
    for spec in specs:
        (out / f"{spec.title}.png").write_bytes(render(spec))
    sheet = contact_sheet(specs, out / "sheet.png")
    print(f"{len(specs)} 张封面 → {sheet}")


def cmd_self_check(_: argparse.Namespace) -> None:
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        books = prepare(root / "books")
        names = {p.name for p in books}
        assert len(books) == 22, len(books)
        assert "alice.epub" not in names and "ailianshuo.epub" not in names
        assert "guwen.epub" in names and "shijing.epub" in names
        from readme_shots.books import FIXTURES, same_file
        assert same_file(root / "books" / "great-gatsby.epub", FIXTURES / "great-gatsby.epub")
        assert same_file(root / "books" / "pride-and-prejudice.epub", FIXTURES / "pride-and-prejudice.epub")
        import zipfile
        with zipfile.ZipFile(root / "books" / "hongloumeng.epub") as zf:
            opf = zf.read("OEBPS/content.opf").decode("utf-8")
            assert "<dc:title>红楼梦</dc:title>" in opf
            assert "曹雪芹" in opf
            assert "Xueqin Cao" not in opf
            assert "紅樓夢" not in opf
            cover_name = next(n for n in zf.namelist() if n.endswith("24264-cover.png"))
            assert len(zf.read(cover_name)) != 49495
        epub = next(p for p in books if p.name == "beiying.epub")
        with zipfile.ZipFile(epub) as zf:
            assert zf.namelist()[0] == "mimetype", zf.namelist()[:3]
            assert zf.getinfo("mimetype").compress_type == zipfile.ZIP_STORED
            assert zf.read("mimetype") == b"application/epub+zip"
            assert "OEBPS/cover.png" in zf.namelist()
            assert zf.read("OEBPS/content.opf").decode().count("<itemref") == 2

        lib = root / "library"
        book = lib / "books" / "0123456789abcdef0123456789abcdef"
        (book / "s").mkdir(parents=True)
        sentences = [
            "我与父亲不相见已二年余了。",
            "那年冬天，祖母死了，父亲的差使也交卸了。",
            "父亲说，事已如此，不必难过，好在天无绝人之路。",
            "我看见他戴着黑布小帽，穿着黑布大马褂。",
            "这时我看见他的背影，我的泪很快地流下来了。",
            "最后一句，用来翻到下一节。",
        ]
        (book / "s" / "0.json").write_text(json.dumps(sentences, ensure_ascii=False), encoding="utf-8")
        (book / "book.json").write_text(json.dumps({"title": "背影", "authors": ["朱自清"]}), encoding="utf-8")
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
        assert marks >= 2, marks
        focus(lib, "listen")
        pos = json.loads((lib / "position.json").read_text(encoding="utf-8"))
        assert pos["listen"]["sentence"] == len(sentences) - 1
        assert "黑布小帽" in pos["same"]["text"]
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
