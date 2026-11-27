"""Public-domain demo EPUBs for README screenshots.

The shelf needs more books than the four fixtures. Texts here are either copied
from fixtures/books (背影) or short, well-known public-domain passages. Nothing
under copyright is fetched or invented as a fake classic.
"""

from __future__ import annotations

import io
import os
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "fixtures" / "books"

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
.author { text-align: center; text-indent: 0; }
"""

# Muted papers. A few dark covers so the grid has rhythm; no cinnabar/ink set.
PALETTE = [
    ("#F3E6D4", "#3E342C"),
    ("#1E3A34", "#F3EDE4"),
    ("#243044", "#F4EFE6"),
    ("#E7D3C4", "#3A2E28"),
    ("#E4E0D4", "#2C2B28"),
    ("#3E2C2A", "#F6EFE6"),
    ("#D9E0D6", "#243028"),
    ("#C4B4A0", "#2A241E"),
    ("#EFE4D4", "#3A332C"),
    ("#2C3330", "#F3EEE6"),
]


def xhtml(title: str, lang: str, body: str) -> str:
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="{lang}" lang="{lang}">\n'
        f"<head><title>{title}</title>\n"
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


def cover_png(title: str, author: str, bg: str, fg: str) -> bytes:
    from PIL import Image, ImageDraw, ImageFont

    w, h = 600, 860
    im = Image.new("RGB", (w, h), bg)
    draw = ImageDraw.Draw(im)
    font = _font(64)
    small = _font(28)
    draw.rectangle([48, 72, w - 48, 76], fill=fg)
    _draw_centered(draw, title, font, fg, w, 180, 520)
    draw.rectangle([48, h - 150, w - 48, h - 146], fill=fg)
    _draw_centered(draw, author, small, fg, w, h - 120, 80)
    buf = io.BytesIO()
    im.save(buf, format="PNG")
    return buf.getvalue()


def _font(size: int):
    from PIL import ImageFont

    for path in _font_candidates():
        try:
            return ImageFont.truetype(path, size=size)
        except OSError:
            continue
    return ImageFont.load_default()


def _font_candidates() -> list[str]:
    env = os.environ.get("YY_SHOT_FONT")
    names = [
        env,
        "/System/Library/Fonts/PingFang.ttc",
        "/System/Library/Fonts/STHeiti Light.ttc",
        "/System/Library/Fonts/Supplemental/Songti.ttc",
        "/Library/Fonts/Arial Unicode.ttf",
        "C:/Windows/Fonts/msyh.ttc",
        "C:/Windows/Fonts/simsun.ttc",
        "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
    ]
    return [p for p in names if p and Path(p).is_file()]


def _draw_centered(draw, text: str, font, fill: str, width: int, top: int, box_h: int) -> None:
    # Wrap on the measured width. Chinese has no spaces, so wrap by character.
    lines: list[str] = []
    current = ""
    for ch in text:
        trial = current + ch
        if draw.textlength(trial, font=font) <= width - 120:
            current = trial
        else:
            if current:
                lines.append(current)
            current = ch
    if current:
        lines.append(current)
    line_h = font.size + 12 if hasattr(font, "size") else 40
    block = line_h * len(lines)
    y = top + max(0, (box_h - block) // 2)
    for line in lines:
        tw = draw.textlength(line, font=font)
        draw.text(((width - tw) / 2, y), line, font=font, fill=fill)
        y += line_h


def build_book(path: Path, ident: str, title: str, author: str, lang: str, sections: list[tuple[str, str]], color: tuple[str, str]) -> None:
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
        paras = "".join(f"<p>{p}</p>\n" for p in paragraphs)
        body = f"<h1>{title}</h1>\n<p class=\"author\">{author}</p>\n<h2>{heading}</h2>\n{paras}" if i == 1 else f"<h2>{heading}</h2>\n{paras}"
        bodies.append((f"OEBPS/{href}", xhtml(heading, lang, body)))
    manifest = "\n".join(
        f'    <item id="{i}" href="{h}" media-type="{m}"'
        + (f' properties="{p}"' if p else "")
        + "/>"
        for i, h, m, p in items
    )
    spine_xml = "\n".join(f'    <itemref idref="{i}"/>' for i in spine)
    opf = f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid" xml:lang="{lang}">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="uid">{ident}</dc:identifier>
    <dc:title>{title}</dc:title>
    <dc:creator>{author}</dc:creator>
    <dc:language>{lang}</dc:language>
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
    nav_items = "\n".join(f'      <li><a href="{h}">{t}</a></li>' for t, h in toc)
    nav = xhtml("目录", lang, f"<nav><h1>目录</h1><ol>\n{nav_items}\n</ol></nav>").replace(
        '<link rel="stylesheet" type="text/css" href="../style.css"/>\n', ""
    )
    points = "\n".join(
        f'    <navPoint id="p{n}" playOrder="{n}"><navLabel><text>{t}</text></navLabel><content src="{h}"/></navPoint>'
        for n, (t, h) in enumerate(toc, 1)
    )
    ncx = f"""<?xml version="1.0" encoding="utf-8"?>
<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
  <head><meta name="dtb:uid" content="{ident}"/></head>
  <docTitle><text>{title}</text></docTitle>
  <navMap>
{points}
  </navMap>
</ncx>
"""
    png = cover_png(title, author, color[0], color[1])
    files: list[tuple[str, bytes | str]] = [
        ("META-INF/container.xml", CONTAINER),
        ("OEBPS/content.opf", opf),
        ("OEBPS/nav.xhtml", nav),
        ("OEBPS/toc.ncx", ncx),
        ("OEBPS/style.css", STYLE),
        ("OEBPS/cover.png", png),
        *bodies,
    ]
    write_epub(path, files)


def beiying_sections() -> list[tuple[str, list[str]]]:
    """背影 from the repo fixture, split so listening can turn onto a second section."""
    text = (FIXTURES / "beiying.txt").read_text(encoding="utf-8")
    lines = [ln.strip() for ln in text.splitlines() if ln.strip()]
    paras = lines[2:]
    # Keep the opening long enough to fill a page; the last paragraphs are section 2.
    if len(paras) < 4:
        return [("背影", paras)]
    return [("背影", paras[:-2]), ("月台", paras[-2:])]


def _p(text: str) -> list[str]:
    return [ln.strip() for ln in text.strip().split("\n") if ln.strip()]


GENERATED = [
    ("beiying.epub", "urn:yuyue:readme:beiying", "背影", "朱自清", "zh-CN", None),
    ("walden.epub", "urn:yuyue:readme:walden", "Walden", "Henry David Thoreau", "en", [
        ("Where I Lived, and What I Lived For", _p("""
When I wrote the following pages, or rather the bulk of them, I lived alone, in the woods, a mile from any neighbor, in a house which I had built myself, on the shore of Walden Pond, in Concord, Massachusetts, and earned my living by the labor of my hands only. I lived there two years and two months. At present I am a sojourner in civilized life again.
I went to the woods because I wished to live deliberately, to front only the essential facts of life, and see if I could not learn what it had to teach, and not, when I came to die, discover that I had not lived. I did not wish to live what was not life, living is so dear; nor did I wish to practise resignation, unless it was quite necessary.
I wanted to live deep and suck out all the marrow of life, to live so sturdily and Spartan-like as to put to rout all that was not life, to cut a broad swath and shave close, to drive life into a corner, and reduce it to its lowest terms.
""")),
    ]),
    ("daodejing.epub", "urn:yuyue:readme:ddj", "道德经", "老子", "zh-CN", [
        ("一章", _p("道可道，非常道。名可名，非常名。无名天地之始，有名万物之母。故常无欲，以观其妙；常有欲，以观其徼。此两者，同出而异名，同谓之玄。玄之又玄，众妙之门。")),
        ("二章", _p("天下皆知美之为美，斯恶已。皆知善之为善，斯不善已。故有无相生，难易相成，长短相形，高下相倾，音声相和，前后相随。")),
    ]),
    ("lunyu.epub", "urn:yuyue:readme:lunyu", "论语", "孔子及其弟子", "zh-CN", [
        ("学而", _p("""
子曰：「学而时习之，不亦说乎？有朋自远方来，不亦乐乎？人不知而不愠，不亦君子乎？」
有子曰：「其为人也孝弟，而好犯上者，鲜矣；不好犯上，而好作乱者，未之有也。君子务本，本立而道生。孝弟也者，其为仁之本与！」
子曰：「巧言令色，鲜矣仁。」
""")),
    ]),
    ("taohuayuan.epub", "urn:yuyue:readme:thy", "桃花源记", "陶渊明", "zh-CN", [
        ("桃花源记", _p("""
晋太元中，武陵人捕鱼为业。缘溪行，忘路之远近。忽逢桃花林，夹岸数百步，中无杂树，芳草鲜美，落英缤纷。渔人甚异之，复前行，欲穷其林。
林尽水源，便得一山，山有小口，仿佛若有光。便舍船，从口入。初极狭，才通人。复行数十步，豁然开朗。土地平旷，屋舍俨然，有良田、美池、桑竹之属。阡陌交通，鸡犬相闻。其中往来种作，男女衣着，悉如外人。黄发垂髫，并怡然自乐。
""")),
    ]),
    ("yueyanglou.epub", "urn:yuyue:readme:yyl", "岳阳楼记", "范仲淹", "zh-CN", [
        ("岳阳楼记", _p("""
庆历四年春，滕子京谪守巴陵郡。越明年，政通人和，百废具兴。乃重修岳阳楼，增其旧制，刻唐贤今人诗赋于其上。属予作文以记之。
予观夫巴陵胜状，在洞庭一湖。衔远山，吞长江，浩浩汤汤，横无际涯；朝晖夕阴，气象万千。此则岳阳楼之大观也，前人之述备矣。
不以物喜，不以己悲。居庙堂之高则忧其民，处江湖之远则忧其君。是进亦忧，退亦忧。然则何时而乐耶？其必曰「先天下之忧而忧，后天下之乐而乐」乎。
""")),
    ]),
    ("ailianshuo.epub", "urn:yuyue:readme:als", "爱莲说", "周敦颐", "zh-CN", [
        ("爱莲说", _p("""
水陆草木之花，可爱者甚蕃。晋陶渊明独爱菊。自李唐来，世人甚爱牡丹。予独爱莲之出淤泥而不染，濯清涟而不妖，中通外直，不蔓不枝，香远益清，亭亭净植，可远观而不可亵玩焉。
予谓菊，花之隐逸者也；牡丹，花之富贵者也；莲，花之君子者也。噫！菊之爱，陶后鲜有闻。莲之爱，同予者何人？牡丹之爱，宜乎众矣。
""")),
    ]),
    ("loushiming.epub", "urn:yuyue:readme:lsm", "陋室铭", "刘禹锡", "zh-CN", [
        ("陋室铭", _p("""
山不在高，有仙则名。水不在深，有龙则灵。斯是陋室，惟吾德馨。苔痕上阶绿，草色入帘青。谈笑有鸿儒，往来无白丁。可以调素琴，阅金经。无丝竹之乱耳，无案牍之劳形。南阳诸葛庐，西蜀子云亭。孔子云：何陋之有？
""")),
    ]),
    ("chushibiao.epub", "urn:yuyue:readme:csb", "出师表", "诸葛亮", "zh-CN", [
        ("出师表", _p("""
先帝创业未半而中道崩殂，今天下三分，益州疲弊，此诚危急存亡之秋也。然侍卫之臣不懈于内，忠志之士忘身于外者，盖追先帝之殊遇，欲报之于陛下也。诚宜开张圣听，以光先帝遗德，恢弘志士之气，不宜妄自菲薄，引喻失义，以塞忠谏之路也。
宫中府中，俱为一体，陟罚臧否，不宜异同。若有作奸犯科及为忠善者，宜付有司论其刑赏，以昭陛下平明之理，不宜偏私，使内外异法也。
""")),
    ]),
    ("guanjun.epub", "urn:yuyue:readme:gj", "关雎", "诗经", "zh-CN", [
        ("关雎", _p("""
关关雎鸠，在河之洲。窈窕淑女，君子好逑。参差荇菜，左右流之。窈窕淑女，寤寐求之。求之不得，寤寐思服。悠哉悠哉，辗转反侧。
参差荇菜，左右采之。窈窕淑女，琴瑟友之。参差荇菜，左右芼之。窈窕淑女，钟鼓乐之。
""")),
    ]),
    ("tangshi.epub", "urn:yuyue:readme:ts", "唐诗三首", "李白等", "zh-CN", [
        ("静夜思", _p("床前明月光，疑是地上霜。举头望明月，低头思故乡。")),
        ("春晓", _p("春眠不觉晓，处处闻啼鸟。夜来风雨声，花落知多少。")),
        ("登鹳雀楼", _p("白日依山尽，黄河入海流。欲穷千里目，更上一层楼。")),
    ]),
    ("alice.epub", "urn:yuyue:readme:alice", "Alice's Adventures in Wonderland", "Lewis Carroll", "en", [
        ("Down the Rabbit-Hole", _p("""
Alice was beginning to get very tired of sitting by her sister on the bank, and of having nothing to do: once or twice she had peeped into the book her sister was reading, but it had no pictures or conversations in it, "and what is the use of a book," thought Alice "without pictures or conversations?"
So she was considering in her own mind (as well as she could, for the hot day made her feel very sleepy and stupid), whether the pleasure of making a daisy-chain would be worth the trouble of getting up and picking the daisies, when suddenly a White Rabbit with pink eyes ran close by her.
""")),
    ]),
    ("sunzi.epub", "urn:yuyue:readme:sunzi", "孙子兵法", "孙武", "zh-CN", [
        ("始计", _p("""
孙子曰：兵者，国之大事，死生之地，存亡之道，不可不察也。故经之以五事，校之以计，而索其情：一曰道，二曰天，三曰地，四曰将，五曰法。
道者，令民与上同意也，故可以与之死，可以与之生，而不畏危。天者，阴阳、寒暑、时制也。地者，远近、险易、广狭、死生也。将者，智、信、仁、勇、严也。法者，曲制、官道、主用也。
""")),
    ]),
    ("zhuangzi.epub", "urn:yuyue:readme:zz", "逍遥游", "庄子", "zh-CN", [
        ("逍遥游", _p("""
北冥有鱼，其名为鲲。鲲之大，不知其几千里也。化而为鸟，其名为鹏。鹏之背，不知其几千里也；怒而飞，其翼若垂天之云。是鸟也，海运则将徙于南冥。南冥者，天池也。
""")),
    ]),
    ("carol.epub", "urn:yuyue:readme:carol", "A Christmas Carol", "Charles Dickens", "en", [
        ("Marley's Ghost", _p("""
Marley was dead, to begin with. There is no doubt whatever about that. The register of his burial was signed by the clergyman, the clerk, the undertaker, and the chief mourner. Scrooge signed it. And Scrooge's name was good upon 'Change, for anything he chose to put his hand to.
Old Marley was as dead as a door-nail.
""")),
    ]),
    ("shishuo.epub", "urn:yuyue:readme:ss", "师说", "韩愈", "zh-CN", [
        ("师说", _p("""
古之学者必有师。师者，所以传道受业解惑也。人非生而知之者，孰能无惑？惑而不从师，其为惑也，终不解矣。生乎吾前，其闻道也固先乎吾，吾从而师之；生乎吾后，其闻道也亦先乎吾，吾从而师之。吾师道也，夫庸知其年之先后生于吾乎？是故无贵无贱，无长无少，道之所存，师之所存也。
""")),
    ]),
    ("lanting.epub", "urn:yuyue:readme:lt", "兰亭集序", "王羲之", "zh-CN", [
        ("兰亭集序", _p("""
永和九年，岁在癸丑，暮春之初，会于会稽山阴之兰亭，修禊事也。群贤毕至，少长咸集。此地有崇山峻岭，茂林修竹，又有清流激湍，映带左右，引以为流觞曲水，列坐其次。虽无丝竹管弦之盛，一觞一咏，亦足以畅叙幽情。
""")),
    ]),
]


FIXTURE_COPIES = [
    "hongloumeng.epub",
    "great-gatsby.epub",
    "pride-and-prejudice.epub",
]


def prepare(out: Path) -> list[Path]:
    """Write generated EPUBs and copy the three real fixtures. Returns every book path."""
    out.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []
    for i, spec in enumerate(GENERATED):
        filename, ident, title, author, lang, sections = spec
        if sections is None:
            sections = beiying_sections()
        path = out / filename
        build_book(path, ident, title, author, lang, sections, PALETTE[i % len(PALETTE)])
        written.append(path)
    for name in FIXTURE_COPIES:
        src = FIXTURES / name
        dest = out / name
        dest.write_bytes(src.read_bytes())
        written.append(dest)
    return written
