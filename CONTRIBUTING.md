# 参与语阅

大的改动请先开 Issue 讨论，开发方向见 [ROADMAP.md](ROADMAP.md)。

## 提 PR

1. 构建方法见 [README](README.md#给开发者)。
2. 跑测试：`cd core && cargo test`，`cd reader && pnpm test`。CI 还会检查 Apple / Android 编译和英文词表，红了不合。
3. **每个缺陷变成一个测试。** 能在核心里复现的，就写在核心里。
4. **认人、对齐的错例加进考卷**（`fixtures/speakers/`、`core/yuyue-core/src/audiobook/eval.rs`）。
5. 改了界面或真机行为的，照 [docs/dev/testing.md](docs/dev/testing.md) 相关几节走一遍，在 PR 里写走了哪几条、在什么设备上。“编过”不等于“做完”。改了书架、阅读条、设置或 App 自己画的弹层，按 testing.md「界面快照」重录 PNG。系统权限框、Sparkle 安装窗、网页书页仍用 `scripts/window-catalog.sh`。
6. 界面文案以中文为键，同时补上英文。颜色、字号、间距改 `design/tokens.json`，再运行 `node design/generate.mjs`。
7. 多端都有的功能，各端一起对齐；非界面逻辑优先下沉到 `core/yuyue-core`，新增或改动功能同步更新 [docs/parity.yaml](docs/parity.yaml)（详见 [docs/dev/multi-platform-architecture.md](docs/dev/multi-platform-architecture.md)）。
8. 跑多端一致性校验：`node scripts/check-parity.mjs`、`cd reader && pnpm schema:check`、`node e2e/midscene/scripts/validate-flows.mjs`、`node scripts/sync-webdav-e2e.mjs`。
9. 提交信息用英文，提交和 PR 标题照 [docs/dev/commits.md](docs/dev/commits.md)：`feat(drama): pick the reference clip by tone`。克隆后运行一次 `git config core.hooksPath .githooks`，提交时会自动检查。

## 许可

代码以 GPL-3.0 发布。新带进来的模型、字体、素材，要在同一个 PR 里写进 [NOTICE](NOTICE)。

注意：MMS 对齐模型和 ZipVoice 的预训练权重只许非商用。它们不在仓库里，App 用到时才下载，详见 NOTICE。
