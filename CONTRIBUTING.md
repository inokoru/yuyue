# 参与语阅

大的改动请先开 Issue 讨论。

各部分的设计方案在 [docs/plan/](docs/plan/)：

| 方案 | 内容 |
|---|---|
| [tech.md](docs/plan/tech.md) | 整体技术方案：原生界面 + Rust 核心 + 网页书页 |
| [features.md](docs/plan/features.md) | 功能清单和不做的功能 |
| [audiobook.md](docs/plan/audiobook.md) | 有声书导入、听读对齐、对照表、播放器 |
| [drama.md](docs/plan/drama.md) | 分角色认人、导演稿、选角、合成、混音 |
| [voices.md](docs/plan/voices.md) | 声音卡、两层合成、克隆、自带声音包 |
| [updates.md](docs/plan/updates.md) | 更新日志、三端检查更新、发布流程 |
| [ROADMAP.md](ROADMAP.md) | 开发路线 |

## 提 PR

1. 构建方法见 [README](README.md#构建)。
2. 跑测试：`cd core && cargo test`，`cd reader && pnpm test`。CI 还会检查两端编译和英文词表，红了不合。
3. **每个缺陷变成一个测试。** 能在核心里复现的，就写在核心里。
4. **认人、对齐的错例加进考卷**（`fixtures/speakers/`、`core/yuyue-core/src/audiobook/eval.rs`）。
5. 改了界面或真机行为的，照 [docs/dev/testing.md](docs/dev/testing.md) 相关几节走一遍，在 PR 里写走了哪几条、在什么设备上。“编过”不等于“做完”。改了书架、阅读条、设置或 App 自己画的弹层，按 testing.md「界面快照」重录 PNG。系统权限框、Sparkle 安装窗、网页书页仍用 `scripts/window-catalog.sh`。
6. 界面文案以中文为键，同时补上英文。颜色、字号、间距改 `design/tokens.json`，再运行 `node design/generate.mjs`。
7. 两端都有的功能，两端一起改。
8. 提交信息用英文，提交和 PR 标题照 [docs/dev/commits.md](docs/dev/commits.md)：`feat(drama): pick the reference clip by tone`。克隆后运行一次 `git config core.hooksPath .githooks`，提交时会自动检查。

## 许可

代码以 GPL-3.0 发布。新带进来的模型、字体、素材，要在同一个 PR 里写进 [NOTICE](NOTICE)。

注意：MMS 对齐模型和 ZipVoice 的预训练权重只许非商用。它们不在仓库里，App 用到时才下载，详见 NOTICE。
