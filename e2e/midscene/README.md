# 语阅 · Midscene 跨端界面冒烟测试（Layer 4）

一套自然语言主线场景（`scenarios/*.yaml`），基于 [Midscene.js](https://midscenejs.com/)（`@midscene/cli`）驱动四端原生应用与 Web 书页，直接对应 [docs/dev/testing.md](../../docs/dev/testing.md) 与 [docs/parity.yaml](../../docs/parity.yaml)。

## 为什么选 Midscene

- **一套剧本覆盖全部平台**：`@midscene/cli` 内置 `@midscene/computer`（Windows WinUI / macOS）、`@midscene/android`（adb）、`@midscene/ios`（WebDriverAgent）与 `@midscene/web`（Playwright）。
- **不依赖跨端统一控件 ID**：通过视觉模型理解中文界面，按用户视角操作与断言。
- **自带可视化报告**：每次运行生成 `midscene_run/report/*.html`（含逐步截图、点击框选与断言理由），可直接附进 PR 验收记录。

## 目录结构

```text
e2e/midscene/
├── scenarios/               # 一份共享主线剧本（与 testing.md / parity.yaml 对应）
│   ├── 01-empty-shelf-and-import.yaml
│   ├── 02-reading-and-typography.yaml
│   ├── 03-highlight-thought-and-export.yaml
│   ├── 04-tts-listen-and-sleep.yaml
│   ├── 05-foreign-lookup-and-vocab.yaml
│   └── 06-sync-and-snapshot.yaml
├── scripts/
│   ├── validate-flows.mjs   # CI 离线校验（零依赖、不花模型费用）
│   └── run-midscene.mjs     # 按目标端渲染 Midscene YAML 并调用 `npx midscene`
├── package.json
└── .env.example
```

## 快速开始

1. 安装依赖并配置视觉模型密钥（独立于 `reader/`，不影响 `reader` CI 锁文件）：
   ```sh
   cd e2e/midscene
   pnpm install
   cp .env.example .env
   # 编辑 .env 填入 MIDSCENE_MODEL_API_KEY / MIDSCENE_MODEL_BASE_URL / MIDSCENE_MODEL_NAME
   ```

2. 校验剧本合法性（离线，CI 每次自动跑）：
   ```sh
   node scripts/validate-flows.mjs
   ```

3. 只渲染目标端 Midscene YAML 预览（不调模型）：
   ```sh
   node scripts/run-midscene.mjs --platform windows --render-only
   ```

4. 在目标端运行主线（始终使用临时空书库，不碰你的真实书库）：
   - **Windows（WinUI 3）**：先启动调试版 `Yuyue.exe`（指向空临时书库），再跑：
     ```sh
     pnpm run:windows
     # 或单跑一条：
     node scripts/run-midscene.mjs --platform windows --scenario 01-empty-shelf-and-import
     ```
   - **macOS**：`open -n <Yuyue.app> --args -debugLibrary /tmp/yuyue-midscene-lib`，再跑：
     ```sh
     pnpm run:macos
     ```
   - **Android**：连上真机/模拟器，`adb push ../../fixtures/books /sdcard/Download/yuyue-fixtures/`，配置 `ANDROID_DEVICE_ID`，再跑：
     ```sh
     pnpm run:android
     ```
   - **iOS**：启动模拟器或 WebDriverAgent，配置 `IOS_UDID`，再跑：
     ```sh
     pnpm run:ios
     ```
   - **Web 书页冒烟**：先在 `reader/` 起 `node scripts/serve.mjs --port 8787`，再跑：
     ```sh
     pnpm run:web
     ```
