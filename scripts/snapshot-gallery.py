#!/usr/bin/env python3
"""把界面快照的 PNG 攒成一个 index.html，方便对着看。

用法: python3 scripts/snapshot-gallery.py [输出目录]

Apple 的图在 apple/YuyueSnapshotTests/**/__Snapshots__/ ，
Android 的图在 android/app/src/screenshotTest*/reference/ 。
全窗口图册（系统权限框、Sparkle、网页书页）仍是 scripts/window-gallery.py。
"""
import html
import os
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
out = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else root / "build" / "snapshot-catalog"
out.mkdir(parents=True, exist_ok=True)

groups = [
    ("Apple · swift-snapshot-testing", root / "apple" / "YuyueSnapshotTests"),
    ("Android · Compose Preview Screenshot Testing", root / "android" / "app" / "src"),
]


def pngs(base: Path):
    if not base.is_dir():
        return []
    found = []
    for path in base.rglob("*.png"):
        parts = path.parts
        if "__Snapshots__" in parts or "reference" in parts:
            found.append(path)
    return sorted(found)


sections = []
total = 0
for title, base in groups:
    shots = pngs(base)
    total += len(shots)
    if not shots:
        sections.append(
            f'<section class="missing"><h2>{html.escape(title)}</h2>'
            f"<p class=\"meta\">还没有 PNG。录制命令见 docs/dev/testing.md「界面快照」。</p></section>"
        )
        continue
    figs = []
    for path in shots:
        rel = Path(os.path.relpath(path, out)).as_posix()
        name = path.stem
        figs.append(
            f'<figure><a href="{html.escape(rel)}" target="_blank">'
            f'<img src="{html.escape(rel)}" loading="lazy" alt="{html.escape(name)}"></a>'
            f"<figcaption><code>{html.escape(name)}</code></figcaption></figure>"
        )
    sections.append(
        f"<section><h2>{html.escape(title)}</h2>"
        f'<p class="meta">{len(shots)} 张 · 点图看原图</p>'
        f'<div class="row">{"".join(figs)}</div></section>'
    )

doc = f"""<!DOCTYPE html>
<html lang="zh"><head><meta charset="utf-8">
<title>语阅 · 界面快照</title>
<style>
  body {{ font: 15px/1.6 -apple-system, "PingFang SC", sans-serif; margin: 0; background: #f5f4f1; color: #2c2b28; }}
  header {{ padding: 28px 32px 10px; }}
  h1 {{ font-size: 22px; margin: 0 0 4px; }}
  header p {{ color: #77756e; margin: 0; }}
  section {{ background: #fff; border-radius: 14px; margin: 18px 32px; padding: 18px 20px; box-shadow: 0 1px 3px rgb(0 0 0 / 6%); }}
  section.missing {{ background: #fdf8ee; }}
  h2 {{ font-size: 16px; margin: 0 0 2px; }}
  .meta {{ color: #98968e; font-size: 12px; margin: 0 0 12px; }}
  .row {{ display: flex; flex-wrap: wrap; gap: 14px; align-items: flex-start; }}
  figure {{ margin: 0; max-width: 280px; }}
  img {{ max-height: 520px; max-width: 100%; border: 1px solid #e4e2dc; border-radius: 8px; display: block; background: #faf9f6; }}
  figcaption {{ color: #98968e; font-size: 12px; margin-top: 4px; word-break: break-all; }}
</style></head><body>
<header><h1>语阅 · 界面快照</h1>
<p>{total} 张 · 书架、阅读条、设置和 App 自己画的弹层。系统权限框、Sparkle 安装窗、网页书页看 scripts/window-gallery.py。</p></header>
{"".join(sections)}
</body></html>
"""
(out / "index.html").write_text(doc, encoding="utf-8")
print(f"index.html 写好了: {total} 张 → {out / 'index.html'}")
