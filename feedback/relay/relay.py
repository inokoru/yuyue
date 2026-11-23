#!/usr/bin/env python3
"""把 ntfy 上的反馈搬进语阅仓库的 Issue（公开的），每条一个。不解密任何东西。

语阅是开源项目，反馈本来就要公开，不收集邮箱或联系方式，所以 App 发来的正文、类别、
版本、系统都是明文（`{"v":2,…}`）。不公开的附件在 App 里用开发者的公钥封好，这里原样搬运：

- 截图（用户同意公开的，消息正文 `v2shot`，附件是 JPEG）：推到公开的截图仓库
  （SHOTS_REPO，本地检出在 SHOTS_DIR），贴进 Issue。
- 其他附件（非图片文件或用户不想公开的截图；消息正文 `v1file`，附件是密封箱）：
  原样提交到私有仓库（GH_REPO 的 feedback-files/），Issue 里只写「只有开发者能看到」。

私钥只在开发者自己的 Mac 上（本机解开私密附件）。这个脚本和它跑的
GitHub Actions 都拿不到私钥，所以私有仓库或 Actions 出了问题，也解不开任何东西。

ntfy.sh 只缓存大约 12 小时，附件只留 3 小时，所以这个脚本要在那之前跑（由 Worker 每 5 分钟检查并按需唤醒）。
格式不对的消息跳过。已经开过的（正文里有 ntfy-id）不会再开，和已有 Issue 正文一样的也跳过。
话题名打包在 App 里，谁都能往里发，所以每次最多开 MAX_ISSUES 个（默认 10，剩下的下一次），
公开的附件只收 JPEG。

环境变量：
  NTFY_TOPIC      话题名
  NTFY_SERVER     默认 https://ntfy.sh
  ISSUE_REPO      开 Issue 的仓库：inokoru/yuyue
  ISSUE_TOKEN     能在 ISSUE_REPO 开 Issue 的 token（fine-grained，只给 Issues 读写）
  GH_REPO         放密封附件的私有仓库，Actions 里用 github.repository
  GH_TOKEN        能往 GH_REPO 提交的 token，Actions 里用 GITHUB_TOKEN
  SHOTS_REPO      放公开附件的仓库：inokoru/yuyue-feedback-assets
  SHOTS_DIR       它的本地检出（工作流用部署密钥 clone 下来），推送也用那把密钥
  SHOTS_BRANCH    默认 main
  MAX_ISSUES      每次最多新开几个，默认 10
  LABEL           反馈 Issue 的标签，默认「反馈」

本地试：python relay.py --dry-run --out 目录
  只要 NTFY_TOPIC，不碰 GitHub；打印会开的 Issue，附件（截图和密文）存进目录。
"""

from __future__ import annotations

import argparse
import base64
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

ID_RE = re.compile(r"ntfy-id:\s*([^\s<]+)")
HASH_RE = re.compile(r"text-hash:\s*([0-9a-f]{16})")
DEDUP_WINDOW_SECONDS = 24 * 3600
SEALED_MARK = "v1file"
SHOT_MARK = "v2shot"
FILES_MAX = 3
TEXT_MAX = 1000          # the app caps at 800; a bit of room, then junk
DOWNLOAD_MAX = 2_100_000
CATEGORIES = {"bug": "问题", "idea": "建议", "other": "其他"}


# ---------------------------------------------------------------- messages

def open_message(message: str) -> dict | None:
    """The report in one ntfy message (`{"v":2,…}`), checked; None when it is junk."""
    text = (message or "").strip()
    if not text.startswith("{"):
        return None
    try:
        data = json.loads(text)
    except ValueError:
        return None
    if not isinstance(data, dict) or data.get("v") != 2:
        return None
    body = data.get("text")
    if not isinstance(body, str) or not body.strip() or len(body) > TEXT_MAX:
        return None
    if data.get("category") not in CATEGORIES:
        data["category"] = "other"
    for key, limit in (("app", 80), ("platform", 40), ("os", 80)):
        value = data.get(key)
        data[key] = clean_line(value)[:limit] if isinstance(value, str) else ""
    files = data.get("files")
    if not isinstance(files, list):
        files = []
    data["files"] = [safe_id(str(i)) for i in files[:FILES_MAX] if safe_id(str(i))]
    return data


def clean_line(text: str) -> str:
    """One line for the Issue header: no control characters, no Markdown/HTML that could fake a field."""
    text = re.sub(r"[\x00-\x1f\x7f<>`*|#\[\]]", " ", text)
    return " ".join(text.split())


def safe_id(ntfy_id: str) -> str:
    return re.sub(r"[^A-Za-z0-9_-]", "", ntfy_id)[:80]


def text_hash(data: dict) -> str:
    text = " ".join(str(data.get("text") or "").split())
    return hashlib.sha256(text.encode()).hexdigest()[:16]


def issue_title(data: dict) -> str:
    label = CATEGORIES.get(data.get("category"), "其他")
    text = " ".join(str(data.get("text", "")).split())
    snippet = text[:40] + ("…" if len(text) > 40 else "")
    return f"[{label}] {snippet}"


def public_text(text: str) -> str:
    """The person's words as Markdown, minus what would break out of it: HTML comments
    (where the markers live) and @mentions (GitHub would notify whoever is named)."""
    text = text.strip().replace("<!--", "&lt;!--").replace("-->", "--&gt;")
    return re.sub(r"(?<![\w`])@(?=[A-Za-z0-9-])", "@​", text)


def issue_body(ntfy_id: str, data: dict, lines: list[str] | None = None) -> str:
    """`lines`: one Markdown line per attachment (a picture, a private note, or why it is missing)."""
    ntfy_id = safe_id(ntfy_id)
    label = CATEGORIES.get(data.get("category"), "其他")
    tail = ("\n" + "\n\n".join(lines) + "\n") if lines else ""
    return (
        f"<!-- ntfy-id: {ntfy_id} text-hash: {text_hash(data)} -->\n\n"
        f"**类别**：{label}\n"
        f"**平台**：{data.get('platform') or ''}\n"
        f"**系统**：{data.get('os') or ''}\n"
        f"**版本**：{data.get('app') or ''}\n"
        f"**时间**：{_when(data.get('sentAt'))}\n\n"
        f"{public_text(str(data.get('text') or ''))}\n"
        f"{tail}"
    )


def known_ids(issues: list) -> set[str]:
    return _markers(issues, ID_RE)


def known_hashes(issues: list, now: float | None = None, window: int = DEDUP_WINDOW_SECONDS) -> set[str]:
    """Only deduplicate identical text against recent Issues (default 24h), so another person
    reporting the same short symptom weeks later is not silently dropped."""
    cutoff = (time.time() if now is None else now) - window
    recent = [i for i in issues if _is_recent(i, cutoff)]
    return _markers(recent, HASH_RE)


def _is_recent(issue: dict, cutoff: float) -> bool:
    if not isinstance(issue, dict):
        return False
    created = issue.get("created_at")
    if not isinstance(created, str) or not created:
        return True
    try:
        ts = datetime.datetime.fromisoformat(created.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return True
    return ts >= cutoff


def _markers(issues: list, pattern: re.Pattern) -> set[str]:
    found = set()
    for issue in issues:
        if not isinstance(issue, dict) or "pull_request" in issue:
            continue
        match = pattern.search(issue.get("body") or "")
        if match:
            found.add(match.group(1))
    return found


def _when(sent_at) -> str:
    if not isinstance(sent_at, (int, float)) or sent_at <= 0:
        return ""
    return time.strftime("%Y-%m-%d %H:%M UTC", time.gmtime(sent_at / 1000))


# ---------------------------------------------------------------- attachments

def attachment(server: str, item: dict) -> tuple[str, str] | None:
    """("shot" | "sealed", url) for an attachment message, only when ntfy itself hosts the file."""
    mark = str(item.get("message") or "").strip()
    kind = {SHOT_MARK: "shot", SEALED_MARK: "sealed"}.get(mark)
    if not kind:
        return None
    found = item.get("attachment")
    url = found.get("url") if isinstance(found, dict) else None
    if not isinstance(url, str) or not url.startswith(server.rstrip("/") + "/file/"):
        return None
    return kind, url


def is_jpeg(raw: bytes) -> bool:
    return raw[:3] == b"\xff\xd8\xff" and raw.rstrip(b"\x00")[-2:] == b"\xff\xd9"


def download(url: str) -> bytes | None:
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "yuyue-feedback"})
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read(DOWNLOAD_MAX + 1)
    except Exception:
        return None
    return raw if 0 < len(raw) <= DOWNLOAD_MAX else None


def fetch_files(data: dict, files: dict[str, tuple[str, str]]) -> list[tuple[str, bytes] | None]:
    """Per listed id: (kind, bytes), or None when it is gone (ntfy keeps a file 3 hours) or bad."""
    found: list[tuple[str, bytes] | None] = []
    for file_id in data["files"]:
        kind, url = files.get(file_id, (None, None))
        raw = download(url) if url else None
        if raw is None or (kind == "shot" and not is_jpeg(raw)):
            found.append(None)
        else:
            found.append((kind, raw))
    return found


class Shots:
    """The public screenshots repo, checked out at `path`; pushed with the deploy key the workflow set up."""

    def __init__(self, repo: str, path: str, branch: str = "main"):
        self.repo, self.path, self.branch = repo, path, branch

    def url(self, rel: str) -> str:
        return f"https://raw.githubusercontent.com/{self.repo}/{urllib.parse.quote(self.branch)}/{urllib.parse.quote(rel)}"

    def git(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run(["git", "-C", self.path, *args], capture_output=True, text=True)

    def add(self, ntfy_id: str, shots: list[tuple[int, bytes]]) -> bool:
        """Commit and push one report's screenshots as `<ntfy-id>/<n>.jpg`."""
        rels = []
        for n, raw in shots:
            rel = f"{ntfy_id}/{n}.jpg"
            os.makedirs(os.path.join(self.path, ntfy_id), exist_ok=True)
            with open(os.path.join(self.path, rel), "wb") as handle:
                handle.write(raw)
            rels.append(rel)
        self.git("add", "--", *rels)
        if self.git("diff", "--cached", "--quiet").returncode != 0:  # nothing staged: pushed by an earlier run
            commit = self.git("-c", "user.name=yuyue-feedback", "-c", "user.email=feedback@users.noreply.github.com",
                              "commit", "-q", "-m", f"反馈截图 {ntfy_id}")
            if commit.returncode != 0:
                print(f"提交截图失败 {ntfy_id}：{commit.stderr.strip()}", file=sys.stderr)
                return False
        for _ in range(2):
            push = self.git("push", "-q", "origin", f"HEAD:{self.branch}")
            if push.returncode == 0:
                return True
            self.git("pull", "-q", "--rebase", "origin", self.branch)
        print(f"推送截图失败 {ntfy_id}：{push.stderr.strip()}", file=sys.stderr)
        return False


def attachment_lines(found: list[tuple[str, bytes] | None], shot_urls: dict[int, str],
                     sealed_urls: dict[int, str]) -> list[str]:
    lines = []
    for n, item in enumerate(found, 1):
        if item is None:
            lines.append(f"_附件 {n} 没取到（ntfy 附件只留 3 小时，或者格式不对）_")
        elif item[0] == "shot":
            url = shot_urls.get(n)
            lines.append(f"![截图 {n}]({url})" if url else f"_截图 {n} 没存上_")
        else:
            url = sealed_urls.get(n)
            lines.append(f"📎 附件 {n}（{_size(len(item[1]))}，只有开发者能看到）" if url else f"_附件 {n} 没存上_")
    return lines


def _size(n: int) -> str:
    return f"{n / 1_000_000:.1f} MB" if n >= 1_000_000 else f"{max(1, n // 1000)} KB"


# ---------------------------------------------------------------- ntfy and GitHub

def poll(server: str, topic: str) -> list[dict]:
    query = urllib.parse.urlencode({"poll": "1", "since": "12h"})
    url = f"{server.rstrip('/')}/{urllib.parse.quote(topic, safe='')}/json?{query}"
    req = urllib.request.Request(url, headers={"Accept": "application/x-ndjson", "User-Agent": "yuyue-feedback"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        raw = resp.read().decode("utf-8", "replace")
    messages = []
    for line in raw.splitlines():
        try:
            item = json.loads(line)
        except ValueError:
            continue
        if isinstance(item, dict) and item.get("event") == "message":
            messages.append(item)
    return messages


def github(method: str, path: str, token: str, body: dict | None = None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(
        "https://api.github.com" + path,
        data=data,
        method=method,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "yuyue-feedback",
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read()
            return resp.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as err:
        return err.code, err.read().decode("utf-8", "replace")


def list_issues(repo: str, token: str, label: str) -> list:
    """The feedback Issues only (by label): the repo's other Issues don't matter here."""
    found = []
    for page in range(1, 51):
        query = urllib.parse.urlencode({"state": "all", "per_page": 100, "page": page, "labels": label})
        status, data = github("GET", f"/repos/{repo}/issues?{query}", token)
        if status != 200 or not isinstance(data, list):
            raise SystemExit(f"读 Issue 失败：HTTP {status}")
        found.extend(data)
        if len(data) < 100:
            break
    return found


def ensure_label(repo: str, token: str, name: str) -> None:
    status, _ = github("POST", f"/repos/{repo}/labels", token,
                       {"name": name, "color": "2f6f5e", "description": "App 里发来的反馈"})
    if status not in (201, 422):  # 422: already there
        print(f"建标签「{name}」失败：HTTP {status}", file=sys.stderr)


def commit_sealed(repo: str, token: str, ntfy_id: str, sealed: list[tuple[int, bytes]]) -> dict[int, str]:
    """The sealed attachments, as they came, at feedback-files/<ntfy-id>/<n>.sealed. Returns n → path."""
    done = {}
    for n, raw in sealed:
        path = f"feedback-files/{ntfy_id}/{n}.sealed"
        status, detail = github("PUT", f"/repos/{repo}/contents/{urllib.parse.quote(path)}", token,
                                {"message": f"反馈附件 {ntfy_id} #{n}（密文）", "content": base64.b64encode(raw).decode()})
        # 422: an earlier run got this far and committed the same file.
        if status in (200, 201, 422):
            done[n] = path
        else:
            print(f"提交附件失败 {path}：HTTP {status} {detail}", file=sys.stderr)
    return done


# ---------------------------------------------------------------- main

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dry-run", action="store_true", help="只打印，不碰 GitHub")
    parser.add_argument("--out", help="--dry-run 时把附件存到这个目录")
    args = parser.parse_args()

    env = lambda name, default="": os.environ.get(name, default).strip()  # noqa: E731
    topic, server = env("NTFY_TOPIC"), env("NTFY_SERVER", "https://ntfy.sh")
    issue_repo, issue_token = env("ISSUE_REPO"), env("ISSUE_TOKEN")
    repo, token = env("GH_REPO"), env("GH_TOKEN")
    shots_repo, shots_dir = env("SHOTS_REPO"), env("SHOTS_DIR")
    label = env("LABEL") or "反馈"
    try:
        max_issues = max(0, int(env("MAX_ISSUES") or 10))
    except ValueError:
        max_issues = 10
    needed = [("NTFY_TOPIC", topic)]
    if not args.dry_run:
        needed += [("ISSUE_REPO", issue_repo), ("ISSUE_TOKEN", issue_token), ("GH_REPO", repo), ("GH_TOKEN", token),
                   ("SHOTS_REPO", shots_repo), ("SHOTS_DIR", shots_dir)]
    missing = [name for name, value in needed if not value]
    if missing:
        print("缺少 " + "、".join(missing), file=sys.stderr)
        return 1
    shots = Shots(shots_repo, shots_dir, env("SHOTS_BRANCH") or "main")

    try:
        messages = poll(server, topic)
    except Exception as err:
        print(f"拉取 ntfy 失败：{err}", file=sys.stderr)
        return 1

    files = {safe_id(str(m.get("id") or "")): found for m in messages if (found := attachment(server, m))}
    issues = [] if args.dry_run else list_issues(issue_repo, issue_token, label)
    seen, hashes = known_ids(issues), known_hashes(issues)
    opened = skipped = junk = failed = held = 0
    labelled = False
    for item in messages:
        ntfy_id = safe_id(str(item.get("id") or ""))
        if not ntfy_id or ntfy_id in files:
            continue
        if ntfy_id in seen:
            skipped += 1
            continue
        data = open_message(str(item.get("message") or ""))
        if data is None:
            junk += 1
            print(f"跳过格式不对的消息 {ntfy_id}")
            continue
        if text_hash(data) in hashes:
            junk += 1
            print(f"跳过重复的反馈 {ntfy_id}")
            continue
        if opened >= max_issues:
            held += 1
            continue
        found = fetch_files(data, files)
        public = [(n, f[1]) for n, f in enumerate(found, 1) if f and f[0] == "shot"]
        sealed = [(n, f[1]) for n, f in enumerate(found, 1) if f and f[0] == "sealed"]

        if args.dry_run:
            shot_urls, sealed_urls = {}, {}
            for kind, items, urls in (("jpg", public, shot_urls), ("sealed", sealed, sealed_urls)):
                for n, raw in items:
                    urls[n] = "（没存）"
                    if args.out:
                        os.makedirs(args.out, exist_ok=True)
                        urls[n] = os.path.join(args.out, f"{ntfy_id}-{n}.{kind}")
                        with open(urls[n], "wb") as handle:
                            handle.write(raw)
            print("—" * 30)
            print(issue_title(data))
            print(issue_body(ntfy_id, data, attachment_lines(found, shot_urls, sealed_urls)))
        else:
            shot_urls = {n: shots.url(f"{ntfy_id}/{n}.jpg") for n, _ in public} if public and shots.add(ntfy_id, public) else {}
            sealed_urls = commit_sealed(repo, token, ntfy_id, sealed) if sealed else {}
            if not labelled:
                ensure_label(issue_repo, issue_token, label)
                labelled = True
            body = issue_body(ntfy_id, data, attachment_lines(found, shot_urls, sealed_urls))
            status, detail = github("POST", f"/repos/{issue_repo}/issues", issue_token,
                                    {"title": issue_title(data), "body": body, "labels": [label]})
            if status not in (200, 201):
                failed += 1
                print(f"开 Issue 失败 {ntfy_id}：HTTP {status} {detail}", file=sys.stderr)
                continue
            print(f"已开 Issue：{issue_title(data)}")
        seen.add(ntfy_id)
        hashes.add(text_hash(data))
        opened += 1

    verb = "会开" if args.dry_run else "新开"
    print(f"完成：{verb} {opened}，已有 {skipped}，跳过 {junk}，留到下次 {held}，失败 {failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
