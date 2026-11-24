# 反馈与崩溃上报

语阅在 Apple（iOS / iPadOS / macOS）、Android 和 Windows 端统一采用 **AirFeedback** 单通道直连方案：用户在应用内填写反馈正文、可选附带截图与基础设备信息，客户端直接向 AirFeedback 接入点发起 HTTPS POST，内容**仅发给开发者、不会公开**，且表单内不收集邮箱或任何联系方式（需要回复的用户可通过表单内置的 `mailto:yuyue.app@icloud.com` 入口用本机邮件客户端直接发信）。

```
Apple (iOS / iPadOS / macOS) ──┐
Android                      ──┼──▶ POST https://apps.air.build/v1/ingest/feedback (Bearer App Key)
Windows                      ──┘      └─ 仅开发者后台可见，不公开为 Issue

Apple (MetricKit, iOS / macOS) ──▶ AirFeedback 崩溃上报（MetricKit 自动采集 + dSYM 符号化 + App Attest）
```

> 仓库中的 `feedback/` 目录（旧版 `ntfy.sh → Cloudflare Worker → GitHub Actions → 公开 Issue` 中转脚本）目前处于**未启用状态**，保留在仓库中仅供将来需要恢复公开中转方案时参考。

## 各平台实现

| 平台 | 反馈表单与客户端 | 崩溃上报 | 说明 |
| --- | --- | --- | --- |
| **Apple (iOS / iPadOS / macOS)** | `apple/Yuyue/App/Feedback.swift`（表单与截图）+ AirFeedback 客户端 | **已支持**：基于系统 `MetricKit` 自动采集诊断与崩溃报告（`AirCrashReports.swift`），发布构建配合 `workers feedback dsyms upload` 符号化，并支持 App Attest 设备证明（`AirDeviceProof.swift`） | 桌面端保留系统级区域/窗口截图条，iOS/iPadOS 支持相册选图与剪贴板贴图 |
| **Android** | `android/app/src/main/java/app/yuyue/feedback/` | 仅应用内反馈表单 | Compose 整页表单，支持相册选图、EXIF 剥离与本地草稿 |
| **Windows** | `windows/Yuyue/Core/AirFeedbackClient.cs`（纯 .NET 8 客户端）+ `windows/Yuyue/Core/UpdateAndFeedbackService.cs`（WinUI 3 弹窗） | 仅应用内反馈表单（不实现自动崩溃上报） | 支持截取当前窗口、多选图片、拖放图片与 `Ctrl+V` 粘贴截图；`windows/Yuyue.Tests/AirFeedbackTests.cs` 覆盖纯客户端单测 |

## 协议与负载格式

三端统一向 `POST https://apps.air.build/v1/ingest/feedback` 提交 JSON，请求头携带 `Authorization: Bearer <AppKey>` 与 `Content-Type: application/json`：

```json
{
  "message": "用户反馈正文（按 Unicode 标量值截断，最多 4000 字）",
  "appVersion": "0.1.3",
  "appBuild": "5",
  "deviceModel": "Windows (x64)",
  "osVersion": "Windows 11 (Build 26100)",
  "locale": "zh-CN",
  "images": [
    {
      "mediaType": "image/jpeg",
      "data": "<base64>"
    }
  ]
}
```

- **提交校验**：正文非纯空白 **或** 至少附带 1 张图片即可发送（支持纯截图提交）；发送中禁用按钮避免重复提交。
- **字数限制**：正文上限为 **4000 个 Unicode 标量值**（`Rune` / scalar count，避免截断代理对拆坏 Emoji）；平时隐藏计数，达到 3600 字显示 `N / 4000`，达到 3900 字高亮提醒。
- **图片规范**：
  - 最多 **4 张**图片，只接受图片或截图，不支持上传普通文件或本地日志。
  - 发送前先按 EXIF 方向矫正并重编码为 JPEG，**彻底剥离原图 EXIF 元数据**（拍摄位置、相机型号、时间等）。
  - 压缩阶梯：长边上限 **2048 px**、单张体积上限 **8 MB**（Windows 压缩阶梯为 `2048@0.80 → 2048@0.65 → 1600@0.65 → 1200@0.55`）。
- **设备信息开关**：
  - 表单内提供可折叠的「附带设备信息」卡片，**默认开启、默认折叠**，展开后明文列出 `版本`、`系统`、`设备`、`语言`。
  - 用户关闭开关时，`appVersion` / `appBuild` / `deviceModel` / `osVersion` / `locale` 全部发送空字符串。

## 隐私与交互约定

- **单通道非公开**：表单底部以绿色锁标明示「仅发给开发者，不会公开」，隐私说明同步更新为「只有在你主动提交反馈时才会联网，正文和截图仅发给开发者，不会公开，也不收集邮箱。」
- **零邮箱采集**：表单不设邮箱输入框；需要回复的用户点击「需要回复？直接发邮件 ↗」（`mailto:yuyue.app@icloud.com`），由本机邮件客户端直接发信。
- **自动草稿与失败重试**：
  - 正文、图片附件与设备信息开关均自动保存在本地草稿中，有内容或图片时底部展示「草稿已自动保存 · 清空」。
  - 网络不可用或服务端返回非 2xx 错误时，**不关闭表单、不清空草稿和图片**，在表单内直接展示错误横幅（优先展示服务端 JSON 中的 `reason`，否则回退为离线重试提示），方便联网后一键重发；不再维护后台离线队列。
  - 发送成功后清空草稿与临时缓存文件，并弹出感谢页（「已发给开发者，谢谢」），提供「再写一条」与「完成」操作。
