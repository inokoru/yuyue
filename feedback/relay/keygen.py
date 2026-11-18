#!/usr/bin/env python3
"""生成语阅反馈的密钥和 ntfy 话题。

私钥只留在开发者自己的 Mac 上（~/.config/yuyue/feedback-keys.env），不放进 GitHub：
云端只搬运密文，解邮箱和私密附件的是本机的 kaneo_import.py。
公钥写进 App 的构建配置。话题写进 NTFY_TOPIC secret，不要提交到公开仓库。

依赖：pip install -r feedback/relay/requirements.txt
"""

import base64
import secrets

from nacl.public import PrivateKey


def main() -> None:
    secret = PrivateKey.generate()
    topic = "yuyue-" + secrets.token_hex(16)
    print("NTFY_TOPIC=" + topic)
    print("FEEDBACK_PRIVATE_KEY=" + base64.b64encode(bytes(secret)).decode())
    print("YUYUE_FEEDBACK_PUBLIC_KEY=" + base64.b64encode(bytes(secret.public_key)).decode())
    print()
    print("私钥只存在这台 Mac 上（~/.config/yuyue/feedback-keys.env，权限 600），不要放进 GitHub。")
    print("公钥和话题按 docs/dev/feedback.md 配进 App，话题不要提交。")


if __name__ == "__main__":
    main()
