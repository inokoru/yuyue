"""两份台词答案逐条对比（按起始句号和开头几个字配对），列出说话人不同的、只在一边有的。
用法：python compare.py <a.jsonl> <b.jsonl>"""
import json, sys

def head(q):
    return "".join(c for c in q if c.isalnum())[:3]

def load(p):
    rows = [json.loads(l) for l in open(p) if l.strip()][1:]
    for r in rows:
        r["who"] = r.get("who") or ""
    return {(r["s"], head(r["q"])): r for r in rows}

a, b = load(sys.argv[1]), load(sys.argv[2])
same = diff = 0
for k in sorted(set(a) | set(b)):
    x, y = a.get(k), b.get(k)
    if x and y:
        if set(x["who"].split("、")) == set(y["who"].split("、")):
            same += 1
        else:
            diff += 1
            print(f"不同 {k[0]} {x['q'][:12]}：{x['who']}（{x['kind']}） vs {y['who']}（{y['kind']}）")
    else:
        r = x or y
        print(f"只在{'前' if x else '后'}一份 {k[0]} {r['q'][:12]}：{r['who']}（{r['kind']}）")
print(f"两边一致 {same}，说话人不同 {diff}，前一份 {len(a)} 条，后一份 {len(b)} 条")
