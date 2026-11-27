"""Download the public-domain texts for the README shelf.

Chinese pages come from Wikisource and are converted to simplified Chinese.
English books come from a Project Gutenberg mirror. The result is written to
fixtures/readme-shelf/*.json so later captures do not need the network.

This does not invent missing pages. 《志摩的诗》 is the set Wikisource files
under that collection.
"""

from __future__ import annotations

import json
import re
import urllib.parse
import urllib.request
from pathlib import Path

import zhconv

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "fixtures" / "readme-shelf"
WIKI = "https://zh.wikisource.org/w/api.php"
MIRROR = "https://mirror.csclub.uwaterloo.ca/gutenberg"
UA = "yuyue-readme/1.0 (inokoru/yuyue; public-domain shelf)"

ANCHOR = "花开了，就像花睡醒了似的。"

ZHIMO = [
    "滬杭車中",
    "破廟",
    "月下雷峰影片",
    "一塊晦色的路碑",
    "希望的埋葬",
    "哀曼殊斐兒",
    "一個祈禱",
    "難得",
    "一星弱火",
    "落葉小唱",
    "殘詩",
    "這是一個懦怯的世界",
    "天國的消息",
    "留別日本",
    "沙揚娜拉",
    "我有一個戀愛",
    "為誰",
    "雪花的快樂",
]

SHISHUO = [
    "德行", "言語", "政事", "文學", "方正", "雅量", "豪爽", "識鑒", "賞譽", "品藻",
    "規箴", "捷悟", "夙惠", "容止", "自新", "企羡", "傷逝", "棲逸", "賢媛", "術解",
    "巧蓺", "寵禮", "任誕", "簡傲", "排調", "輕詆", "假譎", "黜免", "儉嗇", "汰侈",
    "忿狷", "讒險", "尤悔", "紕漏", "惑溺", "仇隟",
]

HULAN = [f"呼蘭河傳/{n}" for n in ["第一章", "第二章", "第三章", "第四章", "第五章", "第六章", "第七章", "尾聲"]]

FUSHENG = [
    "浮生六記/卷一 閨房記樂",
    "浮生六記/卷二 閑情記趣",
    "浮生六記/卷三 坎坷記愁",
    "浮生六記/卷四 浪遊記快",
    "浮生六記/卷五 中山記歷",
    "浮生六記/卷六 養生記道",
]

ZHAOHUA = [
    "朝花夕拾/小引",
    "狗·猫·鼠",
    "阿長與《山海經》",
    "二十四孝圖",
    "五猖會",
    "无常",
    "從百草園到三味書屋",
    "父亲的病",
    "琐记",
    "藤野先生",
    "范愛農",
    "朝花夕拾/後記",
]

YECAO = [
    "秋夜 (魯迅)",
    "影的告别",
    "求乞者",
    "我的失戀",
    "復讎",
    "復讎（其二）",
    "希望",
    "雪 (鲁迅)",
    "風筝",
    "好的故事",
    "過客",
    "死火",
    "狗的駁詰",
    "失掉的好地獄",
    "墓碣文",
    "頹敗線的顫動",
    "立論",
    "死後",
    "這樣的戰士",
    "聰明人和儍子和奴才",
    "臘葉",
    "淡淡的血痕中",
    "一覺",
]


def _get(url: str) -> bytes:
    import time
    import urllib.error

    delay = 2.0
    for attempt in range(6):
        req = urllib.request.Request(url, headers={"User-Agent": UA})
        try:
            with urllib.request.urlopen(req, timeout=90) as resp:
                return resp.read()
        except urllib.error.HTTPError as err:
            if err.code != 429 or attempt == 5:
                raise
            time.sleep(delay)
            delay *= 2
    raise SystemExit(f"下载失败：{url}")


def wiki_html(title: str, api: str = WIKI) -> str:
    q = urllib.parse.urlencode(
        {
            "action": "parse",
            "page": title,
            "prop": "text",
            "format": "json",
            "disableeditsection": "1",
            "redirects": "1",
        }
    )
    data = json.loads(_get(f"{api}?{q}"))
    if "error" in data:
        raise SystemExit(f"维基文库没有这一页：{title} ({data['error'].get('code')})")
    return data["parse"]["text"]["*"]


def _chrome(text: str) -> bool:
    t = text.strip()
    if len(t) < 2:
        return True
    bad = (
        "姊妹计划",
        "姊妹計劃",
        "数据项",
        "數據項",
        "本作品收錄",
        "本作品收录",
        "Public domain",
        "公有领域",
        "公有領域",
        "维基文库",
        "維基文庫",
        "检索自",
        "檢索自",
        "Creative Commons",
        "编辑章节",
        "跳转到",
    )
    if any(k in t for k in bad):
        return True
    if t in {"目录", "目錄", "编辑", "編輯"}:
        return True
    if t.startswith("作者") or t.startswith("←") or t.startswith("▶") or t.startswith("◀"):
        return True
    return False


def _clean(text: str) -> str:
    text = text.replace("\u200b", "").replace("\xa0", " ")
    text = re.sub(r"[ \t]+\n", "\n", text)
    text = re.sub(r"\n{3,}", "\n\n", text)
    text = re.sub(r"\[\d+\]", "", text)
    return text.strip()


def _is_poem(tag) -> bool:
    classes = " ".join(tag.get("class") or [])
    return "poem" in classes or tag.name == "poem"


def _blocks(html: str) -> list[tuple[str, str]]:
    from bs4 import BeautifulSoup

    soup = BeautifulSoup(html, "html.parser")
    for sel in (
        "script",
        "style",
        "sup.reference",
        ".mw-editsection",
        ".toc",
        ".mw-references-wrap",
        ".reflist",
        ".reference",
        ".sistersitebox",
        ".navbox",
        ".catlinks",
        ".mw-authority-control",
        ".licenseContainer",
        ".mbox-image",
        ".noprint",
        ".mw-empty-elt",
        ".shortdescription",
        ".hatnote",
        "table",
    ):
        for tag in soup.select(sel):
            tag.decompose()
    for br in soup.find_all("br"):
        br.replace_with("\n")
    root = soup.select_one(".mw-parser-output") or soup
    out: list[tuple[str, str]] = []

    def walk(node) -> None:
        for child in list(getattr(node, "children", [])):
            name = getattr(child, "name", None)
            if not name:
                continue
            if name in {"h2", "h3"}:
                text = _clean(child.get_text(" ", strip=True))
                if text and not _chrome(text):
                    out.append(("h", text))
            elif name == "p" or name == "li":
                text = _clean(child.get_text("\n", strip=True))
                if text and not _chrome(text):
                    out.append(("p", text))
            elif name in {"ol", "ul"}:
                walk(child)
            elif _is_poem(child):
                text = _clean(child.get_text("\n", strip=True))
                if text and not _chrome(text):
                    out.append(("poem", text))
            elif name in {"div", "section", "blockquote", "center"}:
                walk(child)

    walk(root)
    return out


def _sections(title: str, blocks: list[tuple[str, str]]) -> list[dict]:
    short = title.split("/")[-1]
    short = re.sub(r"\s*\([^)]*\)$", "", short)
    sections: list[dict] = []
    heading = short
    paras: list[str] = []

    def flush() -> None:
        nonlocal paras, heading
        body = [p for p in paras if p.strip()]
        if body:
            sections.append({"heading": heading, "paragraphs": body})
        paras = []

    for kind, text in blocks:
        if kind == "h":
            if text in {short, title}:
                continue
            flush()
            heading = text
            continue
        if kind == "poem":
            stanzas = [s.strip() for s in re.split(r"\n\s*\n", text) if s.strip()]
            paras.extend(stanzas or [text])
        else:
            paras.append(text)
    flush()
    if not sections:
        raise SystemExit(f"这一页没有抽出正文：{title}")
    return sections


def s2t_sections(sections: list[dict]) -> list[dict]:
    out = []
    for sec in sections:
        out.append(
            {
                "heading": zhconv.convert(sec["heading"], "zh-cn"),
                "paragraphs": [zhconv.convert(p, "zh-cn") for p in sec["paragraphs"]],
            }
        )
    return out


def _fix_dropcap(section: dict) -> dict:
    paras = section["paragraphs"]
    out: list[str] = []
    i = 0
    while i < len(paras):
        para = paras[i]
        if len(para) == 1 and para.isalpha() and i + 1 < len(paras):
            nxt = paras[i + 1]
            joined = para + nxt
            space = joined.find(" ")
            if space > 1:
                joined = joined[:space].capitalize() + joined[space:]
            out.append(joined)
            i += 2
            continue
        out.append(para)
        i += 1
    return {"heading": section["heading"], "paragraphs": out}


def fetch_pages(titles: list[str]) -> list[dict]:
    return fetch_pages_host(WIKI, titles, simplify=True)


def fetch_pages_host(api: str, titles: list[str], simplify: bool) -> list[dict]:
    import time

    sections: list[dict] = []
    for title in titles:
        time.sleep(0.4)
        sections.extend(_sections(title, _blocks(wiki_html(title, api))))
    return s2t_sections(sections) if simplify else sections


def split_garden(sections: list[dict]) -> list[dict]:
    """End a section on the garden sentence so listening that sentence turns the page."""
    out: list[dict] = []
    found = False
    for sec in sections:
        if found:
            out.append(sec)
            continue
        hit = None
        for i, para in enumerate(sec["paragraphs"]):
            if ANCHOR in para:
                hit = i
                break
        if hit is None:
            out.append(sec)
            continue
        found = True
        para = sec["paragraphs"][hit]
        end = para.find(ANCHOR) + len(ANCHOR)
        if end < len(para) and para[end] == "。":
            end += 1
        before_paras = sec["paragraphs"][:hit]
        sentence = para[:end].strip()
        # Keep the sentence as the last paragraph of its own short section.
        lead = before_paras[-1:] if before_paras else []
        earlier = before_paras[:-1]
        if earlier:
            out.append({"heading": sec["heading"], "paragraphs": earlier})
        garden = [p for p in [*lead, sentence] if p]
        out.append({"heading": "祖父的园子", "paragraphs": garden})
        rest = para[end:].strip()
        tail = ([rest] if rest else []) + sec["paragraphs"][hit + 1 :]
        if tail:
            out.append({"heading": "园子", "paragraphs": tail})
    if not found:
        raise SystemExit("呼兰河传里没有找到「花开了，就像花睡醒了似的」。")
    return out


def _have(name: str) -> bool:
    path = OUT / name
    return path.is_file() and path.stat().st_size > 1000


def write_book(name: str, book: dict) -> None:
    path = OUT / name
    path.write_text(json.dumps(book, ensure_ascii=False, indent=1), encoding="utf-8")
    chars = sum(len(p) for s in book["sections"] for p in s["paragraphs"])
    print(f"{name}: {len(book['sections'])} 节, {chars} 字")


def gutenberg(ebook: int, path: str) -> str:
    raw = _get(f"{MIRROR}/{path}")
    for enc in ("utf-8-sig", "utf-8", "latin-1"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    raise SystemExit(f"Gutenberg {ebook} 编码读不出来")


def strip_pg(text: str) -> str:
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    start = re.search(r"\*\*\*\s*START OF (?:THE |THIS )?PROJECT GUTENBERG EBOOK.*?\*\*\*", text)
    end = re.search(r"\*\*\*\s*END OF (?:THE |THIS )?PROJECT GUTENBERG EBOOK.*?\*\*\*", text)
    if not start or not end:
        raise SystemExit("找不到 Gutenberg 正文起止标记")
    return text[start.end() : end.start()].strip()


_CHAPTER = re.compile(
    r"^(?:CHAPTER|Chapter)\s+[0-9IVXLC]+(?:\.\s+.*)?$"
    r"|^(?:PART|Part)\s+[0-9IVXLC]+(?:\.\s+.*)?$"
    r"|^(?:BOOK|Book)\s+[0-9IVXLC]+(?:\.\s+.*)?$"
    r"|^(?:THE )?(?:FIRST|SECOND|THIRD|FOURTH|FIFTH|SIXTH|SEVENTH|EIGHTH|NINTH|TENTH|ELEVENTH|TWELFTH) BOOK$"
    r"|^On [A-Z][A-Za-z'’ \-]{2,40}$"
)


def _split_exact(text: str, titles: set[str], unindented: bool = False) -> list[dict]:
    """Split on full lines that match `titles`. The first hit of each title wins."""
    lines = text.split("\n")
    sections: list[dict] = []
    heading = ""
    buf: list[str] = []
    seen: set[str] = set()

    def flush() -> None:
        nonlocal buf
        paras = [p.strip() for p in re.split(r"\n\s*\n", "\n".join(buf)) if p.strip()]
        if paras and heading:
            sections.append({"heading": heading, "paragraphs": paras})
        buf = []

    for line in lines:
        key = line.strip()
        if unindented and line[:1].isspace():
            buf.append(line)
            continue
        if key in titles and key not in seen:
            seen.add(key)
            flush()
            heading = key.title() if key.isupper() else key
            continue
        buf.append(line)
    flush()
    if not sections:
        raise SystemExit("没有切出章节")
    return sections


def english_sections(text: str) -> list[dict]:
    lines = text.split("\n")
    sections: list[dict] = []
    heading = "Text"
    buf: list[str] = []

    def flush() -> None:
        nonlocal buf
        paras = [p.strip() for p in re.split(r"\n\s*\n", "\n".join(buf)) if p.strip()]
        paras = [p for p in paras if p.lower() not in {"[illustration]", "[illustration:]"}]
        if paras:
            sections.append({"heading": heading.strip(), "paragraphs": paras})
        buf = []

    for line in lines:
        if _CHAPTER.match(line.strip()):
            flush()
            heading = line.strip()
            continue
        buf.append(line)
    flush()
    if not sections:
        raise SystemExit("英文正文是空的")
    return sections


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)

    if not _have("yecao.json"):
        write_book(
            "yecao.json",
            {
                "title": "野草",
                "author": "鲁迅",
                "lang": "zh-CN",
                "source": "https://zh.wikisource.org/wiki/野艸",
                "sections": fetch_pages(YECAO),
            },
        )
    else:
        print("yecao.json: 已有，跳过")
    if not _have("zhaohua.json"):
        write_book(
            "zhaohua.json",
            {
                "title": "朝花夕拾",
                "author": "鲁迅",
                "lang": "zh-CN",
                "source": "https://zh.wikisource.org/wiki/朝花夕拾",
                "sections": fetch_pages(ZHAOHUA),
            },
        )
    else:
        print("zhaohua.json: 已有，跳过")
    if not _have("hulan.json"):
        write_book(
            "hulan.json",
            {
                "title": "呼兰河传",
                "author": "萧红",
                "lang": "zh-CN",
                "source": "https://zh.wikisource.org/wiki/呼蘭河傳",
                "sections": split_garden(fetch_pages(HULAN)),
            },
        )
    else:
        print("hulan.json: 已有，跳过")
    if not _have("fusheng.json"):
        write_book(
            "fusheng.json",
            {
                "title": "浮生六记",
                "author": "沈复",
                "lang": "zh-CN",
                "source": "https://zh.wikisource.org/wiki/浮生六記",
                "sections": fetch_pages(FUSHENG),
            },
        )
    else:
        print("fusheng.json: 已有，跳过")
    if not _have("renjian.json"):
        write_book(
            "renjian.json",
            {
                "title": "人间词话",
                "author": "王国维",
                "lang": "zh-CN",
                "source": "https://zh.wikisource.org/wiki/人間詞話",
                "sections": fetch_pages(["人間詞話"]),
            },
        )
    else:
        print("renjian.json: 已有，跳过")
    if not _have("zhimo.json"):
        write_book(
            "zhimo.json",
            {
                "title": "志摩的诗",
                "author": "徐志摩",
                "lang": "zh-CN",
                "source": "https://zh.wikisource.org/wiki/Author:徐志摩",
                "sections": fetch_pages(ZHIMO),
            },
        )
    else:
        print("zhimo.json: 已有，跳过")
    if not _have("shishuo.json"):
        write_book(
            "shishuo.json",
            {
                "title": "世说新语",
                "author": "刘义庆",
                "lang": "zh-CN",
                "source": "https://zh.wikisource.org/wiki/世說新語",
                "sections": fetch_pages([f"世說新語/{name}" for name in SHISHUO]),
            },
        )
    else:
        print("shishuo.json: 已有，跳过")

    english = [
        (
            "dalloway.json",
            71865,
            "7/1/8/6/71865/71865-0.txt",
            "Mrs Dalloway",
            "Virginia Woolf",
            "https://www.gutenberg.org/ebooks/71865",
            None,
        ),
        (
            "walden.json",
            205,
            "2/0/205/205-0.txt",
            "Walden",
            "Henry David Thoreau",
            "https://www.gutenberg.org/ebooks/205",
            "ON THE DUTY OF CIVIL DISOBEDIENCE",
        ),
        (
            "meditations.json",
            2680,
            "2/6/8/2680/2680-0.txt",
            "Meditations",
            "Marcus Aurelius",
            "https://www.gutenberg.org/ebooks/2680",
            None,
        ),
        (
            "dubliners.json",
            2814,
            "2/8/1/2814/2814-0.txt",
            "Dubliners",
            "James Joyce",
            "https://www.gutenberg.org/ebooks/2814",
            None,
        ),
        (
            "prophet.json",
            58585,
            "5/8/5/8/58585/58585-0.txt",
            "The Prophet",
            "Kahlil Gibran",
            "https://www.gutenberg.org/ebooks/58585",
            None,
        ),
    ]
    for name, ebook, path, title, author, source, cut in english:
        if name == "meditations.json":
            continue
        if _have(name):
            print(f"{name}: 已有，跳过")
            continue
        text = strip_pg(gutenberg(ebook, path))
        if cut:
            idx = text.rfind(cut)
            if idx > len(text) // 2:
                text = text[:idx].strip()
        if ebook == 205:
            sections = _split_exact(
                text,
                {
                    "Economy",
                    "Where I Lived, and What I Lived For",
                    "Reading",
                    "Sounds",
                    "Solitude",
                    "Visitors",
                    "The Bean-Field",
                    "The Village",
                    "The Ponds",
                    "Baker Farm",
                    "Higher Laws",
                    "Brute Neighbors",
                    "House-Warming",
                    "Former Inhabitants and Winter Visitors",
                    "Winter Animals",
                    "The Pond in Winter",
                    "Spring",
                    "Conclusion",
                },
                unindented=True,
            )
        elif ebook == 2814:
            sections = _split_exact(
                text,
                {
                    "THE SISTERS",
                    "AN ENCOUNTER",
                    "ARABY",
                    "EVELINE",
                    "AFTER THE RACE",
                    "TWO GALLANTS",
                    "THE BOARDING HOUSE",
                    "A LITTLE CLOUD",
                    "COUNTERPARTS",
                    "CLAY",
                    "A PAINFUL CASE",
                    "IVY DAY IN THE COMMITTEE ROOM",
                    "A MOTHER",
                    "GRACE",
                    "THE DEAD",
                },
            )
        elif ebook == 71865:
            marker = "Mrs. Dalloway said she would buy the flowers herself."
            at = text.find(marker)
            if at < 0:
                raise SystemExit("Mrs Dalloway 正文没找到")
            sections = [{"heading": "Mrs Dalloway", "paragraphs": [p.strip() for p in re.split(r"\n\s*\n", text[at:]) if p.strip()]}]
        elif ebook == 58585:
            marker = "Almustafa, the chosen"
            at = text.find(marker)
            if at < 0:
                raise SystemExit("The Prophet 正文没找到")
            sections = [{"heading": "The Prophet", "paragraphs": [p.strip() for p in re.split(r"\n\s*\n", text[at:]) if p.strip()]}]
        else:
            sections = english_sections(text)
        write_book(
            name,
            {
                "title": title,
                "author": author,
                "lang": "en",
                "source": source,
                "translator": "",
                "sections": sections,
            },
        )

    if not _have("meditations.json"):
        books = [f"The Thoughts of the Emperor Marcus Aurelius Antoninus/Book {n}" for n in ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII"]]
        sections = fetch_pages_host("https://en.wikisource.org/w/api.php", books, simplify=False)
        sections = [_fix_dropcap(s) for s in sections]
        blob = "\n".join(p for s in sections for p in s["paragraphs"])
        if "grandfather Verus" not in blob or "good morals" not in blob:
            raise SystemExit("Meditations 不是 George Long 的译文")
        write_book(
            "meditations.json",
            {
                "title": "Meditations",
                "author": "Marcus Aurelius",
                "lang": "en",
                "source": "https://en.wikisource.org/wiki/The_Thoughts_of_the_Emperor_Marcus_Aurelius_Antoninus",
                "translator": "George Long",
                "sections": sections,
            },
        )


if __name__ == "__main__":
    main()
