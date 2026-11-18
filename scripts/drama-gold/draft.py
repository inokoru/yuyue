"""广播剧考卷的草稿：让一个（不是被考的那个）大模型把一章的每句台词标出说话人，写成
fixtures/speakers 的格式，再由人核对。

用法：python draft.py <yuyue> <书库> <书> <章号> <章名> <书文件名> <base url> <model> <out.jsonl>
"""
import json, subprocess, sys, urllib.request

yuyue, lib, book, section, title, bookfile, base, model, out = sys.argv[1:10]
rows = subprocess.run([yuyue, "sentences", lib, book, section], capture_output=True, text=True, check=True).stdout.splitlines()
text = "\n".join(f"[{r.split(chr(9), 1)[0]}] {r.split(chr(9), 1)[1]}" for r in rows if "\t" in r)
system = """你在给小说标注“每句台词是谁说的”，用来做考卷，要求极其准确。
原文每句前有 [句号]。找出所有引号里的话（“”「」『』"" 等），按出现顺序，每段台词一条：
- s：台词开始的句号；e：结束的句号
- q：台词开头的 6～10 个字，逐字抄原文（不含引号）
- who：说话人，用原文里最常用的那个名字（同一个人始终同一个写法）；一群人一起说用“、”连起几个名字或写“众人”
- kind：named（说话词前后写了名字）／zero（承前省略了主语）／pronoun（他、她、二人这类代词）／group（众人、一群人）／unnamed（没名字只有身份，如“一个小厮”）／not_speech（引号里不是说出口的话：题字、书名、引用、心里想的除外）
- 心里想的（心想、暗道）也算一条，kind 照写法标
- ambiguous：真拿不准就标 true（只在真拿不准时）
只回 JSON：{"lines":[{"s":..,"e":..,"q":"..","who":"..","kind":"..","ambiguous":false}]}"""
body = {"model": model, "temperature": 0, "max_tokens": 32000,
        "messages": [{"role": "system", "content": system}, {"role": "user", "content": text}],
        "response_format": {"type": "json_object"}}
req = urllib.request.Request(base.rstrip("/") + "/chat/completions", data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
import time, urllib.error
for attempt in range(12):
    try:
        ans = json.load(urllib.request.urlopen(req, timeout=1800))["choices"][0]["message"]["content"]
        break
    except urllib.error.HTTPError as e:
        # Too many requests: wait and ask again, longer each time.
        if e.code != 429 or attempt == 11:
            raise
        time.sleep(30 * (attempt + 1))
ans = ans.strip().removeprefix("```json").removeprefix("```").removesuffix("```")
lines = json.loads(ans)["lines"]
with open(out, "w") as f:
    f.write(json.dumps({"book": bookfile, "section": int(section), "title": title, "note": f"草稿：{model} 标，待人核对"}, ensure_ascii=False) + "\n")
    for l in lines:
        row = {"s": l["s"], "e": l.get("e", l["s"]), "q": l["q"], "who": l["who"], "kind": l["kind"]}
        if l.get("ambiguous"): row["ambiguous"] = True
        f.write(json.dumps(row, ensure_ascii=False) + "\n")
print(out, len(lines), "lines")
