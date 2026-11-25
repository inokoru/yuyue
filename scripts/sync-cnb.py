#!/usr/bin/env python3
"""将本地 Git 代码、标签以及 dist/release-<版本>/ 发布包同步到 CNB.cool（国内高速镜像）。

特性：
1. 推送 main 分支与对应 Tag 到 https://cnb.cool/<repo>.git（使用临时 HTTP Header 鉴权，不把 Token 写进 .git/config）。
2. 自动生成一份将下载直链改写为 CNB 域名的更新清单（update.json / appcast.xml / appcast-x86_64.xml），
   保留原有的 Sparkle Ed25519 签名与 Android SHA-256 哈希不变，供国内客户端无感高速更新。
3. 通过 CNB OpenAPI（https://api.cnb.cool）创建或更新 Release，并流式上传全部安装包与更新元数据。
4. 上传完成后自动校验匿名直链（/-/releases/download/<tag>/... 与 /-/releases/latest/download/...）可达性。

用法：
  scripts/sync-cnb.py                     # 默认同步当前版本（从 apple/project.yml 读取）
  scripts/sync-cnb.py 0.1.2               # 指定版本（对应 dist/release-0.1.2 与标签 v0.1.2）
  scripts/sync-cnb.py 0.2.0 --beta 1      # 指定测试版（标签 v0.2.0-beta.1，标为 prerelease）
  scripts/sync-cnb.py 0.1.2 --skip-git    # 只传 Release 附件，跳过 git push

令牌读取顺序：
  1. 环境变量 CNB_TOKEN
  2. 文件 ~/.config/yuyue/cnb.token（推荐：chmod 600 ~/.config/yuyue/cnb.token）
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

DEFAULT_CNB_REPO = "inokoru/yuyue"
GITHUB_REPO = "inokoru/yuyue"
API_BASE = "https://api.cnb.cool"
WEB_BASE = "https://cnb.cool"


def read_token(token_file: Path) -> str:
    env_tok = os.environ.get("CNB_TOKEN", "").strip()
    if env_tok:
        return env_tok
    if token_file.is_file():
        tok = token_file.read_text(encoding="utf-8").strip()
        if tok:
            return tok
    sys.exit(
        f"未找到 CNB 访问令牌。\n"
        f"请先保存令牌到 {token_file}（或设置环境变量 CNB_TOKEN）：\n"
        f"  mkdir -p ~/.config/yuyue && printf '%s' '<你的令牌>' > {token_file} && chmod 600 {token_file}\n"
        f"所需令牌权限：repo-code:rw（推代码/Tag）与 repo-release:rw（发版与传附件）。"
    )


def detect_current_version(root: Path) -> str:
    yml = (root / "apple" / "project.yml").read_text(encoding="utf-8")
    m = re.search(r"^\s*MARKETING_VERSION:\s*(\S+)", yml, re.MULTILINE)
    if not m:
        sys.exit("无法从 apple/project.yml 读取 MARKETING_VERSION")
    return m.group(1).strip()


def git_push_cnb(root: Path, cnb_repo: str, tag: str, token: str) -> None:
    remote_url = f"{WEB_BASE}/{cnb_repo}.git"
    basic = base64.b64encode(f"cnb:{token}".encode("utf-8")).decode("ascii")
    auth_cfg = f"http.{WEB_BASE}/.extraheader=AUTHORIZATION: Basic {basic}"

    # 确认本地存在该 tag；若只有工作区提交，至少推当前 HEAD 到 main 并确保 tag 存在。
    tag_check = subprocess.run(
        ["git", "-C", str(root), "rev-parse", "-q", "--verify", f"refs/tags/{tag}"],
        capture_output=True,
        text=True,
    )
    refs = ["HEAD:refs/heads/main"]
    if tag_check.returncode == 0:
        refs.append(f"refs/tags/{tag}:refs/tags/{tag}")
    else:
        print(f"提示：本地未找到 Git 标签 {tag}，将在远端以 main 最新提交创建标签 Release。")

    print(f"=== 推送代码与标签到 {remote_url} ({', '.join(refs)}) ===")
    cmd = ["git", "-C", str(root), "-c", auth_cfg, "push", "--force", remote_url, *refs]
    res = subprocess.run(cmd)
    if res.returncode != 0:
        sys.exit("git push 到 CNB 失败，请检查令牌是否包含 repo-code:rw 权限以及仓库 inokoru/yuyue 是否已创建。")


def api_json(
    method: str,
    url_or_path: str,
    token: str,
    payload: dict | None = None,
    allow_404: bool = False,
) -> dict | list | None:
    url = url_or_path if url_or_path.startswith("http") else f"{API_BASE}{url_or_path}"
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8") if payload is not None else None
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/json",
        "User-Agent": "yuyue-release-sync/1.0",
    }
    if data is not None:
        headers["Content-Type"] = "application/json"

    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read()
            if not raw:
                return {}
            return json.loads(raw.decode("utf-8"))
    except urllib.error.HTTPError as e:
        if allow_404 and e.code == 404:
            return None
        body = e.read().decode("utf-8", errors="replace")
        sys.exit(f"CNB API 请求失败：{method} {url} -> HTTP {e.code}\n{body}")


def prepare_cnb_stage(release_dir: Path, stage_dir: Path, cnb_repo: str, tag: str, version: str = "") -> list[Path]:
    """复制 release_dir 产物到临时目录，并将元数据中的 GitHub 下载地址替换为 CNB 同版本直链。
    若产物目录仍是带版本号的旧文件名（如 Yuyue-0.1.3-android.apk），自动补充无版本号固定名（Yuyue-android.apk），
    使 /-/releases/latest/download/Yuyue-* 永久免改链接直达最新版。
    """
    gh_dl = f"https://github.com/{GITHUB_REPO}/releases/download/{tag}"
    cnb_dl = f"{WEB_BASE}/{cnb_repo}/-/releases/download/{tag}"
    gh_page = f"https://github.com/{GITHUB_REPO}/releases/tag/{tag}"
    cnb_page = f"{WEB_BASE}/{cnb_repo}/-/releases/tag/{tag}"
    gh_blob_cl = f"https://github.com/{GITHUB_REPO}/blob/main/CHANGELOG.md"
    cnb_cl = f"{cnb_dl}/CHANGELOG.md"

    text_meta = {"update.json", "appcast.xml", "appcast-x86_64.xml"}
    staged_files: list[Path] = []

    for src in sorted(release_dir.iterdir()):
        if not src.is_file() or src.name.startswith("."):
            continue
        dst = stage_dir / src.name
        if src.name in text_meta:
            content = src.read_text(encoding="utf-8")
            content = (
                content.replace(gh_dl, cnb_dl)
                .replace(gh_page, cnb_page)
                .replace(gh_blob_cl, cnb_cl)
            )
            dst.write_text(content, encoding="utf-8")
        else:
            shutil.copy2(src, dst)
        staged_files.append(dst)

    if version:
        stable_suffixes = [
            "android.apk",
            "mac-arm64.dmg",
            "mac-x86_64.dmg",
            "ios-unsigned.ipa",
            "windows-x64-Setup.exe",
            "windows-arm64-Setup.exe",
        ]
        for suffix in stable_suffixes:
            versioned_src = release_dir / f"Yuyue-{version}-{suffix}"
            stable_dst = stage_dir / f"Yuyue-{suffix}"
            if versioned_src.is_file() and not stable_dst.exists():
                shutil.copy2(versioned_src, stable_dst)
                staged_files.append(stable_dst)

    return staged_files


def ensure_release(
    cnb_repo: str,
    tag: str,
    title: str,
    body: str,
    prerelease: bool,
    draft: bool,
    token: str,
) -> str:
    make_latest = "false" if (prerelease or draft) else "true"
    existing = api_json("GET", f"/{cnb_repo}/-/releases/tags/{urllib.parse.quote(tag, safe='')}", token, allow_404=True)
    if isinstance(existing, dict) and existing.get("id"):
        rel_id = str(existing["id"])
        print(f"=== 更新已存在的 CNB Release: {tag} (id={rel_id}) ===")
        api_json(
            "PATCH",
            f"/{cnb_repo}/-/releases/{rel_id}",
            token,
            payload={
                "name": title,
                "body": body,
                "draft": draft,
                "prerelease": prerelease,
                "make_latest": make_latest,
            },
        )
        return rel_id

    print(f"=== 创建 CNB Release: {tag} ({title}) ===")
    created = api_json(
        "POST",
        f"/{cnb_repo}/-/releases",
        token,
        payload={
            "tag_name": tag,
            "target_commitish": "main",
            "name": title,
            "body": body,
            "draft": draft,
            "prerelease": prerelease,
            "make_latest": make_latest,
        },
    )
    if not isinstance(created, dict) or not created.get("id"):
        sys.exit(f"创建 Release 返回异常：{created}")
    return str(created["id"])


def upload_asset(cnb_repo: str, release_id: str, file_path: Path, token: str) -> None:
    size = file_path.stat().st_size
    name = file_path.name
    mb = size / (1024 * 1024)
    print(f"  -> 上传 {name} ({mb:.1f} MB) ...")

    info = api_json(
        "POST",
        f"/{cnb_repo}/-/releases/{release_id}/asset-upload-url",
        token,
        payload={
            "asset_name": name,
            "size": size,
            "overwrite": True,
            "ttl": 0,
        },
    )
    if not isinstance(info, dict) or "upload_url" not in info or "verify_url" not in info:
        sys.exit(f"获取上传地址失败：{info}")

    upload_url = info["upload_url"]
    verify_url = info["verify_url"]

    # 使用系统 curl 流式 PUT 上传大文件（自动处理进度、HTTP/2 与重定向，不在内存中整块缓冲）。
    put_cmd = [
        "curl",
        "--fail",
        "--silent",
        "--show-error",
        "--location",
        "-X",
        "PUT",
        "-H",
        "Content-Type: application/octet-stream",
        "--upload-file",
        str(file_path),
        upload_url,
    ]
    res = subprocess.run(put_cmd)
    if res.returncode != 0:
        sys.exit(f"PUT 上传附件失败：{name}")

    # 调用 verify_url 确认上传完成，使附件正式挂载到 Release。
    sep = "&" if "?" in verify_url else "?"
    confirm_url = f"{verify_url}{sep}ttl=0"
    api_json("POST", confirm_url, token)


def verify_public_urls(cnb_repo: str, tag: str, filenames: list[str], check_latest: bool) -> None:
    print("=== 验证 CNB 公开免登录直链 ===")
    sample = [n for n in filenames if n.endswith((".dmg", ".apk", ".ipa", "update.json", "appcast.xml"))]
    for name in sample:
        url = f"{WEB_BASE}/{cnb_repo}/-/releases/download/{tag}/{name}"
        code = subprocess.run(
            ["curl", "-sIL", "-o", "/dev/null", "-w", "%{http_code}", url],
            capture_output=True,
            text=True,
        ).stdout.strip()
        print(f"  [{code}] {url}")

    if check_latest:
        latest_names = [
            n
            for n in filenames
            if n in {
                "update.json",
                "Yuyue-android.apk",
                "Yuyue-mac-arm64.dmg",
                "Yuyue-mac-x86_64.dmg",
                "Yuyue-ios-unsigned.ipa",
            }
        ]
        for name in latest_names:
            latest_url = f"{WEB_BASE}/{cnb_repo}/-/releases/latest/download/{name}"
            code = subprocess.run(
                ["curl", "-sIL", "-o", "/dev/null", "-w", "%{http_code}", latest_url],
                capture_output=True,
                text=True,
            ).stdout.strip()
            print(f"  [{code}] {latest_url}")


def main() -> None:
    parser = argparse.ArgumentParser(description="同步语阅代码与安装包到 CNB.cool Releases")
    parser.add_argument("version", nargs="?", help="版本号（如 0.1.2），默认读取 apple/project.yml")
    parser.add_argument("--beta", help="测试版序号（如 1 对应 v0.2.0-beta.1）")
    parser.add_argument("--repo", default=os.environ.get("CNB_REPO", DEFAULT_CNB_REPO), help="CNB 仓库路径（默认 inokoru/yuyue）")
    parser.add_argument("--token-file", default=str(Path.home() / ".config" / "yuyue" / "cnb.token"), help="CNB 令牌文件路径")
    parser.add_argument("--draft", action="store_true", help="创建为草稿（默认直接正式发布）")
    parser.add_argument("--skip-git", action="store_true", help="跳过 git push，仅上传 Release 附件")
    parser.add_argument("--only", nargs="+", help="仅上传指定文件名的附件（用于增量补传）")
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    version = args.version or detect_current_version(root)
    shown = f"{version}-beta.{args.beta}" if args.beta else version
    tag = f"v{shown}"
    title = f"语阅 {version}" + (f" 测试版 {args.beta}" if args.beta else "")
    prerelease = bool(args.beta)

    release_dir = root / "dist" / f"release-{version}"
    if not release_dir.is_dir():
        sys.exit(f"找不到发布产物目录：{release_dir}\n请先运行 scripts/publish-release.sh 生成该版本产物。")

    notes_file = root / "dist" / f"release-{version}-notes.md"
    body = notes_file.read_text(encoding="utf-8") if notes_file.is_file() else f"语阅 {shown}"

    token = read_token(Path(args.token_file).expanduser())

    if not args.skip_git:
        git_push_cnb(root, args.repo, tag, token)

    with tempfile.TemporaryDirectory(prefix=f"yuyue-cnb-{version}-") as tmp:
        staged = prepare_cnb_stage(release_dir, Path(tmp), args.repo, tag, version)
        if args.only:
            only_set = set(args.only)
            staged = [f for f in staged if f.name in only_set]
        if not staged:
            sys.exit(f"{release_dir} 中没有可上传的文件")

        rel_id = ensure_release(
            cnb_repo=args.repo,
            tag=tag,
            title=title,
            body=body,
            prerelease=prerelease,
            draft=args.draft,
            token=token,
        )

        print(f"=== 上传 {len(staged)} 个发布文件到 {args.repo} @ {tag} ===")
        for f in staged:
            upload_asset(args.repo, rel_id, f, token)

        if not args.draft:
            verify_public_urls(
                args.repo,
                tag,
                [f.name for f in staged],
                check_latest=not prerelease,
            )

    print(f"\n完成！国内发布页地址：{WEB_BASE}/{args.repo}/-/releases")


if __name__ == "__main__":
    main()
