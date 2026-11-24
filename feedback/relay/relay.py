#!/usr/bin/env python3
"""把 ntfy 上的反馈搬进语阅仓库的 Issue（公开的），每条一个。

语阅是开源项目，反馈的正文、标题、设备信息和附件均公开在 GitHub，方便社区一起排查复现；
不收集邮箱或联系方式。

- 截图（消息正文 `v2shot`，附件是 JPEG）：推到公开附件仓库（SHOTS_REPO）的 `<ntfy-id>/<n>.jpg`，
  在 Issue 里直接贴图。
- 普通文件附件（消息正文 `v2file` 或 `v2file <percent-encoded-name>`）：推到公开附件仓库的
  `<ntfy-id>/<n><ext>`，在 Issue 里提供查看与直接下载链接，贡献者可直接下载调试。
- 兼容旧版客户端（0.1.0 / 0.1.1）发来的密封附件（消息正文 `v1file`）：原样提交到私有仓库
  （GH_REPO 的 feedback-files/），Issue 里写「只有开发者能看到」。

ntfy.sh 只缓存大约 12 小时，附件只留 3 小时，所以这个脚本要在那之前跑（由 Worker 每 5 分钟检查并按需唤醒）。
格式不对的消息跳过。已经开过的（正文里有 ntfy-id）不会再开，和已有 Issue 正文一样的也跳过。
话题名打包在 App 里，谁都能往里发，所以每次最多开 MAX_ISSUES 个（默认 10，剩下的下一次）。

环境变量：
  NTFY_TOPIC      话题名
  NTFY_SERVER     默认 https://ntfy.sh
  ISSUE_REPO      开 Issue 的仓库：inokoru/yuyue
  ISSUE_TOKEN     能在 ISSUE_REPO 开 Issue 的 token（fine-grained，只给 Issues 读写）
  GH_REPO         中转私有仓库（存去重账本和旧版密封附件），Actions 里用 github.repository
  GH_TOKEN        能往 GH_REPO 提交的 token，Actions 里用 GITHUB_TOKEN
  SHOTS_REPO      放公开附件的仓库：inokoru/yuyue-feedback-assets
  SHOTS_DIR       它的本地检出（工作流用部署密钥 clone 下来），推送也用那把密钥
  SHOTS_BRANCH    默认 main
  MAX_ISSUES      每次最多新开几个，默认 10
  LABEL           反馈 Issue 的标签，默认「反馈」

本地试：python relay.py --dry-run --out 目录
  只要 NTFY_TOPIC，不碰 GitHub；打印会开的 Issue，附件存进目录。
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
SEEN_RETENTION_SECONDS = 48 * 3600
SEEN_PATH = "feedback-files/seen.json"
SEALED_MARK = "v1file"
SHOT_MARK = "v2shot"
FILE_MARK = "v2file"
FILES_MAX = 3
TITLE_MAX = 80           # the app caps at 60; a bit of room, then junk
TEXT_MAX = 1000          # the app caps at 800; a bit of room, then junk
LEGACY_TITLE_LIMIT = 120
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
    raw_title = data.get("title")
    if raw_title is not None:
        if not isinstance(raw_title, str):
            return None
        title = clean_title(raw_title)
        if len(title) > TITLE_MAX:
            return None
        data["title"] = title
    else:
        data["title"] = ""
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


def clean_title(text: str) -> str:
    """Single-line Issue title: strip control characters and collapse whitespace, keeping user punctuation."""
    text = re.sub(r"[\x00-\x1f\x7f]", " ", text)
    return " ".join(text.split())


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
    explicit = clean_title(str(data.get("title") or ""))
    if explicit:
        return f"[{label}] {explicit}"
    # Legacy clients (0.1.0 / 0.1.1) without a dedicated title field: avoid awkward 40-char cuts.
    raw = str(data.get("text", "")).strip()
    first_line = clean_title(raw.splitlines()[0] if raw else "")
    if not first_line:
        first_line = clean_title(raw)
    if len(first_line) <= LEGACY_TITLE_LIMIT:
        return f"[{label}] {first_line}"
    # Prefer stopping cleanly at the first sentence ending within the limit.
    for match in re.finditer(r"[。！？]|[.!?](?=\s)", first_line[:LEGACY_TITLE_LIMIT]):
        if match.end() >= 12:
            return f"[{label}] {first_line[:match.end()].rstrip('。')}"
    return f"[{label}] {first_line[:LEGACY_TITLE_LIMIT]}…"


def public_text(text: str) -> str:
    """The person's words as Markdown, minus what would break out of it: HTML comments
    (where the markers live) and @mentions (GitHub would notify whoever is named)."""
    text = text.strip().replace("<!--", "&lt;!--").replace("-->", "--&gt;")
    return re.sub(r"(?<![\w`])@(?=[A-Za-z0-9-])", "@​", text)


def issue_body(ntfy_id: str, data: dict, lines: list[str] | None = None) -> str:
    """`lines`: one Markdown line per attachment (a picture, a public file link, or a legacy private note)."""
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

def safe_filename(name: str) -> str:
    """Display filename stripped of paths and control chars."""
    name = (name or "").replace("\\", "/").rsplit("/", 1)[-1]
    name = re.sub(r"[\x00-\x1f\x7f]", "", name).strip(" .")
    return name[:100] or "file"


def safe_ext(name: str) -> str:
    """Safe ASCII extension (e.g. `.mobi`, `.txt`), or `.bin` when absent/unsafe."""
    base = safe_filename(name)
    if "." in base:
        ext = base.rsplit(".", 1)[-1].lower()
        if re.fullmatch(r"[a-z0-9]{1,10}", ext):
            return "." + ext
    return ".bin"


def escape_md_label(text: str) -> str:
    """Escape characters that could break out of a Markdown link label."""
    return re.sub(r"([\\\[\]()*_`<>])", r"\\\1", text)


def attachment(server: str, item: dict) -> tuple[str, str, str] | None:
    """("shot" | "file" | "sealed", url, display_name) for an attachment message hosted by ntfy."""
    mark = str(item.get("message") or "").strip()
    found = item.get("attachment")
    url = found.get("url") if isinstance(found, dict) else None
    if not isinstance(url, str) or not url.startswith(server.rstrip("/") + "/file/"):
        return None
    if mark == SHOT_MARK:
        return "shot", url, ""
    if mark == SEALED_MARK:
        return "sealed", url, ""
    if mark == FILE_MARK or mark.startswith(FILE_MARK + " "):
        encoded = mark[len(FILE_MARK):].strip()
        decoded = urllib.parse.unquote(encoded) if encoded else ""
        fallback = found.get("name") if isinstance(found, dict) and isinstance(found.get("name"), str) else ""
        return "file", url, safe_filename(decoded or fallback or "file")
    return None


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


def fetch_files(data: dict, files: dict[str, tuple[str, str, str]]) -> list[tuple[str, bytes, str] | None]:
    """Per listed id: (kind, bytes, name), or None when it is gone (ntfy keeps a file 3 hours) or bad."""
    found: list[tuple[str, bytes, str] | None] = []
    for file_id in data["files"]:
        entry = files.get(file_id)
        if not entry:
            found.append(None)
            continue
        kind, url, name = entry
        raw = download(url) if url else None
        if raw is None or (kind == "shot" and not is_jpeg(raw)):
            found.append(None)
        else:
            found.append((kind, raw, name))
    return found


class Shots:
    """The public attachments repo, checked out at `path`; pushed with the deploy key the workflow set up."""

    def __init__(self, repo: str, path: str, branch: str = "main"):
        self.repo, self.path, self.branch = repo, path, branch

    def url(self, rel: str) -> str:
        return f"https://raw.githubusercontent.com/{self.repo}/{urllib.parse.quote(self.branch)}/{urllib.parse.quote(rel)}"

    def blob_url(self, rel: str) -> str:
        return f"https://github.com/{self.repo}/blob/{urllib.parse.quote(self.branch)}/{urllib.parse.quote(rel)}"

    def git(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run(["git", "-C", self.path, *args], capture_output=True, text=True)

    def add(self, ntfy_id: str, shots: list[tuple[int, bytes]]) -> bool:
        """Commit and push one report's screenshots as `<ntfy-id>/<n>.jpg`."""
        return self.add_files(ntfy_id, [(f"{n}.jpg", raw) for n, raw in shots])

    def add_files(self, ntfy_id: str, items: list[tuple[str, bytes]]) -> bool:
        """Commit and push one report's public attachments as `<ntfy-id>/<filename>`."""
        rels = []
        for filename, raw in items:
            rel = f"{ntfy_id}/{filename}"
            os.makedirs(os.path.join(self.path, ntfy_id), exist_ok=True)
            with open(os.path.join(self.path, rel), "wb") as handle:
                handle.write(raw)
            rels.append(rel)
        self.git("add", "--", *rels)
        if self.git("diff", "--cached", "--quiet").returncode != 0:  # nothing staged: pushed by an earlier run
            commit = self.git("-c", "user.name=yuyue-feedback", "-c", "user.email=feedback@users.noreply.github.com",
                              "commit", "-q", "-m", f"反馈附件 {ntfy_id}")
            if commit.returncode != 0:
                print(f"提交附件失败 {ntfy_id}：{commit.stderr.strip()}", file=sys.stderr)
                return False
        for _ in range(2):
            push = self.git("push", "-q", "origin", f"HEAD:{self.branch}")
            if push.returncode == 0:
                return True
            self.git("pull", "-q", "--rebase", "origin", self.branch)
        print(f"推送附件失败 {ntfy_id}：{push.stderr.strip()}", file=sys.stderr)
        return False


def attachment_lines(
    found: list[tuple[str, bytes, str] | None],
    shot_urls: dict[int, str],
    file_links: dict[int, tuple[str, str]] | None = None,
    sealed_urls: dict[int, str] | None = None,
) -> list[str]:
    file_links = file_links or {}
    sealed_urls = sealed_urls or {}
    lines = []
    for n, item in enumerate(found, 1):
        if item is None:
            lines.append(f"_附件 {n} 没取到（ntfy 附件只留 3 小时，或者格式不对）_")
        elif item[0] == "shot":
            url = shot_urls.get(n)
            lines.append(f"![截图 {n}]({url})" if url else f"_截图 {n} 没存上_")
        elif item[0] == "file":
            links = file_links.get(n)
            if links:
                blob_url, raw_url = links
                label = escape_md_label(item[2] or f"附件 {n}")
                lines.append(f"📎 附件 {n}：[{label}]({blob_url})（{_size(len(item[1]))} · [直接下载]({raw_url})）")
            else:
                lines.append(f"_附件 {n} 没存上_")
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
    """Legacy sealed attachments (0.1.0 / 0.1.1), at feedback-files/<ntfy-id>/<n>.sealed. Returns n → path."""
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


def parse_seen_ledger(raw_ledger, now: float | None = None) -> tuple[dict[str, int], dict[str, int]]:
    """Pruned {ntfy_id: unix_sec} (48h) and {text_hash: unix_sec} (24h) so deleted Issues stay dead."""
    ts_now = int(time.time() if now is None else now)
    id_cutoff = ts_now - SEEN_RETENTION_SECONDS
    hash_cutoff = ts_now - DEDUP_WINDOW_SECONDS
    ids: dict[str, int] = {}
    hashes: dict[str, int] = {}
    if isinstance(raw_ledger, dict):
        raw_ids = raw_ledger.get("ids")
        if isinstance(raw_ids, dict):
            for k, v in raw_ids.items():
                sid = safe_id(str(k))
                if sid and isinstance(v, (int, float)) and int(v) >= id_cutoff:
                    ids[sid] = int(v)
        raw_hashes = raw_ledger.get("hashes")
        if isinstance(raw_hashes, dict):
            for k, v in raw_hashes.items():
                h = str(k)
                if re.fullmatch(r"[0-9a-f]{16}", h) and isinstance(v, (int, float)) and int(v) >= hash_cutoff:
                    hashes[h] = int(v)
    return ids, hashes


def load_seen_ledger(repo: str, token: str) -> tuple[dict[str, int], dict[str, int], str | None]:
    status, data = github("GET", f"/repos/{repo}/contents/{urllib.parse.quote(SEEN_PATH)}", token)
    if status == 404 or not isinstance(data, dict):
        return {}, {}, None
    if status != 200:
        print(f"读取 {SEEN_PATH} 失败：HTTP {status}", file=sys.stderr)
        return {}, {}, None
    try:
        decoded = json.loads(base64.b64decode(data.get("content") or "").decode("utf-8"))
    except (ValueError, TypeError):
        decoded = {}
    ids, hashes = parse_seen_ledger(decoded)
    return ids, hashes, data.get("sha")


def save_seen_ledger(repo: str, token: str, ids: dict[str, int], hashes: dict[str, int], sha: str | None) -> None:
    payload = json.dumps({"ids": dict(sorted(ids.items())), "hashes": dict(sorted(hashes.items()))}, indent=2) + "\n"
    body: dict = {
        "message": "chore: record processed feedback ids",
        "content": base64.b64encode(payload.encode("utf-8")).decode(),
    }
    if sha:
        body["sha"] = sha
    status, detail = github("PUT", f"/repos/{repo}/contents/{urllib.parse.quote(SEEN_PATH)}", token, body)
    if status not in (200, 201):
        print(f"写入 {SEEN_PATH} 失败：HTTP {status} {detail}", file=sys.stderr)


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
    ledger_ids, ledger_hashes, ledger_sha = ({}, {}, None) if args.dry_run else load_seen_ledger(repo, token)
    before_ids, before_hashes = dict(ledger_ids), dict(ledger_hashes)
    now_sec = int(time.time())
    seen = known_ids(issues) | set(ledger_ids)
    hashes = known_hashes(issues) | set(ledger_hashes)
    opened = skipped = junk = failed = held = 0
    labelled = False
    for item in messages:
        ntfy_id = safe_id(str(item.get("id") or ""))
        if not ntfy_id or ntfy_id in files:
            continue
        if ntfy_id in seen:
            ledger_ids.setdefault(ntfy_id, now_sec)
            skipped += 1
            continue
        data = open_message(str(item.get("message") or ""))
        if data is None:
            junk += 1
            print(f"跳过格式不对的消息 {ntfy_id}")
            continue
        h = text_hash(data)
        if h in hashes:
            ledger_ids.setdefault(ntfy_id, now_sec)
            ledger_hashes.setdefault(h, now_sec)
            junk += 1
            print(f"跳过重复的反馈 {ntfy_id}")
            continue
        if opened >= max_issues:
            held += 1
            continue
        found = fetch_files(data, files)
        public_assets: list[tuple[int, str, str, bytes]] = []  # (n, kind, filename, raw)
        sealed: list[tuple[int, bytes]] = []
        for n, f in enumerate(found, 1):
            if not f:
                continue
            kind, raw, name = f
            if kind == "shot":
                public_assets.append((n, "shot", f"{n}.jpg", raw))
            elif kind == "file":
                public_assets.append((n, "file", f"{n}{safe_ext(name)}", raw))
            else:
                sealed.append((n, raw))

        if args.dry_run:
            shot_urls: dict[int, str] = {}
            file_links: dict[int, tuple[str, str]] = {}
            sealed_urls: dict[int, str] = {}
            for n, kind, filename, raw in public_assets:
                dest = "（没存）"
                if args.out:
                    os.makedirs(args.out, exist_ok=True)
                    dest = os.path.join(args.out, f"{ntfy_id}-{filename}")
                    with open(dest, "wb") as handle:
                        handle.write(raw)
                if kind == "shot":
                    shot_urls[n] = dest
                else:
                    file_links[n] = (dest, dest)
            for n, raw in sealed:
                dest = "（没存）"
                if args.out:
                    os.makedirs(args.out, exist_ok=True)
                    dest = os.path.join(args.out, f"{ntfy_id}-{n}.sealed")
                    with open(dest, "wb") as handle:
                        handle.write(raw)
                sealed_urls[n] = dest
            print("—" * 30)
            print(issue_title(data))
            print(issue_body(ntfy_id, data, attachment_lines(found, shot_urls, file_links, sealed_urls)))
        else:
            shot_urls = {}
            file_links = {}
            if public_assets and shots.add_files(ntfy_id, [(fn, raw) for _, _, fn, raw in public_assets]):
                for n, kind, fn, _ in public_assets:
                    rel = f"{ntfy_id}/{fn}"
                    if kind == "shot":
                        shot_urls[n] = shots.url(rel)
                    else:
                        file_links[n] = (shots.blob_url(rel), shots.url(rel))
            sealed_urls = commit_sealed(repo, token, ntfy_id, sealed) if sealed else {}
            if not labelled:
                ensure_label(issue_repo, issue_token, label)
                labelled = True
            body = issue_body(ntfy_id, data, attachment_lines(found, shot_urls, file_links, sealed_urls))
            status, detail = github("POST", f"/repos/{issue_repo}/issues", issue_token,
                                    {"title": issue_title(data), "body": body, "labels": [label]})
            if status not in (200, 201):
                failed += 1
                print(f"开 Issue 失败 {ntfy_id}：HTTP {status} {detail}", file=sys.stderr)
                continue
            print(f"已开 Issue：{issue_title(data)}")
            ledger_ids[ntfy_id] = now_sec
            ledger_hashes[h] = now_sec
        seen.add(ntfy_id)
        hashes.add(h)
        opened += 1

    if not args.dry_run and (ledger_ids != before_ids or ledger_hashes != before_hashes):
        save_seen_ledger(repo, token, ledger_ids, ledger_hashes, ledger_sha)

    verb = "会开" if args.dry_run else "新开"
    print(f"完成：{verb} {opened}，已有 {skipped}，跳过 {junk}，留到下次 {held}，失败 {failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
