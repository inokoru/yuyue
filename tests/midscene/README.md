# 语阅 Windows 端 Midscene 正式版视觉 E2E 测试

基于 `@midscene/test` + `@midscene/computer` 搭建的 Windows 桌面正式版验收套件，用来覆盖 [docs/dev/testing.md](../../docs/dev/testing.md) 中单元测试无法验证的真实界面渲染、深浅色对比度、PDF 排版、品牌图标一致性、弹窗交互与重启持久化。

> **与 `e2e/midscene/` 的分工**：
> - **`tests/midscene/`（本目录）**：Windows 端发版前完整验收套件（6 个文件、17 条用例），内置 `yuyue.launch` / `yuyue.close` / `yuyue.checkLog` 生命周期节点，支持 Debug 与 **Release 正式构建**，自动隔离临时书库并在每条用例结束后扫描 `yuyue.log` 拦截未捕获异常。
> - **`e2e/midscene/`**：四端共享 Layer 4 主线剧本（CI 离线校验 `validate-flows.mjs` + `docs/parity.yaml` 对照表证据），已自动复用本目录 `.env` 配置。

测试启动 `Yuyue.exe` 时始终传入 `--library <临时目录>`，**绝不会触碰你的真实书库**；每条用例结束或异常中断后会自动关闭测试进程、检查 `yuyue.log` 崩溃日志并清理临时书库。

## 1. 覆盖矩阵（发正式版前验收）

| 用例文件 | 覆盖内容 | Debug / Release 通用 |
|---|---|---|
| `cases/01-shelf-smoke.yaml` | 首次启动空书架导入入口；导入《背影》EPUB 中文书页渲染 | 是 |
| `cases/02-reader-visual.yaml` | 深色模式《红楼梦》翻页、目录跳章、排版 `Aa` 浮层；`sample.pdf` 渲染 | 是 |
| `cases/03-import-and-edge-cases.yaml` | GBK TXT 中文解码；《了不起的盖茨比》英文排版与翻页；空文件/损坏 EPUB 容错不崩 | 是 |
| `cases/04-settings-and-persistence.yaml` | 深色设置页切换（外观/阅读）；英文界面本地化完整度；重启书库持久化 | 是 |
| `cases/05-companion-and-listening.yaml` | 书伴「添加服务商」分类搜索弹窗；朗读首屏默认声音、音色试听工作台、声音来源目录；隐藏在线声音一键恢复；阅读器底部朗读条与定时菜单 | 是 |
| `cases/06-export-sync-and-about.yaml` | 浅色/深色「笔记导出」四款品牌方砖图标（含新枝）；同步与 `.yuyue` 数据快照入口；浅色/深色「关于」双栏藏书票与手写签名 | 是 |

## 2. 前置准备

1. 构建 Windows **Release 正式版**（或 Debug 调试版）：
   ```powershell
   dotnet build windows/Yuyue/Yuyue.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
   ```
2. 安装依赖（在 `tests/midscene/` 目录执行）：
   ```powershell
   cd tests/midscene
   pnpm install --ignore-workspace
   ```
3. 配置视觉模型（复制 `.env.example` 为 `.env`，填入 Key，`.env` 不会被提交）：
   ```powershell
   Copy-Item .env.example .env
   pnpm run model:verify
   ```

## 3. 发版前运行命令

```powershell
# 1) 离线静态检查（TS 类型 + 节点文档）
pnpm run check

# 2) 验证视觉模型连通性与坐标定位
pnpm run model:verify

# 3) 跑正式版（Release）全量 6 组 17 条视觉验收（推荐发版前必跑）
pnpm run test:release

# 4) 只跑快速冒烟（空书架 + 设置 + 持久化）
pnpm run test:release:smoke

# 5) 只跑设置 / 书伴 / 朗读 / 笔记导出 / 关于（适合界面改版回归）
pnpm run test:release:settings

# 6) 单跑指定用例文件（加 --cache 可开启定位缓存加速复测）
node run.mjs --release --cache cases/06-export-sync-and-about.yaml

# 查看多显示器 ID（多屏时可填入 .env 的 COMPUTER_DISPLAY_ID）
pnpm run displays
```

测试报告输出至 `tests/midscene/midscene_run/report/*.html`（已加入 `.gitignore`），用浏览器打开即可逐帧回放每一步截图、点击框选坐标与 AI 断言理由。

## 4. 自定义生命周期节点

[midscene.config.ts](./midscene.config.ts) 为语阅注册了三个专用节点：

- **`yuyue.launch`**：以独立临时 `--library` 启动 `Yuyue.exe`。传 `theme`（`light`/`dark`）或 `prefs`（`DevicePrefs` 字段覆盖，如 `UiLanguage`、`HiddenVoiceProviders`）时会自动预写 `windows-prefs.json`，因此在 **Release 正式版**下也 100% 生效；同时支持 `fixtures`、`settings`、`tab`、`view`、`reuseLibrary` 等参数。
- **`yuyue.close`**：关闭测试进程，自动检查 `yuyue.log` 是否有未捕获异常/启动崩溃（默认开启，发现即报错），再清理临时书库（传 `keepLibrary: true` 可配合 `reuseLibrary: true` 测重启持久化）。
- **`yuyue.checkLog`**：在长用例中途主动校验 `yuyue.log` 无致命异常。

## 5. Windows 桌面端运行注意事项

- **权限一致（UIPI）**：确保 PowerShell 终端与 `Yuyue.exe` 同为普通用户权限，避免模拟键鼠被拦截。
- **运行期间不要移动鼠标或切窗口**：`@midscene/computer` 驱动系统真实键鼠与主屏截屏。
- **发版前校验**：可顺带在仓库根目录跑一次跨端离线校验：
  ```powershell
  node e2e/midscene/scripts/validate-flows.mjs
  node scripts/check-parity.mjs
  node windows/build-l10n.mjs --audit
  ```
