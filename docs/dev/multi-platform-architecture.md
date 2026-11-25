# 语阅 · 多端功能一致方案（五层防线 + Midscene）

本文档记录语阅在**不更换现有技术栈**（Rust 核心 + TypeScript 书页 + Apple SwiftUI / Android Compose / Windows WinUI 3 三套原生壳）的前提下，确保 Mac、iPhone/iPad、Android、Windows 四端功能与行为一致的完整工程方案、工具选型、落地规范与首轮审计基线。

---

## 一、要解决的问题

语阅的共享层为 `core/yuyue-core`（Rust + UniFFI）和 `reader/`（TypeScript，运行在各端 WebView），但三套原生壳分别用 Swift、Kotlin、C# 实现。各端出现差异通常集中在三类位置：

1. **业务规则在三端各写一份**：设置校验、冲突判定、导出拼装、状态流转在原生端重复实现，改一端漏两端，导致同功能行为分叉。
2. **某端漏做或滞后**：新功能先在主平台上线，其他端缺少系统化的缺口清单，久而久之形成隐性欠账。
3. **WebView 内核与桥接漂移**：Apple 使用 WKWebView（WebKit），Android 使用系统 WebView（Chromium），Windows 使用 WebView2（Chromium）；JS ↔ 原生消息结构稍有变动就可能在某一端静默失效。

---

## 二、总体架构：五层防线（从下往上）

| 层 | 管什么 | 工具与做法 | 仓库产物 | 校验命令 |
|---|---|---|---|---|
| **第 0 层：逻辑下沉** | 杜绝三端各写一份业务逻辑 | 借鉴 Element X / Mozilla UniFFI / Crux 思路：非界面逻辑一律下沉到 `yuyue-core`，原生壳只管显示与系统接入 | 本文规范 + [PR 模板](../../.github/PULL_REQUEST_TEMPLATE.md) | 代码评审 + `audit-api-parity.mjs` |
| **第 1 层：功能对照表** | 防止"某端漏做"，欠账显式可见 | 维护功能 × 四端矩阵；CI 强检 `done` 项必须有真实存在的测试证据，`partial`/`planned`/`na` 必须说明原因；配合 UniFFI/书页 API 自动审计 | [docs/parity.yaml](../parity.yaml)<br>[scripts/check-parity.mjs](../../scripts/check-parity.mjs)<br>[scripts/audit-api-parity.mjs](../../scripts/audit-api-parity.mjs) | `node scripts/check-parity.mjs`<br>`node scripts/audit-api-parity.mjs` |
| **第 2 层：核心共享用例** | 同样输入，四语言输出完全一致 | 声明式 JSON 共享场景 + 四语言 UniFFI 运行器（Rust / C# / Swift / Kotlin）+ Rust `proptest` 属性测试 + `insta` 快照锁定 | [fixtures/conformance/](../../fixtures/conformance/)<br>[conformance.rs](../../core/yuyue-core/tests/conformance.rs) · [sync_properties.rs](../../core/yuyue-core/tests/sync_properties.rs) · [snapshots.rs](../../core/yuyue-core/tests/snapshots.rs)<br>[ConformanceTests.cs](../../windows/Yuyue.Tests/ConformanceTests.cs)<br>[ConformanceTests.swift](../../apple/YuyueSnapshotTests/ConformanceTests.swift)<br>[ConformanceTest.kt](../../android/app/src/test/java/app/yuyue/core/ConformanceTest.kt) | `cd core && cargo test --workspace --locked`<br>`dotnet test windows/Yuyue.Tests/Yuyue.Tests.csproj -c Release -p:Platform=x64`<br>Apple `xcodebuild test`<br>Android `./gradlew testDebugUnitTest` |
| **第 3 层：书页通信约定** | 约束 WebView 内核差异与 JS ↔ 原生消息格式 | 直接从 TS 类型生成 JSON Schema；Playwright 在 WebKit 与 Chromium 上做实时消息校验 + 三端桥接传输模拟 + 视觉不变量 / `toHaveScreenshot`；三端原生解析器各自跑契约单测 | [generate-schema.mjs](../../reader/scripts/generate-schema.mjs)<br>[bridge-messages.schema.json](../../reader/schema/bridge-messages.schema.json)<br>[reader-messages.json](../../fixtures/conformance/reader-messages.json)<br>[contract.spec.ts](../../reader/test/contract.spec.ts) · [visual.spec.ts](../../reader/test/visual.spec.ts)<br>三端 `ReaderBridgeContractTest*` | `cd reader && pnpm schema:check && pnpm typecheck && pnpm test` |
| **第 4 层：跨端界面冒烟（Midscene）** | 真实用户主线在四端都能走通 | 采用 **Midscene.js**（`@midscene/cli`），用一份自然语言剧本覆盖 Windows / macOS（`@midscene/computer`）、Android（`@midscene/android`）、iOS（`@midscene/ios`）与 Web；CI 离线校验剧本、夹具与 `parity_ids` | [e2e/midscene/](../../e2e/midscene/README.md)<br>[scenarios/*.yaml](../../e2e/midscene/scenarios/)<br>[run-midscene.mjs](../../e2e/midscene/scripts/run-midscene.mjs)<br>[validate-flows.mjs](../../e2e/midscene/scripts/validate-flows.mjs) | `node e2e/midscene/scripts/validate-flows.mjs`<br>`pnpm --dir e2e/midscene run:<platform>` |
| **第 5 层：跨设备同步与旧库兼容** | 多端互相同步不出错、升级不丢旧数据 | 内置 RFC 4918 WebDAV 服务 + 真实 `yuyue` CLI 多设备同步冲突模拟；冻结旧版本书库与 `.yuyue` 快照样本自动回归 | [scripts/sync-webdav-e2e.mjs](../../scripts/sync-webdav-e2e.mjs)<br>[fixtures/legacy/](../../fixtures/legacy/README.md)<br>[legacy_compat.rs](../../core/yuyue-core/tests/legacy_compat.rs) | `node scripts/sync-webdav-e2e.mjs`<br>`cd core && cargo test -p yuyue-core --test legacy_compat --locked` |

### 工具选型说明

- **为什么第 4 层选 Midscene 而不是 Maestro + FlaUI + XCUITest**：Maestro 不支持 Windows WinUI 桌面端，强行拆多套工具维护成本高；Midscene（`@midscene/cli`）内置 `@midscene/computer` / `@midscene/android` / `@midscene/ios` / `@midscene/web`，无需先在三端统一无障碍 ID，可直接把 [testing.md](testing.md) 的主线写成一份共享自然语言剧本，并对中文界面友好、自带截图 HTML 报告。因其每次调用视觉模型有耗时与费用，**只用于 6 条核心主线验收**，细节与边界逻辑全部交给第 2、3、5 层确定性测试。
- **截图快照（辅助）**：Apple 已有 `YuyueSnapshotTests`，Android 已有 Compose Preview Screenshot Testing，Playwright 已有 `toHaveScreenshot`；截图比对只用于各端与自己上一版防回归，不要求三端像素相同。
- **流程辅助（可选）**：`pstack` 的"验证技能 / 拿出真实运行证据再汇报"以及 Spec Kit / OpenSpec 的规格驱动思想，可用于约束 AI 辅助开发过程，但**最终兜底以本仓库的五层自动化校验和 CI 门禁为准**。

---

## 三、第 0 层：代码分层硬规则（Core-First）

### 必须放入 `core/yuyue-core`（Rust + UniFFI）
- 书籍解析、编码识别（UTF-8 / GBK）、分句、简繁转换、正则替换规则与排序
- 阅读进度计算、书架状态 / 合集 / 评分 / 标签 / 只留记录、划线 / 书签 / 生词本 / 今日回顾 / 每日阅读时长统计
- 阅读设置模型与默认值、我的纸配色计算
- HLC CRDT 同步日志、`SyncPart` 分部过滤、想法冲突检测与解决、`.yuyue` 数据快照
- 导出结构拼装（带划线 EPUB 等非平台 IO 部分）、剧本认人、广播剧分角混音、有声书对齐、更新版本比较与更新日志解析

### 必须放入 `reader/`（TypeScript）
- 分页与滚动几何、中文标点挤压、墨线跟读、划线与搜索命中绘制、气泡模式 DOM 重排、外文点词与行间注释
- 所有 JS ↔ 原生通信结构必须先改 `reader/src/bridge.ts` / `settings.ts` / `marks.ts` / `foreign.ts`，然后运行 `cd reader && pnpm schema` 重新生成 Schema 并同步三端契约测试

### 只留在原生壳（`apple/`、`android/`、`windows/`）
- 原生控件布局、平台视觉风格与动画（如 Liquid Glass / Material / Mica）、手势、键盘快捷键、窗口与导航生命周期
- 系统能力接入：系统 TTS、锁屏 / 耳机 / 任务栏媒体控制、系统文件选择器、凭据存储（Keychain / Keystore / DPAPI）、本地音频播放与 ONNX 推理调度、iCloud / Google Drive / WebDAV 传输层

### 禁止事项
- 禁止在 Swift、Kotlin、C# 中各自重复实现一套业务规则；发现重复实现时优先下沉到 `yuyue-core` 并补充 `fixtures/conformance/` 用例。
- 禁止修改 `reader/src/bridge.ts` 字段而不更新 `reader/schema/bridge-messages.schema.json` 及三端解析测试。

---

## 四、各层细则

### 第 1 层：`docs/parity.yaml` 与自动审计
- `status` 仅允许 `done`、`partial`、`planned`、`na`。
- `done` 必须提供至少一条 `tests` 证据，`scripts/check-parity.mjs` 会校验目标文件与符号真实存在（支持 `rust:`、`conformance:`、`reader:`、`apple:`、`android:`、`windows:`、`midscene:`、`sync-e2e:`、`script:`、`manual:`）。
- `partial`、`planned`、`na` 必须填写非空 `note` 说明缺口或原因。
- `scripts/audit-api-parity.mjs` 自动扫描三端源码对所有导出 UniFFI 方法、书页 `window.yy.*` 接口与 11 种桥接消息的覆盖情况，防止对照表与真实代码脱节。

### 第 2 层：核心共享用例 + 属性测试 + 快照
- 共享用例放在 `fixtures/conformance/*.json`（格式见 [fixtures/conformance/README.md](../../fixtures/conformance/README.md)），当前覆盖：
  - `01-import-and-dedup`（多格式导入、GBK 解码、去重、坏/空文件报错）
  - `02-progress-and-settings`（进度、全局设置重开持久化、单书设置覆盖与 HTML 契约）
  - `03-marks-search-and-delete-undo`（划线样式与想法、书内搜索、删书撤销）
  - `04-sync-two-devices-and-conflict`（双设备独立设置合并、离线想法冲突、`Both` 解决与幂等性）
  - `05-snapshot-and-sync-parts`（`SyncPart` 过滤回补、`.yuyue` 快照导出合并恢复）
  - `06-sentences-and-versions`（`page_sentences` 与 `compare_versions` 纯函数）
  - `07-review-stats-undo-and-rules`（今日回顾、每日阅读统计、书签/合集/我的纸/生词撤销、单书词频、替换规则排序、只留记录与清理已删文件）
- `core/yuyue-core/tests/sync_properties.rs` 用 `proptest` 验证 CRDT 同步的**幂等性**、**乱序交换律**以及 **±24 小时时钟偏差下离线并发想法不丢失**。
- `core/yuyue-core/tests/snapshots.rs` 用 `insta` 锁定中英混排断句、4 套纸张 HTML 头部与更新日志解析输出。

### 第 3 层：书页与原生通信契约
- `reader/scripts/generate-schema.mjs` 直接解析 TS 源码生成 Draft 2020-12 JSON Schema（`reader/schema/bridge-messages.schema.json`），零额外依赖，CI 运行 `pnpm schema:check` 防止漂移。
- `reader/test/contract.spec.ts` 在 WebKit（Apple）与 Chromium（Android / Windows）上验证所有规范样例和实时书页消息，并同时模拟三套桥接通道：`webkit.messageHandlers.yy`、`yyNative.postMessage`、`chrome.webview.postMessage`。
- `reader/test/visual.spec.ts` 验证纸张主题、墨水屏、双栏、划线与墨线的视觉不变量，支持通过 `YY_VISUAL=1` 开启 `toHaveScreenshot` 像素回归。
- Windows（`ReaderBridgeContractTests.cs`）、Apple（`ReaderBridgeContractTests.swift`）、Android（`ReaderBridgeContractTest.kt`）各自校验规范消息样例。

### 第 4 层：Midscene 跨端主线测试
- 共享剧本位于 `e2e/midscene/scenarios/`（6 条主线、70 步、22 个 `aiAssert`），与 [testing.md](testing.md) 和 [docs/parity.yaml](../parity.yaml) 一一对应。
- `e2e/midscene/scripts/run-midscene.mjs` 按目标平台渲染 `@midscene/cli` YAML，在 Windows 上自动以 `Yuyue.exe --library <临时目录>` 拉起隔离实例并在结束时清理，绝不触碰真实书库。
- `e2e/midscene/scripts/validate-flows.mjs` 在 CI 离线校验所有剧本语法、`parity_ids`、`fixtures` 与断言完整性（不消耗模型额度）。

### 第 5 层：WebDAV 同步模拟与旧书库兼容
- `scripts/sync-webdav-e2e.mjs` 内置 RFC 4918 WebDAV 服务（Basic Auth、`OPTIONS`/`MKCOL`/`PROPFIND`/`GET`/`PUT`/`DELETE`），也支持 `--webdav-url` 对接外部 WebDAV，使用真实 `yuyue` CLI 进程跑 4 条跨设备场景（双向同步含书文件、设置无冲突合并、离线想法冲突与 `both` 解决、删书传播不复活与幂等性）。
- `fixtures/legacy/` 冻结各历史版本的小书库与 `.yuyue` 快照（当前已冻结 `v0.1.3-sample`），由 `core/yuyue-core/tests/legacy_compat.rs` 在每次 `cargo test` 时自动验证原地打开与快照恢复零丢数。

---

## 五、代码级审计基线与缺口清零记录（2026-11-25）

运行 `node scripts/audit-api-parity.mjs` 得到的全仓覆盖变化（首轮审计 → 缺口补齐后）：

| 维度 | Apple | Android | Windows |
|---|---:|---:|---:|
| `YuyueCore` UniFFI（271 个导出方法） | 261 / 271 | 253 → **257 / 271** | 228 → **257 / 271** |
| 书页 `window.yy.*`（25 个接口） | 24 / 25 | 19 / 25 | 25 / 25 |
| 桥接消息（11 种 Schema 类型） | 11 / 11 | 9 / 11 | 11 / 11 |
| [docs/parity.yaml](../parity.yaml)（31 项功能） | 30 done / 1 partial | **29 done** / 1 partial / 1 N/A | **27 done** / 3 partial / 1 N/A |

已全部补齐并入库的原审计缺口（`[A K -]` 缺口清零，Android 功能型 `[A - W]` 缺口清零）：

- **Android（已补齐）**：
  1. 同步想法冲突并排解决界面（`note_conflicts` / `resolve_note_conflict`，`ConflictSheet.kt` + `Sync.kt` + `DataPages.kt`）
  2. 替换规则上移/下移排序（`move_replace_rule`，`ReplaceRulesSheet.kt`）与 TXT 排版疑点检测提示（`txt_doubt`，`ReaderModel.kt` / `ReaderScreen.kt` / `ShelfSheets.kt`）
- **Windows（已补齐）**：
  1. 今日回顾（`review_today` / `set_mark_review`，`LibraryView.SubViews.cs`）
  2. 阅读统计与 26 周热力图（`day_times`，`ReadingHeatmapBuilder.cs` + `BookRecordPageBuilder.cs`）
  3. 只留记录、标记读完与定时/手动清理已删文件（`keep_record_only` / `set_finished` / `purge_deleted`）
  4. 书签 / 合集 / 我的纸 / 生词删除撤销（`restore_bookmark` / `restore_collection` / `restore_custom_paper` / `restore_word`）
  5. 单书生词出现次数（`book_word_count`）与听读书对齐时间轴同步（`save_listening`）
  6. 有声书章节重命名、声学缓存快速重对齐、跳过/停顿省时、同步时间轴 EPUB 导出、录音转写文字版提示、搜索按词定位及广播剧模式/层/角色/局域网借用片段与音色卡导入（共 18 项接口）

---

## 六、日常开发与发版流程

### 新增或补齐一个功能
1. **核心与契约先行**：在 `core/yuyue-core` 实现并导出 UniFFI 接口；若涉及多端共享行为，在 `fixtures/conformance/` 补 JSON 场景用例。
2. **书页更新**（若涉及阅读页）：修改 `reader/src/` 后执行 `cd reader && pnpm schema && pnpm build && pnpm test`。
3. **原生端接入**：接入 Mac / iOS / Android / Windows；暂缓或不适用端在 `docs/parity.yaml` 写明 `status` 与 `note`。
4. **更新对照表与主线剧本**：更新 `docs/parity.yaml`，必要时同步 `e2e/midscene/scenarios/`。
5. **本地一键验证**：
   ```sh
   node scripts/check-parity.mjs
   node scripts/audit-api-parity.mjs
   node e2e/midscene/scripts/validate-flows.mjs
   cd core && cargo test --workspace --locked
   cd reader && pnpm schema:check && pnpm typecheck && pnpm test
   node scripts/sync-webdav-e2e.mjs
   ```
   Windows 端额外执行：
   ```powershell
   dotnet test windows/Yuyue.Tests/Yuyue.Tests.csproj -c Release -p:Platform=x64 --verbosity minimal
   ```

### 运行 Midscene 跨端实测
```powershell
cd e2e/midscene
pnpm install
copy .env.example .env      # 填入视觉模型 Key（如千问 VL / OpenAI / Gemini）
pnpm verify:model           # 校验视觉模型连通性
pnpm run:windows            # 自动拉起隔离临时书库的 Yuyue.exe 并生成 HTML 截图报告
```
其他平台详见 [e2e/midscene/README.md](../../e2e/midscene/README.md)。

### 每次正式发版前
1. 冻结该版本的最小书库与 `snapshot.yuyue` 样本进 `fixtures/legacy/v<x.y.z>-sample/`（见 [fixtures/legacy/README.md](../../fixtures/legacy/README.md)），确认 `cargo test -p yuyue-core --test legacy_compat --locked` 全绿。
2. 确认 CI 中 `parity`、`core`、`reader`、`l10n`、`android`、`apple`、`windows` 全部通过。
