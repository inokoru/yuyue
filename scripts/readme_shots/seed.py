"""Patch a Yuyue library.db the same way the core writes registers.

The app must be closed. This never opens the real library: callers pass the
scratch directory they just created with --library / -debugLibrary.
"""

from __future__ import annotations

import json
import sqlite3
import time
from pathlib import Path

# title substring, status, shelf fraction (None keeps the book as 想读 with no progress)
PLAN = [
    ("呼兰河传", "reading", 0.28),
    ("Walden", "reading", 0.24),
    ("Gatsby", "reading", 0.41),
    ("朝花夕拾", "done", 1.0),
    ("人间词话", "done", 1.0),
    ("野草", "done", 1.0),
]

# The Chinese reading demo is one sentence in 呼兰河传, 祖父的园子.
DEMO_TITLE = "呼兰河传"
ANCHOR = "花开了，就像花睡醒了似的"
NOTE_TEXT = "花是自己醒的。"
MARKS = [
    (ANCHOR, "yellow", NOTE_TEXT),
]
SAME_PHRASE = ANCHOR


def _dumps(value) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _parse_hlc(hlc: str) -> tuple[int, int]:
    parts = hlc.split(".")
    if len(parts) < 2:
        return 0, 0
    return int(parts[0], 16), int(parts[1], 16)


def _tick(last: tuple[int, int], now: int) -> tuple[int, int]:
    ms, counter = last
    if now > ms:
        return now, 0
    if counter < 0xFFFF:
        return ms, counter + 1
    return ms + 1, 0


class Store:
    def __init__(self, db: Path):
        self.conn = sqlite3.connect(db)
        self.conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        device = self.conn.execute("SELECT value FROM meta WHERE key='device'").fetchone()
        if not device:
            raise SystemExit(f"{db} 里没有 device。先让应用用这个书库启动过一次。")
        self.device = device[0]
        row = self.conn.execute("SELECT value FROM meta WHERE key='hlc'").fetchone()
        self.last = _parse_hlc(row[0]) if row else (0, 0)
        seq = self.conn.execute("SELECT seq FROM clock WHERE device=?", (self.device,)).fetchone()
        self.seq = int(seq[0]) if seq else 0

    def put(self, entity: str, field: str, value) -> None:
        now = int(time.time() * 1000)
        self.seq += 1
        self.last = _tick(self.last, now)
        hlc = f"{self.last[0]:013x}.{self.last[1]:04x}.{self.device}"
        payload = _dumps(value)
        self.conn.execute(
            "INSERT INTO clock (device, seq) VALUES (?, ?) ON CONFLICT(device) DO UPDATE SET seq = max(seq, excluded.seq)",
            (self.device, self.seq),
        )
        cur = self.conn.execute(
            "INSERT INTO registers (entity, field, value, hlc) VALUES (?, ?, ?, ?) "
            "ON CONFLICT(entity, field) DO UPDATE SET value = excluded.value, hlc = excluded.hlc "
            "WHERE excluded.hlc > registers.hlc",
            (entity, field, payload, hlc),
        )
        if cur.rowcount:
            self.conn.execute("DELETE FROM ops WHERE entity=? AND field=?", (entity, field))
            self.conn.execute(
                "INSERT OR REPLACE INTO ops (device, seq, hlc, entity, field, value) VALUES (?, ?, ?, ?, ?, ?)",
                (self.device, self.seq, hlc, entity, field, payload),
            )
        self.conn.execute("DELETE FROM conflicts WHERE entity=? AND field=?", (entity, field))
        self.conn.execute(
            "INSERT OR REPLACE INTO meta (key, value) VALUES ('hlc', ?)",
            (hlc,),
        )

    def commit(self) -> None:
        self.conn.commit()
        self.conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        self.conn.commit()


def books_in(library: Path) -> list[tuple[str, str, Path]]:
    root = library / "books"
    found = []
    if not root.is_dir():
        return found
    for book_json in sorted(root.glob("*/book.json")):
        try:
            data = json.loads(book_json.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            continue
        title = str(data.get("title") or "")
        found.append((book_json.parent.name, title, book_json.parent))
    return found


def _match(title: str, needle: str) -> bool:
    return needle.lower() in title.lower()


def _sentences(book_dir: Path, section: int) -> list[str]:
    path = book_dir / "s" / f"{section}.json"
    if not path.is_file():
        return []
    data = json.loads(path.read_text(encoding="utf-8"))
    return [s for s in data if isinstance(s, str)]


def _find_sentence(book_dir: Path, phrase: str) -> tuple[int, int, str] | None:
    sdir = book_dir / "s"
    if not sdir.is_dir():
        return None
    files = sorted(sdir.glob("*.json"), key=lambda p: int(p.stem) if p.stem.isdigit() else 0)
    for path in files:
        if not path.stem.isdigit():
            continue
        section = int(path.stem)
        for i, text in enumerate(_sentences(book_dir, section)):
            if phrase in text:
                return section, i, text
    return None


def apply_plan(library: Path) -> dict:
    db = library / "library.db"
    if not db.is_file():
        raise SystemExit(f"找不到 {db}")
    store = Store(db)
    now = int(time.time() * 1000)
    catalog = books_in(library)
    if not catalog:
        raise SystemExit(f"{library}/books 里没有书。导入还没完成。")

    demo = None
    reading_rank = 0
    for book_id, title, book_dir in catalog:
        status, fraction = "want", None
        for needle, st, frac in PLAN:
            if _match(title, needle):
                status, fraction = st, frac
                break
        entity = f"book/{book_id}"
        store.put(entity, "status", status)
        store.put(entity, "statusAt", now)
        store.put(entity, "finished", status == "done")
        if status == "done":
            store.put(entity, "finishedAt", now - 86_400_000)
            store.put(entity, "startedAt", now - 86_400_000 * 14)
        elif status == "reading":
            reading_rank += 1
            opened = now - reading_rank * 60_000
            store.put(entity, "startedAt", opened - 86_400_000)
            store.put(entity, "openedAt", opened)
            store.put(entity, "finishedAt", None)
            sent = 4 if fraction and fraction < 1 else 0
            store.put(
                f"progress/{book_id}/{store.device}",
                "position",
                {"section": 0, "sentence": sent, "fraction": fraction or 0.02, "at": opened},
            )
        else:
            store.put(entity, "finishedAt", None)
        if _match(title, DEMO_TITLE):
            demo = (book_id, book_dir)

    position = {"bookId": None, "title": DEMO_TITLE, "same": None, "listen": None, "note": NOTE_TEXT}
    if demo:
        book_id, book_dir = demo
        position["bookId"] = book_id
        found = _find_sentence(book_dir, SAME_PHRASE)
        if found:
            section, idx, text = found
            spot = {"section": section, "sentence": idx, "text": text}
            # This sentence is the last one in its section, so listening it turns the page.
            position["same"] = spot
            position["listen"] = spot
        for phrase, style, note in MARKS:
            hit = _find_sentence(book_dir, phrase)
            if not hit:
                continue
            section, idx, text = hit
            # Mark the whole sentence so the offsets match what the page shows.
            mark_id = f"{section:04x}{idx:012x}"
            fields = {
                "section": section,
                "start": {"s": idx, "o": 0},
                "end": {"s": idx, "o": len(text)},
                "text": text,
                "style": style,
                "note": note,
                "at": now,
            }
            entity = f"mark/{book_id}/{mark_id}"
            for field, value in fields.items():
                store.put(entity, field, value)

    store.commit()
    out = library / "position.json"
    out.write_text(json.dumps(position, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"书库已写入 {len(catalog)} 本的状态 → {db}")
    if position["same"]:
        print(f"同一位置：第 {position['same']['section']} 节第 {position['same']['sentence']} 句 「{position['same']['text'][:24]}」")
    if position["listen"]:
        print(f"听书翻页从第 {position['listen']['sentence']} 句起（这一节的最后一句）")
    return position


def focus(library: Path, mode: str) -> dict:
    """Move 呼兰河传's progress onto the garden sentence, or the listen spot."""
    meta = json.loads((library / "position.json").read_text(encoding="utf-8"))
    book_id = meta.get("bookId")
    if not book_id:
        raise SystemExit("position.json 里没有呼兰河传。先跑 seed。")
    store = Store(library / "library.db")
    now = int(time.time() * 1000)
    if mode == "listen":
        spot = meta.get("listen") or {"section": 0, "sentence": 0}
        fraction = 0.9
    elif mode == "same":
        spot = meta.get("same") or {"section": 0, "sentence": 0}
        fraction = 0.45
    else:
        spot = {"section": 0, "sentence": 1}
        fraction = 0.22
    store.put(
        f"progress/{book_id}/{store.device}",
        "position",
        {
            "section": int(spot["section"]),
            "sentence": int(spot["sentence"]),
            "fraction": fraction,
            "at": now,
        },
    )
    store.put(f"book/{book_id}", "openedAt", now)
    store.commit()
    meta["focus"] = mode
    (library / "position.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"呼兰河传进度 → {mode} 第 {spot['section']} 节第 {spot['sentence']} 句")
    return meta


def write_windows_prefs(library: Path, open_book: str | None, width: int, height: int) -> None:
    path = library / "windows-prefs.json"
    prefs = {}
    if path.is_file():
        try:
            prefs = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            prefs = {}
    prefs.update(
        {
            "ThemeMode": "light",
            "UiLanguage": "zh-Hans",
            "UseWarmMica": False,
            "MainWindowMaximized": False,
            "MainWindowWidth": width,
            "MainWindowHeight": height,
            "ShelfViewMode": "grid",
            "CoverWidth": 108,
            "ReopenLast": False,
            "RestoreLastTabs": bool(open_book),
            "ReaderSpaceAction": "listen",
            "TwoColumns": True,
            "ShelfSingleClickOpen": False,
        }
    )
    if open_book:
        prefs["LastOpenTabs"] = [{"BookId": open_book, "Kind": "reader"}]
    else:
        prefs["LastOpenTabs"] = []
    path.write_text(json.dumps(prefs, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"windows-prefs.json 已写好（浅色，{width}×{height}）")
