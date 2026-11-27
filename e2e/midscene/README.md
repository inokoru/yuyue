# 语阅 · Midscene 全端界面验收套件（Layer 4）

一套共享自然语言剧本（`scenarios/*.yaml`），基于 [Midscene.js](https://midscenejs.com/)（`@midscene/cli` v1.18+）统一驱动 **Windows / macOS / Android / iOS / Web 五端**，直接对应 [docs/dev/testing.md](../../docs/dev/testing.md) 与 [docs/parity.yaml](../../docs/parity.yaml)。

## 五端支持总览

| 平台 | Midscene 驱动 | 运行主机要求 | 自动准备与隔离能力 | 一键命令 |
|---|---|---|---|---|
| **Windows** | `@midscene/computer` | Windows PC | 自动拉起隔离 `--library <临时目录>`，锁定主窗口，结束自动查 `yuyue.log` 崩溃并清理 | `pnpm run run:windows` |
| **macOS** | `@midscene/computer` | Mac（需授终端录屏 + 辅助功能权限） | 自动查找 Debug `Yuyue.app` 并以 `-debugLibrary <临时目录>` 隔离启动，默认保护真实书库 | `pnpm run run:macos` |
| **Android** | `@midscene/android` | 任意系统（Windows / Mac / Linux + `adb`） | 自动检测在线真机/模拟器、校验 `app.yuyue` 安装、自动 `adb push` 测试夹具到手机，支持 `--apk` / `--fresh` | `pnpm run run:android` |
| **iOS** | `@midscene/ios` | Mac（连真机或 Simulator + WDA） | 自动探测 WebDriverAgent `/status` 健康度；模拟器下自动把夹具放入 `Documents/yuyue-fixtures/` | `pnpm run run:ios` |
| **Web 书页** | `@midscene/web` | 任意系统（无头浏览器，不占用桌面键鼠） | 自动拉起 `reader/scripts/serve.mjs` 打开 `/demo/`，测完自动关闭本地服务 | `pnpm run run:web` |

> **补充**：Windows 端在发正式版前还提供了更细粒度的桌面专项套件 [tests/midscene/](../../tests/midscene/README.md)（6 组 17 条用例，含深浅色切换、英文界面、隐藏在线声音一键恢复、品牌方砖、关于藏书票与重启持久化）。两套目录自动共用 `tests/midscene/.env`，无需重复填写 API Key。

## 目录结构

```text
e2e/midscene/
├── scenarios/                        # 8 套共享剧本（对应 testing.md / parity.yaml）
│   ├── 01-empty-shelf-and-import.yaml        # 四端：空书架、多格式导入、GBK、坏文件容错
│   ├── 02-reading-and-typography.yaml        # 四端：翻页、目录跳转、换纸/字号排版
│   ├── 03-highlight-thought-and-export.yaml  # 四端：选字划线、写想法、笔记查看与导出
│   ├── 04-tts-listen-and-sleep.yaml          # 四端：朗读、墨线跟读、睡眠定时、暂停
│   ├── 05-foreign-lookup-and-vocab.yaml      # 四端：外文点词释义、生词本
│   ├── 06-sync-and-snapshot.yaml             # 四端：WebDAV 同步与 .yuyue 数据快照
│   ├── 07-companion-voices-and-about.yaml    # 四端：书伴服务商、朗读声音、笔记导出品牌、关于页
│   └── 08-web-reader-smoke.yaml              # Web：共享书页翻页、换纸、模拟朗读墨线
├── scripts/
│   ├── validate-flows.mjs            # CI 离线校验（零依赖、不花模型费用）
│   └── run-midscene.mjs              # 五端环境体检（--doctor）、YAML 渲染与实机执行
├── package.json
└── .env.example
```

## 快速开始

### 1. 安装与校验模型（共用 `tests/midscene/.env`）

```sh
cd e2e/midscene
pnpm install
# 若已配好 tests/midscene/.env 可直接复用；否则 cp .env.example .env 后填入 Key
pnpm run verify:model
```

### 2. 离线校验与五端 YAML 预渲染（不消耗模型额度）

```sh
pnpm run validate    # 校验全部剧本语法、parity_ids、夹具与平台目标头
pnpm run render      # 一次性渲染五端 YAML 到 generated/<platform>/
```

### 3. 各端实机验收步骤

#### Windows（在 Windows PC 上）
```powershell
pnpm run doctor:windows
pnpm run run:windows
# 只跑单条剧本：
node scripts/run-midscene.mjs --platform windows --scenario 07-companion-voices-and-about
```
（完整 17 条 Windows 正式版验收请运行 `cd tests/midscene && pnpm run test:release`。）

#### Android（Windows / Mac / Linux 均可，连手机或开模拟器）
```sh
# 1) USB 连上 Android 手机（开 USB 调试）或启动模拟器，安装好 app.yuyue
pnpm run doctor:android

# 2) 自动推送测试书籍夹具并执行全量 7 条主线
pnpm run run:android

# 可选：在测试机/模拟器上自动安装 APK 并清空旧数据从空书架起跑（真机慎用 --fresh）
node scripts/run-midscene.mjs --platform android --apk ../../android/app/build/outputs/apk/debug/app-debug.apk --fresh
```

#### macOS（在 Mac 上）
```sh
# 1) 先在「系统设置 → 隐私与安全性」给终端授予「录屏与系统录音」和「辅助功能」权限
# 2) 先构建 Debug 版（仅 Debug 支持 -debugLibrary 隔离书库，防止动到你真实书库）：
#    cd apple && xcodegen && xcodebuild -project Yuyue.xcodeproj -scheme Yuyue -configuration Debug -destination 'platform=macOS' -derivedDataPath build/dd build
pnpm run doctor:macos
pnpm run run:macos
```

#### iOS（在 Mac 上连真机或 iOS Simulator）
```sh
# 1) 在模拟器或真机装好 Yuyue（Bundle ID: app.yuyue.Yuyue）并启动 WebDriverAgent（默认 8100 端口）
#    例如真机用 tidevice: tidevice wdaproxy -B <你的WDA BundleID> --port 8100
pnpm run doctor:ios
pnpm run run:ios
```

#### Web 书页（任意系统，无头浏览器秒跑）
```sh
# 先确保已编译书页：cd reader && pnpm install && pnpm build
pnpm run doctor:web
pnpm run run:web
# 加 --headed 可视化观看浏览器操作：
node scripts/run-midscene.mjs --platform web --headed
```
