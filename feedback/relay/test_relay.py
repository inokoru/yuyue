#!/usr/bin/env python3
"""离线检查：v2 消息的校验、Issue 正文、公开和私密附件、Mac 上的解密和回写规则。"""

import json
import os
import subprocess
import tempfile
import unittest

try:
    from nacl.public import PrivateKey, SealedBox
except ImportError:  # pragma: no cover - optional local crypto dep
    PrivateKey = SealedBox = None

try:
    import kaneo_import
except ImportError:  # pragma: no cover - local-only gitignored script
    kaneo_import = None

import relay

JPEG = b"\xff\xd8\xff\xe0" + b"\x00" * 20 + b"\xff\xd9"


class RelayTest(unittest.TestCase):
    def setUp(self):
        if PrivateKey is not None:
            self.secret = PrivateKey.generate()
            self.box = SealedBox(self.secret.public_key)
        else:
            self.secret = self.box = None

    def message(self, **fields) -> str:
        return json.dumps({"v": 2, "category": "bug", "text": "翻页卡住", **fields}, ensure_ascii=False)

    def test_message_checks(self):
        opened = relay.open_message(self.message(title="  翻页卡住  ", platform="ios", os="iOS 18", app="0.1.0 (1)"))
        self.assertEqual(opened["title"], "翻页卡住")
        self.assertEqual(opened["text"], "翻页卡住")
        self.assertEqual(opened["platform"], "ios")
        self.assertEqual(relay.issue_title(opened), "[问题] 翻页卡住")
        # Dedicated title up to 60 chars is never truncated.
        full_title = "mobi文件无法导入，其他文件能导入但一点击阅读就闪退到桌面，包括txt文件（荣耀Magic7 MagicOS11）"
        self.assertLessEqual(len(full_title), 60)
        opened_full = relay.open_message(self.message(title=full_title, text="详细说明"))
        self.assertEqual(relay.issue_title(opened_full), f"[问题] {full_title}")
        # Legacy clients without a title field do not get cut mid-sentence at 40 chars.
        legacy_text = "mobi文件无法导入，其他文件能导入但一点击阅读就闪退到桌面，包括txt文件。不知道是哪里出了问题，手机型号是荣耀magic7。"
        legacy = relay.open_message(self.message(text=legacy_text))
        self.assertEqual(relay.issue_title(legacy), f"[问题] {legacy_text}")
        for junk in ("hello", "v1.abc", "{", json.dumps({"v": 1, "text": "x"}), self.message(text="  "),
                     self.message(text="字" * (relay.TEXT_MAX + 1)),
                     self.message(title="标" * (relay.TITLE_MAX + 1)),
                     self.message(title=123), "[1]"):
            self.assertIsNone(relay.open_message(junk), junk[:30])
        self.assertEqual(relay.open_message(self.message(category="nope"))["category"], "other")
        # A header field can't fake another line or carry markup.
        self.assertEqual(relay.open_message(self.message(os="iOS\n**平台**：<b>x</b>"))["os"], "iOS 平台 ： b x /b")
        self.assertEqual(relay.open_message(self.message(platform=5))["platform"], "")
        files = relay.open_message(self.message(files=["ab1", "../x", 5, "c", "d", "e"]))["files"]
        self.assertEqual(files, ["ab1", "x", "5"])

    def test_issue_body(self):
        data = relay.open_message(self.message(contact="reader@example.com", text="见 @someone 和 <!-- x --> 第二段", sentAt=1_700_000_000_000))
        body = relay.issue_body("abc123", data, ["![截图 1](u)"])
        self.assertTrue(relay.issue_title(data).startswith("[问题]"))
        self.assertNotIn("reader@example.com", body)
        self.assertNotIn("contact:", body)
        self.assertNotIn("联系", body)
        self.assertIn("@​someone", body)
        self.assertNotIn("<!-- x -->", body)
        self.assertIn("2023-11-14", body)
        self.assertTrue(body.rstrip().endswith("![截图 1](u)"))
        self.assertEqual(relay.known_ids([{"body": body}, {"body": "x"}, {"body": body, "pull_request": {}}]), {"abc123"})
        now = 1_700_100_000.0
        self.assertEqual(
            relay.known_hashes([
                {"body": body, "created_at": "2023-11-15T20:00:00Z"},   # within 24h
                {"body": relay.issue_body("old1", relay.open_message(self.message(text="旧反馈"))),
                 "created_at": "2023-11-01T00:00:00Z"},                 # older than 24h
            ], now=now),
            {relay.text_hash(data)},
        )
        self.assertEqual(relay.safe_id("ab c-->x"), "abc--x")
        ids, hashes = relay.parse_seen_ledger(
            {
                "ids": {"keep1": 1_700_050_000, "expired1": 1_699_000_000},
                "hashes": {"0123456789abcdef": 1_700_050_000, "fedcba9876543210": 1_699_900_000},
            },
            now=now,
        )
        self.assertEqual(ids, {"keep1": 1_700_050_000})
        self.assertEqual(hashes, {"0123456789abcdef": 1_700_050_000})

    def test_attachments(self):
        server = "https://ntfy.sh"
        shot = {"id": "s1", "message": "v2shot", "attachment": {"url": "https://ntfy.sh/file/s1.jpg"}}
        pub_file = {
            "id": "p1",
            "message": "v2file %E7%BA%A2%E6%A5%BC%E6%A2%A6.mobi",
            "attachment": {"url": "https://ntfy.sh/file/p1.mobi", "name": "file-1.mobi"},
        }
        pub_plain = {"id": "p2", "message": "v2file", "attachment": {"url": "https://ntfy.sh/file/p2.txt", "name": "sample.txt"}}
        sealed = {"id": "f1", "message": "v1file", "attachment": {"url": "https://ntfy.sh/file/f1.bin"}}
        self.assertEqual(relay.attachment(server, shot), ("shot", "https://ntfy.sh/file/s1.jpg", ""))
        self.assertEqual(relay.attachment(server, pub_file), ("file", "https://ntfy.sh/file/p1.mobi", "红楼梦.mobi"))
        self.assertEqual(relay.attachment(server, pub_plain), ("file", "https://ntfy.sh/file/p2.txt", "sample.txt"))
        self.assertEqual(relay.attachment(server, sealed)[0], "sealed")
        self.assertIsNone(relay.attachment(server, {**shot, "attachment": {"url": "https://evil.example/file/x"}}))
        self.assertIsNone(relay.attachment(server, {**shot, "message": "hi"}))
        self.assertEqual(relay.safe_ext("红楼梦.MOBI"), ".mobi")
        self.assertEqual(relay.safe_ext("noext"), ".bin")
        self.assertTrue(relay.is_jpeg(JPEG))
        self.assertFalse(relay.is_jpeg(b"\x89PNG\r\n\x1a\n...."))
        lines = relay.attachment_lines(
            [("shot", JPEG, ""), ("file", b"m" * 631_000, "红楼梦[测试].mobi"), ("sealed", b"x" * 2000, ""), None],
            {1: "https://u/1.jpg"},
            {2: ("https://github.com/r/blob/main/id/2.mobi", "https://raw.githubusercontent.com/r/main/id/2.mobi")},
            {3: "p"},
        )
        self.assertEqual(lines[0], "![截图 1](https://u/1.jpg)")
        self.assertIn("红楼梦\\[测试\\].mobi", lines[1])
        self.assertIn("https://github.com/r/blob/main/id/2.mobi", lines[1])
        self.assertIn("直接下载", lines[1])
        self.assertIn("只有开发者能看到", lines[2])
        self.assertIn("没取到", lines[3])
        self.assertIn("没存上", relay.attachment_lines([("shot", JPEG, "")], {}, {}, {})[0])

    def test_shots_repo_push(self):
        with tempfile.TemporaryDirectory() as tmp:
            remote, work = os.path.join(tmp, "remote.git"), os.path.join(tmp, "work")
            run = lambda *a: subprocess.run(a, check=True, capture_output=True)  # noqa: E731
            run("git", "init", "-q", "--bare", "-b", "main", remote)
            run("git", "clone", "-q", remote, work)
            shots = relay.Shots("inokoru/yuyue-feedback-assets", work)
            self.assertTrue(shots.add_files("abc", [("1.jpg", JPEG), ("2.mobi", b"BOOKMOBI")]))
            self.assertTrue(shots.add_files("abc", [("1.jpg", JPEG)]))  # again: nothing new, still fine
            listed = subprocess.run(["git", "--git-dir", remote, "ls-tree", "-r", "--name-only", "main"],
                                    capture_output=True, text=True).stdout.split()
            self.assertEqual(listed, ["abc/1.jpg", "abc/2.mobi"])
            self.assertEqual(shots.url("abc/1.jpg"), "https://raw.githubusercontent.com/inokoru/yuyue-feedback-assets/main/abc/1.jpg")
            self.assertEqual(shots.blob_url("abc/2.mobi"), "https://github.com/inokoru/yuyue-feedback-assets/blob/main/abc/2.mobi")

    @unittest.skipIf(kaneo_import is None or PrivateKey is None, "local kaneo_import / pynacl not installed")
    def test_mac_side_opens_what_the_app_sealed(self):
        body = relay.issue_body("id9", relay.open_message(self.message()))

        def pack(name: str, data: bytes) -> bytes:
            raw = name.encode()
            return self.box.encrypt(b"YYF1" + len(raw).to_bytes(2, "big") + raw + data)

        self.assertEqual(kaneo_import.open_file(self.secret, pack("日志.txt", b"hello")), ("日志.txt", b"hello"))
        self.assertEqual(kaneo_import.open_file(self.secret, pack("../a:b.txt", b"p"))[0], "ab.txt")
        self.assertIsNone(kaneo_import.open_file(self.secret, pack("empty", b"")))
        self.assertIsNone(kaneo_import.open_file(self.secret, b"junk"))

        task = kaneo_import.task_description("inokoru/yuyue", 3, body, ["- 附件 1：/x/1-日志.txt"])
        self.assertTrue(task.startswith("yuyue-feedback-issue:3\n"))
        self.assertIn("/x/1-日志.txt", task)
        self.assertNotIn("contact:", task)
        self.assertEqual(kaneo_import.MARKER_RE.findall(task), ["3"])
        self.assertEqual(kaneo_import.priority_for(task), "high")

    @unittest.skipIf(kaneo_import is None, "local kaneo_import not installed")
    def test_sync_back_rules(self):
        cols = [{"slug": "to-do", "position": 0}, {"slug": "in-progress", "position": 1},
                {"slug": "done", "position": 2, "isFinal": True}]
        stage = kaneo_import.stage
        self.assertEqual([stage(s, cols) for s in ("to-do", "in-progress", "done", None, "gone")],
                         ["new", "working", "done", "new", "new"])
        act = kaneo_import.sync_actions
        self.assertEqual(act(None, "new", True), [])          # just imported
        self.assertEqual(act("new", "new", True), [])
        self.assertEqual(act("new", "working", True), ["label"])
        self.assertEqual(act("working", "done", True), ["unlabel", "close"])
        self.assertEqual(act("done", "done", False), [])      # closed by hand stays closed
        self.assertEqual(act("done", "working", False), ["reopen", "label"])
        self.assertEqual(act("working", "new", True), ["unlabel"])
        self.assertEqual(act(None, "done", True), ["unlabel", "close"])


if __name__ == "__main__":
    unittest.main()
