"""Public-domain demo EPUBs for README screenshots.

Texts are short genuine excerpts: Chinese originals, or public-domain English
for the translated-looking shelf titles. Nothing here is a still-copyrighted
translation, and nothing is written as if it were the whole classic.
"""

from __future__ import annotations

import hashlib
import shutil
import zipfile
from pathlib import Path

from readme_shots.covers import Cover, render

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


def build_book(
    path: Path,
    ident: str,
    title: str,
    author: str,
    lang: str,
    sections: list[tuple[str, list[str]]],
    cover: Cover,
) -> None:
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
        if i == 1 and author:
            head = f"<h1>{title}</h1>\n<p class=\"author\">{author}</p>\n<h2>{heading}</h2>\n{paras}"
        elif i == 1:
            head = f"<h1>{title}</h1>\n<h2>{heading}</h2>\n{paras}"
        else:
            head = f"<h2>{heading}</h2>\n{paras}"
        bodies.append((f"OEBPS/{href}", xhtml(heading, lang, head)))
    manifest = "\n".join(
        f'    <item id="{i}" href="{h}" media-type="{m}"' + (f' properties="{p}"' if p else "") + "/>"
        for i, h, m, p in items
    )
    spine_xml = "\n".join(f'    <itemref idref="{i}"/>' for i in spine)
    creator = f"\n    <dc:creator>{author}</dc:creator>" if author else ""
    opf = f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid" xml:lang="{lang}">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="uid">{ident}</dc:identifier>
    <dc:title>{title}</dc:title>{creator}
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


def _p(text: str) -> list[str]:
    return [ln.strip() for ln in text.strip().split("\n") if ln.strip()]


def beiying_sections() -> list[tuple[str, list[str]]]:
    text = (FIXTURES / "beiying.txt").read_text(encoding="utf-8")
    lines = [ln.strip() for ln in text.splitlines() if ln.strip()]
    paras = lines[2:]
    if len(paras) < 4:
        return [("背影", paras)]
    return [("背影", paras[:-2]), ("月台", paras[-2:])]


def patch_hongloumeng(dest: Path) -> None:
    """Keep the fixture text. Replace the Gutenberg cover and the shelf metadata."""
    cover = render(
        Cover("cloth", "红楼梦", "曹雪芹", bg="#3A2A2C", ink="#EDE3D4")
    )
    src = FIXTURES / "hongloumeng.epub"
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists():
        dest.unlink()
    with zipfile.ZipFile(src) as zin, zipfile.ZipFile(dest, "w") as zout:
        # mimetype must stay first and uncompressed.
        info = zipfile.ZipInfo("mimetype")
        info.compress_type = zipfile.ZIP_STORED
        zout.writestr(info, zin.read("mimetype"))
        for name in zin.namelist():
            if name == "mimetype":
                continue
            data = zin.read(name)
            if name.endswith("content.opf"):
                text = data.decode("utf-8")
                text = text.replace("<dc:title>紅樓夢</dc:title>", "<dc:title>红楼梦</dc:title>")
                text = text.replace(">Xueqin Cao</dc:creator>", ">曹雪芹</dc:creator>")
                text = text.replace(">Cao, Xueqin</meta>", ">曹雪芹</meta>")
                text = text.replace("<dc:language>zh</dc:language>", "<dc:language>zh-CN</dc:language>")
                data = text.encode("utf-8")
            elif name.endswith("24264-cover.png"):
                data = cover
            zout.writestr(name, data, compress_type=zipfile.ZIP_DEFLATED)


def catalog() -> list[tuple[str, str, str, str, str, list, Cover]]:
    return [
        ("beiying.epub", "urn:yuyue:readme:beiying", "背影", "朱自清", "zh-CN", beiying_sections(),
         Cover("bigchar", "背影", "朱自清", "#EFE4D2", "#2C241C")),
        ("nahan.epub", "urn:yuyue:readme:nahan", "呐喊", "鲁迅", "zh-CN", [
            ("狂人日记", _p("""
某君昆仲，今隐其名，皆余昔日在中学时良友；分隔多年，消息渐阙。日前偶闻其一大病；适归故乡，迂道往访，则仅晤一人，言病者其弟也。劳君远道来视，然已早愈，赴某地候补矣。因大笑，出示日记二册，谓可见当日病状，不妨献诸旧友。
今天晚上，很好的月光。我不见他，已是三十多年；今天见了，精神分外爽快。才知道以前的三十多年，全是发昏；然而须十分小心。不然，那赵家的狗，何以看我两眼呢？我怕得有理。
""")),
        ], Cover("bigchar", "呐喊", "鲁迅", "#2A2A28", "#EDE6D8")),
        ("zhaohua.epub", "urn:yuyue:readme:zhaohua", "朝花夕拾", "鲁迅", "zh-CN", [
            ("从百草园到三味书屋", _p("""
我家的后面有一个很大的园，相传叫作百草园。现在是早已并屋子一起卖给朱文公的子孙了，连那最末次的相见也已经隔了七八年，其中似乎确凿只有一些野草；但那时却是我的乐园。
不必说碧绿的菜畦，光滑的石井栏，高大的皂荚树，紫红的桑椹；也不必说鸣蝉在树叶里长吟，肥胖的黄蜂伏在菜花上，轻捷的叫天子（云雀）忽然从草间直窜向云霄里去了。单是周围的短短的泥墙根一带，就有无限趣味。
""")),
        ], Cover("bigchar", "朝花夕拾", "鲁迅", "#D8D5C6", "#2C241C", pair=("朝花", "夕拾"))),
        ("lunyu.epub", "urn:yuyue:readme:lunyu", "论语", "孔子", "zh-CN", [
            ("学而", _p("""
子曰：「学而时习之，不亦说乎？有朋自远方来，不亦乐乎？人不知而不愠，不亦君子乎？」
有子曰：「其为人也孝弟，而好犯上者，鲜矣；不好犯上，而好作乱者，未之有也。君子务本，本立而道生。孝弟也者，其为仁之本与！」
子曰：「巧言令色，鲜矣仁。」
""")),
        ], Cover("bigchar", "论语", "孔子", "#D6DCCF", "#243028")),
        ("daodejing.epub", "urn:yuyue:readme:ddj", "道德经", "老子", "zh-CN", [
            ("一章", _p("道可道，非常道。名可名，非常名。无名天地之始，有名万物之母。故常无欲，以观其妙；常有欲，以观其徼。此两者，同出而异名，同谓之玄。玄之又玄，众妙之门。")),
            ("二章", _p("天下皆知美之为美，斯恶已。皆知善之为善，斯不善已。故有无相生，难易相成，长短相形，高下相倾，音声相和，前后相随。")),
        ], Cover("column", "道德经", "老子", "#E6DCC8", "#2C261E")),
        ("zhuangzi.epub", "urn:yuyue:readme:zz", "庄子", "庄周", "zh-CN", [
            ("逍遥游", _p("""
北冥有鱼，其名为鲲。鲲之大，不知其几千里也。化而为鸟，其名为鹏。鹏之背，不知其几千里也；怒而飞，其翼若垂天之云。是鸟也，海运则将徙于南冥。南冥者，天池也。
""")),
        ], Cover("bigchar", "庄子", "庄周", "#D5D8D4", "#1E2824")),
        ("shijing.epub", "urn:yuyue:readme:sj", "诗经", "", "zh-CN", [
            ("关雎", _p("关关雎鸠，在河之洲。窈窕淑女，君子好逑。参差荇菜，左右流之。窈窕淑女，寤寐求之。求之不得，寤寐思服。悠哉悠哉，辗转反侧。")),
            ("蒹葭", _p("蒹葭苍苍，白露为霜。所谓伊人，在水一方。溯洄从之，道阻且长。溯游从之，宛在水中央。")),
        ], Cover("bigchar", "诗经", "", "#E8D8D0", "#3A2C28")),
        ("tangshi.epub", "urn:yuyue:readme:ts", "唐诗三百首", "蘅塘退士", "zh-CN", [
            ("静夜思", _p("李白\n床前明月光，疑是地上霜。举头望明月，低头思故乡。")),
            ("春晓", _p("孟浩然\n春眠不觉晓，处处闻啼鸟。夜来风雨声，花落知多少。")),
            ("登鹳雀楼", _p("王之涣\n白日依山尽，黄河入海流。欲穷千里目，更上一层楼。")),
            ("春望", _p("杜甫\n国破山河在，城春草木深。感时花溅泪，恨别鸟惊心。烽火连三月，家书抵万金。白头搔更短，浑欲不胜簪。")),
            ("游子吟", _p("孟郊\n慈母手中线，游子身上衣。临行密密缝，意恐迟迟归。谁言寸草心，报得三春晖。")),
            ("枫桥夜泊", _p("张继\n月落乌啼霜满天，江枫渔火对愁眠。姑苏城外寒山寺，夜半钟声到客船。")),
            ("江雪", _p("柳宗元\n千山鸟飞绝，万径人踪灭。孤舟蓑笠翁，独钓寒江雪。")),
            ("九月九日忆山东兄弟", _p("王维\n独在异乡为异客，每逢佳节倍思亲。遥知兄弟登高处，遍插茱萸少一人。")),
        ], Cover("mark", "唐诗三百首", "蘅塘退士", "#E8DCC8", "#2C241C", mark="诗")),
        ("songci.epub", "urn:yuyue:readme:sc", "宋词三百首", "朱祖谋", "zh-CN", [
            ("雨霖铃", _p("""
柳永
寒蝉凄切，对长亭晚，骤雨初歇。都门帐饮无绪，留恋处，兰舟催发。执手相看泪眼，竟无语凝噎。念去去，千里烟波，暮霭沉沉楚天阔。
多情自古伤离别，更那堪，冷落清秋节。今宵酒醒何处？杨柳岸，晓风残月。此去经年，应是良辰好景虚设。便纵有千种风情，更与何人说？
""")),
            ("声声慢", _p("""
李清照
寻寻觅觅，冷冷清清，凄凄惨惨戚戚。乍暖还寒时候，最难将息。三杯两盏淡酒，怎敌他、晚来风急？雁过也，正伤心，却是旧时相识。
满地黄花堆积，憔悴损，如今有谁堪摘？守着窗儿，独自怎生得黑？梧桐更兼细雨，到黄昏、点点滴滴。这次第，怎一个愁字了得！
""")),
            ("江城子", _p("""
苏轼
十年生死两茫茫，不思量，自难忘。千里孤坟，无处话凄凉。纵使相逢应不识，尘满面，鬓如霜。
夜来幽梦忽还乡，小轩窗，正梳妆。相顾无言，惟有泪千行。料得年年肠断处，明月夜，短松冈。
""")),
        ], Cover("mark", "宋词三百首", "朱祖谋", "#D2D0D4", "#2A2A28", mark="词")),
        ("guwen.epub", "urn:yuyue:readme:gw", "古文观止", "吴楚材等", "zh-CN", [
            ("桃花源记", _p("""
陶渊明
晋太元中，武陵人捕鱼为业。缘溪行，忘路之远近。忽逢桃花林，夹岸数百步，中无杂树，芳草鲜美，落英缤纷。渔人甚异之，复前行，欲穷其林。
林尽水源，便得一山，山有小口，仿佛若有光。便舍船，从口入。初极狭，才通人。复行数十步，豁然开朗。土地平旷，屋舍俨然，有良田、美池、桑竹之属。阡陌交通，鸡犬相闻。其中往来种作，男女衣着，悉如外人。黄发垂髫，并怡然自乐。
""")),
            ("岳阳楼记", _p("""
范仲淹
庆历四年春，滕子京谪守巴陵郡。越明年，政通人和，百废具兴。乃重修岳阳楼，增其旧制，刻唐贤今人诗赋于其上。属予作文以记之。
予观夫巴陵胜状，在洞庭一湖。衔远山，吞长江，浩浩汤汤，横无际涯；朝晖夕阴，气象万千。此则岳阳楼之大观也，前人之述备矣。
不以物喜，不以己悲。居庙堂之高则忧其民，处江湖之远则忧其君。是进亦忧，退亦忧。然则何时而乐耶？其必曰「先天下之忧而忧，后天下之乐而乐」乎。
""")),
            ("兰亭集序", _p("""
王羲之
永和九年，岁在癸丑，暮春之初，会于会稽山阴之兰亭，修禊事也。群贤毕至，少长咸集。此地有崇山峻岭，茂林修竹，又有清流激湍，映带左右，引以为流觞曲水，列坐其次。虽无丝竹管弦之盛，一觞一咏，亦足以畅叙幽情。
""")),
            ("出师表", _p("""
诸葛亮
先帝创业未半而中道崩殂，今天下三分，益州疲弊，此诚危急存亡之秋也。然侍卫之臣不懈于内，忠志之士忘身于外者，盖追先帝之殊遇，欲报之于陛下也。诚宜开张圣听，以光先帝遗德，恢弘志士之气，不宜妄自菲薄，引喻失义，以塞忠谏之路也。
""")),
            ("师说", _p("""
韩愈
古之学者必有师。师者，所以传道受业解惑也。人非生而知之者，孰能无惑？惑而不从师，其为惑也，终不解矣。生乎吾前，其闻道也固先乎吾，吾从而师之；生乎吾后，其闻道也亦先乎吾，吾从而师之。吾师道也，夫庸知其年之先后生于吾乎？是故无贵无贱，无长无少，道之所存，师之所存也。
""")),
            ("爱莲说", _p("""
周敦颐
水陆草木之花，可爱者甚蕃。晋陶渊明独爱菊。自李唐来，世人甚爱牡丹。予独爱莲之出淤泥而不染，濯清涟而不妖，中通外直，不蔓不枝，香远益清，亭亭净植，可远观而不可亵玩焉。
予谓菊，花之隐逸者也；牡丹，花之富贵者也；莲，花之君子者也。噫！菊之爱，陶后鲜有闻。莲之爱，同予者何人？牡丹之爱，宜乎众矣。
""")),
            ("陋室铭", _p("""
刘禹锡
山不在高，有仙则名。水不在深，有龙则灵。斯是陋室，惟吾德馨。苔痕上阶绿，草色入帘青。谈笑有鸿儒，往来无白丁。可以调素琴，阅金经。无丝竹之乱耳，无案牍之劳形。南阳诸葛庐，西蜀子云亭。孔子云：何陋之有？
""")),
        ], Cover("mark", "古文观止", "吴楚材等", "#DCCCB4", "#2C241C", mark="文")),
        ("shishuo.epub", "urn:yuyue:readme:ssy", "世说新语", "刘义庆", "zh-CN", [
            ("德行", _p("""
管宁、华歆共园中锄菜，见地有片金，管挥锄与瓦石不异，华捉而掷去之。又尝同席读书，有乘轩冕过门者，宁读如故，歆废书出看。宁割席分坐，曰：「子非吾友也。」
""")),
            ("任诞", _p("""
王子猷居山阴，夜大雪，眠觉，开室命酌酒，四望皎然。因起彷徨，咏左思《招隐诗》。忽忆戴安道。时戴在剡，即便夜乘小船就之。经宿方至，造门不前而返。人问其故，王曰：「吾本乘兴而行，兴尽而返，何必见戴？」
""")),
        ], Cover("horizon", "世说新语", "刘义庆", "#D3D6D2", "#1E2824")),
        ("liaozhai.epub", "urn:yuyue:readme:lz", "聊斋志异", "蒲松龄", "zh-CN", [
            ("聊斋自志", _p("""
披萝带荔，三闾氏感而为骚；牛鬼蛇神，长爪郎吟而成癖。自鸣天籁，不择好音，有由然矣。松落落秋萤之火，魑魅争光；逐逐野马之尘，罔两见笑。才非干宝，雅爱搜神；情类黄州，喜人谈鬼。闻则命笔，遂以成编。
""")),
        ], Cover("cloth", "聊斋志异", "蒲松龄", "#1E2A2C", "#E6DCC8")),
        ("renjian.epub", "urn:yuyue:readme:rj", "人间词话", "王国维", "zh-CN", [
            ("境界", _p("""
词以境界为最上。有境界则自成高格，自有名句。五代北宋之词所以独绝者在此。
有有我之境，有无我之境。「泪眼问花花不语，乱红飞过秋千去」，「可堪孤馆闭春寒，杜鹃声里斜阳暮」，有我之境也。「采菊东篱下，悠然见南山」，「寒波澹澹起，白鸟悠悠下」，无我之境也。有我之境，以我观物，故物皆著我之色彩。无我之境，以物观物，故不知何者为我，何者为物。
""")),
        ], Cover("horizon", "人间词话", "王国维", "#EFE8DC", "#2C241C")),
        ("fusheng.epub", "urn:yuyue:readme:fs", "浮生六记", "沈复", "zh-CN", [
            ("闺房记乐", _p("""
余生乾隆癸未冬十一月二十有二日，正值太平盛世，且在衣冠之家，居苏州沧浪亭畔，天之厚我可谓至矣。东坡云：「事如春梦了无痕」，苟不记之笔墨，未免有辜彼苍之厚。
余忆童稚时，能张目对日，明察秋毫，见藐小微物，必细察其纹理，故时有物外之趣。
""")),
        ], Cover("horizon", "浮生六记", "沈复", "#E6D6C8", "#3A2C28")),
        ("xiyou.epub", "urn:yuyue:readme:xy", "西游记", "吴承恩", "zh-CN", [
            ("灵根育孕源流出", _p("""
混沌未分天地乱，茫茫渺渺无人见。自从盘古破鸿蒙，开辟从兹清浊辨。
盖闻天地之数，有十二万九千六百岁为一元。将一元分为十二会，乃子、丑、寅、卯、辰、巳、午、未、申、酉、戌、亥之十二支也。
东胜神洲，海外有一国土，名曰傲来国。国近大海，海中有一座名山，唤为花果山。
""")),
        ], Cover("cloth", "西游记", "吴承恩", "#3A3328", "#E8DCC8")),
        ("walden.epub", "urn:yuyue:readme:walden", "瓦尔登湖", "梭罗", "en", [
            ("Where I Lived, and What I Lived For", _p("""
When I wrote the following pages, or rather the bulk of them, I lived alone, in the woods, a mile from any neighbor, in a house which I had built myself, on the shore of Walden Pond, in Concord, Massachusetts, and earned my living by the labor of my hands only. I lived there two years and two months. At present I am a sojourner in civilized life again.
I went to the woods because I wished to live deliberately, to front only the essential facts of life, and see if I could not learn what it had to teach, and not, when I came to die, discover that I had not lived.
I wanted to live deep and suck out all the marrow of life, to live so sturdily and Spartan-like as to put to rout all that was not life, to cut a broad swath and shave close, to drive life into a corner, and reduce it to its lowest terms.
""")),
        ], Cover("panel", "瓦尔登湖", "梭罗", "#24332A", "#E8E2D4", band="#2E4436", english="Walden")),
        ("jane.epub", "urn:yuyue:readme:jane", "简·爱", "夏洛蒂·勃朗特", "en", [
            ("Chapter I", _p("""
There was no possibility of taking a walk that day. We had been wandering, indeed, in the leafless shrubbery an hour in the morning; but since dinner (Mrs. Reed, when there was no company, dined early) the cold winter wind had brought with it clouds so sombre, and a rain so penetrating, that further outdoor exercise was now out of the question.
I was glad of it: I never liked long walks, especially on chilly afternoons: dreadful to me was the coming home in the raw twilight, with nipped fingers and toes, and a heart saddened by the chidings of Bessie, the nurse, and humbled by the consciousness of my physical inferiority to Eliza, John, and Georgiana Reed.
""")),
        ], Cover("panel", "简·爱", "夏洛蒂·勃朗特", "#4A3034", "#F0E6D8", band="#5A3A3E", english="Jane Eyre")),
        ("crime.epub", "urn:yuyue:readme:crime", "罪与罚", "陀思妥耶夫斯基", "en", [
            ("Part I", _p("""
On an exceptionally hot evening early in July a young man came out of the garret in which he lodged in S. Place and walked slowly, as though in hesitation, towards K. bridge.
He had successfully avoided meeting his landlady on the staircase. His garret was under the roof of a high, five-storied house and was more like a cupboard than a room. The landlady who provided him with garret, dinners, and attendance, lived on the floor below, and every time he went out he was obliged to pass her kitchen, the door of which invariably stood open.
""")),
        ], Cover("cloth", "罪与罚", "陀思妥耶夫斯基", "#2C3038", "#E8E2D6")),
        ("hamlet.epub", "urn:yuyue:readme:hamlet", "哈姆雷特", "莎士比亚", "en", [
            ("Act I", _p("""
Bernardo: Who's there?
Francisco: Nay, answer me: stand, and unfold yourself.
Bernardo: Long live the king!
Francisco: Bernardo?
Bernardo: He.
To be, or not to be, that is the question:
Whether 'tis nobler in the mind to suffer
The slings and arrows of outrageous fortune,
Or to take arms against a sea of troubles
And by opposing end them.
""")),
        ], Cover("panel", "哈姆雷特", "莎士比亚", "#1C2430", "#E8E2D4", band="#243040", english="Hamlet")),
    ]


def all_covers() -> list[Cover]:
    covers = [row[6] for row in catalog()]
    covers.append(Cover("cloth", "红楼梦", "曹雪芹", "#3A2A2C", "#EDE3D4"))
    return covers


def prepare(out: Path) -> list[Path]:
    """Write generated EPUBs and the three fixture books. Returns every book path."""
    out.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []
    for filename, ident, title, author, lang, sections, cover in catalog():
        path = out / filename
        build_book(path, ident, title, author, lang, sections, cover)
        written.append(path)
    patch_hongloumeng(out / "hongloumeng.epub")
    written.append(out / "hongloumeng.epub")
    for name in ("great-gatsby.epub", "pride-and-prejudice.epub"):
        dest = out / name
        shutil.copyfile(FIXTURES / name, dest)
        written.append(dest)
    return written


def same_file(a: Path, b: Path) -> bool:
    return hashlib.sha256(a.read_bytes()).digest() == hashlib.sha256(b.read_bytes()).digest()
