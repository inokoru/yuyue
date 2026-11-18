#!/usr/bin/env python3
"""语阅反馈在这台 Mac 上的一头：Issue 进 Kaneo，Kaneo 里的进度回到 Issue。

1. 导入：语阅仓库里打了「反馈」标签的 Issue，一条建一张 Kaneo 任务。
   私钥只在这台 Mac 上，所以邮箱和「只有开发者能看到」的附件在这里解开：
   邮箱写进任务描述；附件从私有仓库（FILES_REPO 的 feedback-files/<ntfy-id>/）下载、
   解密，存到 FILES_DIR/<Issue 编号>/，路径写进任务描述。都只留在本机。
2. 回写：任务挪到进行中的列，Issue 打上「处理中」；挪到完成的列（Kaneo 里标了 isFinal 的），
   Issue 留言并关闭；从完成挪回来，Issue 重新打开。只在任务换列时动，上次的状态记在
   ~/.config/yuyue/feedback-sync.json，所以你在 GitHub 上手动开关 Issue 不会被改回去。

不使用 Kaneo 的 GitHub App：它的实时同步要求公网回调地址，这台 Mac 没有。

幂等：任务描述第一行是 `yuyue-feedback-issue:<编号>`。已经有这行的不再创建。
删掉 Kaneo 里的任务后，下次运行会再导入一次。

配置放在 ~/.config/yuyue/feedback.env（权限 600），~/.config/yuyue/feedback-keys.env 也会读
（keygen.py 的输出放那里）。同名环境变量优先：

  FEEDBACK_PRIVATE_KEY  私钥。解邮箱和附件要它，没有就只导入公开的部分
  GITHUB_TOKEN       不填就用 `gh auth token`。要能读 FILES_REPO、写 GITHUB_REPO 的 Issue
  GITHUB_REPO        默认 inokoru/yuyue
  FILES_REPO         默认 inokoru/yuyue-feedback（私有，放密封附件）
  FILES_DIR          默认 ~/Library/Application Support/yuyue-feedback
  FEEDBACK_LABEL     默认「反馈」
  KANEO_BASE         例如 http://127.0.0.1:5173/api   （浏览器地址后面加 /api）
  KANEO_API_KEY      Kaneo 账户里创建的 API key，需要能读项目、创建任务
  KANEO_PROJECT_ID   项目 ID（打开项目时 URL 里的那一段）
  KANEO_STATUS       可选，新任务放进哪一列（slug）。不填就用第一列未完成的列

依赖：pip install pynacl（只有解密要用）

用法：
  python3 kaneo_import.py              # 导入并回写
  python3 kaneo_import.py --dry-run    # 只打印会做什么
  python3 kaneo_import.py --no-sync    # 只导入，不回写 Issue
  python3 kaneo_import.py --contact < Issue 正文   # 打印里面加密的邮箱
  python3 kaneo_import.py --open 1.sealed [目录]   # 解开一个密封附件
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

MARKER_RE = re.compile(r"yuyue-feedback-issue:(\d+)")
NTFY_ID_RE = re.compile(r"ntfy-id:\s*([A-Za-z0-9_-]+)")
CONTACT_RE = re.compile(r"<!--\s*contact:\s*v1\.([A-Za-z0-9+/=]+)\s*-->")
PRIORITY = {"问题": "high", "建议": "medium", "其他": "low"}
FILE_MAGIC = b"YYF1"
WORKING = "处理中"
DONE_NOTE = "已经处理好了，谢谢反馈。之后的版本会带上。"
STATE_PATH = os.path.expanduser("~/.config/yuyue/feedback-sync.json")


def load_env_files() -> None:
    for name in ("feedback.env", "feedback-keys.env"):
        path = os.path.expanduser(f"~/.config/yuyue/{name}")
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                key, value = line.split("=", 1)
                os.environ.setdefault(key.strip(), value.strip().strip('"').strip("'"))


def github_token() -> str:
    token = os.environ.get("GITHUB_TOKEN", "").strip()
    if token:
        return token
    try:
        out = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True, timeout=10)
        return out.stdout.strip() if out.returncode == 0 else ""
    except (OSError, subprocess.SubprocessError):
        return ""


def request(method: str, url: str, token: str, body: dict | None = None, github: bool = False):
    data = None if body is None else json.dumps(body).encode()
    headers = {"Accept": "application/json", "User-Agent": "yuyue-feedback"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if github:
        headers["Accept"] = "application/vnd.github+json"
        headers["X-GitHub-Api-Version"] = "2022-11-28"
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read()
            return resp.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as err:
        detail = err.read().decode("utf-8", "replace")
        if err.code == 429:
            wait = int(err.headers.get("Retry-After") or "2")
            time.sleep(min(wait, 30))
        return err.code, detail


def gh(method: str, path: str, token: str, body: dict | None = None):
    return request(method, "https://api.github.com" + path, token, body, github=True)


# ---------------------------------------------------------------- decryption (this Mac only)

def secret_key(secret_b64: str):
    if not secret_b64:
        return None
    try:
        from nacl.public import PrivateKey

        return PrivateKey(base64.b64decode(re.sub(r"\s+", "", secret_b64)))
    except Exception:
        return None


def unseal(secret, raw: bytes) -> bytes | None:
    if secret is None:
        return None
    try:
        from nacl.public import SealedBox

        return SealedBox(secret).decrypt(raw)
    except Exception:
        return None


def open_contact(secret, body: str) -> str | None:
    """The email the app sealed into the public Issue."""
    match = CONTACT_RE.search(body or "")
    if not match:
        return None
    try:
        plain = unseal(secret, base64.b64decode(match.group(1)))
        return plain.decode("utf-8") if plain else None
    except Exception:
        return None


def open_file(secret, sealed: bytes) -> tuple[str, bytes] | None:
    """(name, bytes) inside one sealed attachment: `YYF1`, name length (u16 BE), name, file."""
    plain = unseal(secret, sealed)
    if not plain or len(plain) < 6 or not plain.startswith(FILE_MAGIC):
        return None
    size = int.from_bytes(plain[4:6], "big")
    try:
        name = plain[6 : 6 + size].decode("utf-8")
    except UnicodeDecodeError:
        return None
    body = plain[6 + size :]
    return (safe_name(name), body) if body else None


def safe_name(name: str) -> str:
    name = name.replace("\\", "/").rsplit("/", 1)[-1]
    name = re.sub(r"[\x00-\x1f:]", "", name).strip(" .")
    return name[:100] or "file"


def private_files(files_repo: str, token: str, secret, body: str, number: int, out_root: str,
                  dry_run: bool = False) -> list[str]:
    """Download and open this Issue's sealed attachments; returns one line per file for the task."""
    match = NTFY_ID_RE.search(body or "")
    if not match or not token:
        return []
    status, listing = gh("GET", f"/repos/{files_repo}/contents/feedback-files/{match.group(1)}", token)
    if status != 200 or not isinstance(listing, list):
        return []
    lines = []
    for entry in sorted(listing, key=lambda e: e.get("name", "")):
        if not str(entry.get("name", "")).endswith(".sealed"):
            continue
        n = entry["name"].split(".", 1)[0]
        if secret is None:
            lines.append(f"- 附件 {n}：没有私钥，解不开")
            continue
        if dry_run:
            lines.append(f"- 附件 {n}：会解开")
            continue
        status, blob = gh("GET", f"/repos/{files_repo}/git/blobs/{entry['sha']}", token)
        raw = base64.b64decode(blob.get("content", "")) if status == 200 and isinstance(blob, dict) else b""
        opened = open_file(secret, raw) if raw else None
        if opened is None:
            lines.append(f"- 附件 {n}：解不开")
            continue
        name, data = opened
        folder = os.path.join(out_root, str(number))
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, f"{n}-{name}")
        with open(path, "wb") as handle:
            handle.write(data)
        lines.append(f"- 附件 {n}：{path}")
    return lines


# ---------------------------------------------------------------- import

def github_issues(repo: str, token: str, label: str) -> list[dict]:
    found = []
    for page in range(1, 51):
        query = urllib.parse.urlencode({"state": "all", "per_page": 100, "page": page, "labels": label})
        status, data = gh("GET", f"/repos/{repo}/issues?{query}", token)
        if status != 200 or not isinstance(data, list):
            raise SystemExit(f"读 GitHub Issue 失败：HTTP {status}")
        found.extend(item for item in data if "pull_request" not in item)
        if len(data) < 100:
            break
    return found


def kaneo_columns(base: str, project: str, token: str) -> list[dict]:
    status, data = request("GET", f"{base}/column/{project}", token)
    if status != 200 or not isinstance(data, list) or not data:
        raise SystemExit(f"读 Kaneo 列失败：HTTP {status}。确认 KANEO_BASE 以 /api 结尾，项目 ID 正确。")
    return sorted(data, key=lambda col: col.get("position") or 0)


def column_slug(columns: list[dict]) -> str:
    chosen = os.environ.get("KANEO_STATUS", "").strip()
    if chosen:
        return chosen
    open_columns = [col for col in columns if not col.get("isFinal")]
    col = (open_columns or columns)[0]
    slug = col.get("slug") or col.get("id")
    if not slug:
        raise SystemExit("Kaneo 没有可用的列")
    return slug


def kaneo_tasks(base: str, project: str, token: str) -> dict[int, str | None]:
    """Issue number → the status (column slug) of its task; None for an archived task."""
    found: dict[int, str | None] = {}
    for page in range(1, 51):
        status, data = request("GET", f"{base}/task/tasks/{project}?limit=100&page={page}", token)
        if status != 200 or not isinstance(data, dict):
            raise SystemExit(f"读 Kaneo 任务失败：HTTP {status}")
        board = data.get("data") or {}
        for col in board.get("columns") or []:
            for task in col.get("tasks") or []:
                for n in MARKER_RE.findall(task.get("description") or ""):
                    found[int(n)] = task.get("status") or col.get("slug")
        for key in ("archivedTasks", "plannedTasks"):
            for task in board.get(key) or []:
                for n in MARKER_RE.findall(task.get("description") or ""):
                    found.setdefault(int(n), task.get("status") if key == "plannedTasks" else None)
        total = int((data.get("pagination") or {}).get("totalPages") or 1)
        if page >= total:
            break
    return found


def priority_for(body: str) -> str:
    for label, priority in PRIORITY.items():
        if f"**类别**：{label}" in body:
            return priority
    return "low"


def task_description(repo: str, number: int, body: str, email: str | None = None,
                     files: list[str] | None = None) -> str:
    text = re.sub(r"<!--.*?-->", "", body or "", flags=re.S).strip()
    if email:
        text = text.replace("留了邮箱（加密，只有开发者能看到）", email, 1)
    if files:
        text += "\n\n**本机附件**（解密后只存在这台 Mac 上）：\n" + "\n".join(files)
    return f"yuyue-feedback-issue:{number}\nhttps://github.com/{repo}/issues/{number}\n\n{text}\n"


# ---------------------------------------------------------------- Kaneo → Issue

def stage(slug: str | None, columns: list[dict]) -> str:
    """`new` (the first open column), `working` (any other open one) or `done` (a final one)."""
    if slug is None:
        return "new"
    col = next((c for c in columns if c.get("slug") == slug), None)
    if col and col.get("isFinal"):
        return "done"
    first = next((c.get("slug") for c in columns if not c.get("isFinal")), None)
    return "new" if slug == first or col is None else "working"


def sync_actions(before: str | None, now: str, issue_open: bool) -> list[str]:
    """What to do to the Issue when its task went from `before` to `now`. Nothing when it didn't move."""
    if before == now:
        return []
    if now == "done":
        return ["unlabel", "close"] if issue_open else ["unlabel"]
    actions = [] if issue_open else ["reopen"]
    actions.append("label" if now == "working" else "unlabel")
    if before is None and now == "new":
        return []  # first sight of a fresh task: nothing has happened yet
    return actions


def apply_actions(repo: str, token: str, number: int, actions: list[str], labels: set[str]) -> bool:
    ok = True
    for action in actions:
        if action == "label" and WORKING not in labels:
            status, _ = gh("POST", f"/repos/{repo}/issues/{number}/labels", token, {"labels": [WORKING]})
        elif action == "unlabel" and WORKING in labels:
            status, _ = gh("DELETE", f"/repos/{repo}/issues/{number}/labels/{urllib.parse.quote(WORKING)}", token)
        elif action == "close":
            gh("POST", f"/repos/{repo}/issues/{number}/comments", token, {"body": DONE_NOTE})
            status, _ = gh("PATCH", f"/repos/{repo}/issues/{number}", token, {"state": "closed", "state_reason": "completed"})
        elif action == "reopen":
            status, _ = gh("PATCH", f"/repos/{repo}/issues/{number}", token, {"state": "open"})
        else:
            continue
        if status not in (200, 201, 204):
            print(f"回写 #{number} 失败（{action}）：HTTP {status}", file=sys.stderr)
            ok = False
    return ok


def load_state() -> dict[str, str]:
    try:
        with open(STATE_PATH, encoding="utf-8") as handle:
            data = json.load(handle)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def save_state(state: dict[str, str]) -> None:
    os.makedirs(os.path.dirname(STATE_PATH), exist_ok=True)
    tmp = STATE_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump(state, handle, ensure_ascii=False, indent=1, sort_keys=True)
    os.replace(tmp, STATE_PATH)


# ---------------------------------------------------------------- main

def main() -> int:
    parser = argparse.ArgumentParser(description="语阅反馈：Issue 导入本机 Kaneo，进度回写 Issue")
    parser.add_argument("--dry-run", action="store_true", help="只打印，不创建任务、不改 Issue")
    parser.add_argument("--no-sync", action="store_true", help="不把 Kaneo 的进度回写到 Issue")
    parser.add_argument("--contact", action="store_true", help="从标准输入读 Issue 正文，打印里面加密的邮箱")
    parser.add_argument("--open", nargs="+", metavar=("SEALED", "DIR"), help="解开一个密封附件，存到目录（默认当前目录）")
    args = parser.parse_args()
    load_env_files()
    secret = secret_key(os.environ.get("FEEDBACK_PRIVATE_KEY", "").strip())

    if args.contact or args.open:
        if secret is None:
            print("要 FEEDBACK_PRIVATE_KEY（和 pip install pynacl）", file=sys.stderr)
            return 1
        if args.contact:
            email = open_contact(secret, sys.stdin.read())
            print(email or "这条没有留邮箱，或者解不开")
            return 0 if email else 1
        with open(args.open[0], "rb") as handle:
            opened = open_file(secret, handle.read())
        if opened is None:
            print("解不开", file=sys.stderr)
            return 1
        path = os.path.join(args.open[1] if len(args.open) > 1 else ".", opened[0])
        with open(path, "wb") as handle:
            handle.write(opened[1])
        print(path)
        return 0

    token = github_token()
    repo = os.environ.get("GITHUB_REPO", "inokoru/yuyue").strip()
    files_repo = os.environ.get("FILES_REPO", "inokoru/yuyue-feedback").strip()
    files_dir = os.path.expanduser(os.environ.get("FILES_DIR", "~/Library/Application Support/yuyue-feedback").strip())
    label = os.environ.get("FEEDBACK_LABEL", "").strip() or "反馈"
    base = os.environ.get("KANEO_BASE", "").rstrip("/")
    kaneo = os.environ.get("KANEO_API_KEY", "").strip()
    project = os.environ.get("KANEO_PROJECT_ID", "").strip()
    missing = [name for name, value in (("KANEO_BASE", base), ("KANEO_API_KEY", kaneo), ("KANEO_PROJECT_ID", project)) if not value]
    if missing:
        print("还缺：" + "、".join(missing) + "。写在 ~/.config/yuyue/feedback.env", file=sys.stderr)
        return 2
    if secret is None:
        print("没有私钥（FEEDBACK_PRIVATE_KEY）或没装 pynacl：邮箱和私密附件这次不解开。", file=sys.stderr)

    issues = [item for item in github_issues(repo, token, label) if "ntfy-id:" in (item.get("body") or "")]
    columns = kaneo_columns(base, project, kaneo)
    tasks = kaneo_tasks(base, project, kaneo)
    status_slug = column_slug(columns)
    created = skipped = synced = 0
    failed = False
    for issue in issues:
        number = int(issue["number"])
        if number in tasks:
            skipped += 1
            continue
        body = issue.get("body") or ""
        title = (issue.get("title") or f"反馈 #{number}")[:200]
        files = private_files(files_repo, token, secret, body, number, files_dir, args.dry_run)
        description = task_description(repo, number, body, open_contact(secret, body), files)
        priority = priority_for(body)
        if args.dry_run:
            print(f"将创建：{title}  →  {status_slug} / {priority}" + (f"，附件 {len(files)} 个" if files else ""))
            created += 1
            continue
        code, detail = request("POST", f"{base}/task/{project}", kaneo,
                               {"title": title, "description": description, "priority": priority, "status": status_slug})
        if code != 200:
            print(f"创建任务失败 #{number}：HTTP {code} {detail}", file=sys.stderr)
            return 1
        tasks[number] = status_slug
        created += 1
        print(f"已导入 #{number} {title}")

    if not args.no_sync:
        state = load_state()
        by_number = {int(i["number"]): i for i in issues}
        for number, slug in sorted(tasks.items()):
            issue = by_number.get(number)
            if issue is None or slug is None:
                continue  # not a feedback Issue any more, or the task is archived
            now = stage(slug, columns)
            actions = sync_actions(state.get(str(number)), now, issue.get("state") == "open")
            if actions:
                labels = {lab.get("name") for lab in issue.get("labels") or [] if isinstance(lab, dict)}
                if args.dry_run:
                    print(f"将回写 #{number}：{'、'.join(actions)}")
                elif not token or not apply_actions(repo, token, number, actions, labels):
                    failed = True
                    continue  # try again next time
                synced += 1
            if not args.dry_run:
                state[str(number)] = now
        if not args.dry_run:
            save_state(state)

    print(f"完成：新建 {created}，已在 Kaneo 里 {skipped}，回写 {synced}。新任务的列：{status_slug}")
    if failed and not token:
        print("回写要 GitHub token：填 GITHUB_TOKEN，或者先 gh auth login", file=sys.stderr)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
