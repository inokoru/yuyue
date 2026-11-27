# 提交规范

一条提交只说一件事。读历史的人只看标题，就该知道这件事是什么；想知道为什么，再看正文。

格式照 [Conventional Commits 1.0](https://www.conventionalcommits.org/zh-hans/v1.0.0/)，整条提交信息用英文写。

```
<类型>(<范围>): <描述>

<正文：为什么改，之前哪里不对，换了什么做法>

<脚注：Closes #12 / BREAKING CHANGE: … / Co-authored-by: …>
```

## 标题

```
feat(drama): pick the reference clip by tone
fix(android): keep making the whole book with the screen off
perf(core): find anchors by longest increasing subsequence
```

- **描述**：祈使句、小写开头，说这次提交做了什么：`add`、`fix`、`drop`，不写 `added`、`fixes`。不加句号，不写 `update code`、`some fixes` 这类空话。
- **长度**：整行不超过 72 个字符，最好在 50 以内。
- **破坏兼容**：在冒号前加 `!`，比如 `feat(sync)!: sync progress by sentence id`。正文或脚注里写上 `BREAKING CHANGE:`，说明旧数据怎么办。改了同步格式、书库数据库、核心对各端的接口，都算破坏兼容。

### 类型

| 类型 | 什么时候用 |
|---|---|
| `feat` | 用户能看到、能用到的新东西 |
| `fix` | 修好一个用户能碰到的毛病 |
| `perf` | 更快、更省电、更省内存，行为不变 |
| `refactor` | 换写法，行为和性能都不变 |
| `test` | 只动测试和考卷 |
| `docs` | 只动文档、方案、注释 |
| `build` | 编译、依赖、打包、模型下载地址 |
| `ci` | 只动 `.github/` |
| `chore` | 其余杂事；发版用 `chore(release)` |
| `revert` | 撤回一条提交，描述照抄原标题 |

拿不准的时候，问自己：用户会不会因为这条提交而有所察觉？会，就是 `feat` 或 `fix`。

### 范围

范围可以不写；写就只写一个，从下面挑。

| 范围 | 指什么 |
|---|---|
| `core` | Rust 核心（`core/`） |
| `reader` | 网页书页（`reader/`） |
| `apple` | Mac 和 iPhone 共用的部分 |
| `mac` · `ios` · `android` · `windows` | 只改了这一端 |
| `web` | 网页版 |
| `drama` · `audiobook` · `voices` | 广播剧、有声书、声音（跨了几层的功能） |
| `sync` · `update` · `l10n` | 同步、检查更新、词表和翻译 |
| `design` | 设计令牌（`design/`） |
| `release` | 发版 |

只动了一端，写那一端；一个功能横跨核心和几端，写功能名。

## 正文

- 和标题之间空一行，每行折到 72 个字符。
- 写**为什么**：之前哪里不对、为什么选这个做法、放弃了什么。代码改了什么，diff 已经说了。
- 有实测数字就写数字：`Chapter 8: 0 wrong, unsure down from 8 to 1.`

## 脚注

| 写法 | 用途 |
|---|---|
| `Closes #12` | 合并后关掉这个 Issue |
| `Refs #12` | 有关系，但不关掉 |
| `BREAKING CHANGE: …` | 破坏兼容，说明旧数据怎么迁 |
| `Co-authored-by: 名字 <邮箱>` | 一起写的人 |

## 一条提交多大

- **一件事一条**。顺手修的别的毛病，另提一条。
- **各端一起改**。同一个功能的核心、Apple、Android、Windows 尽量放在同一条提交里，不要长期只改一端；暂缓端同步记入 `docs/parity.yaml`。
- **每条都能编过、测试都过**。往回找毛病（`git bisect`）时，每一步都要能用。
- 格式化、改名、挪文件单独一条，不和逻辑改动混在一起。

## 分支、合并、标签

- **分支**：`<类型>/<几个英文词>`，比如 `feat/drama-tone`、`fix/android-screen-off`。
- **合并**：PR 一律压成一条合并（Squash and merge），历史保持一条直线。PR 标题就是这条提交的标题，也要合规范，CI 会查。
- **标签**：
  - 正式版 `v0.1.0`
  - 测试版 `v0.1.0-beta.1`，在 GitHub 上标成预发布，App 检查更新不会查到它
  - 模型 `models-1`，App 里写死了这个地址，发出去就不再移动、不删
- **发版提交**：`chore(release): 0.1.0`，只改版本号、编号和 `CHANGELOG.md`。

`CHANGELOG.md` 是写给用户看的，手写，不从提交里生成。

## 检查

`scripts/check-commit-msg.sh` 检查标题是不是合规范，本机提交时和 CI 用的是同一份。克隆后运行一次：

```sh
git config core.hooksPath .githooks
```

之后每次提交都会先检查标题，不合规范就提交不了。`git commit --amend`、`git rebase -i` 时修改的标题也会被检查。
