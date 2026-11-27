# 手动测试

自动测试（`cargo test`、`pnpm test`）管不到界面和真机行为。改了界面、播放、同步、导入这类东西，提 PR 前照下面相关的几节走一遍，在 PR 里写上走了哪几条、在什么设备上。

每条写“对 / 不对 + 看到了什么”，不对的截图。

## 准备

测试材料在 `fixtures/books/`：

| 文件 | 用途 |
|---|---|
| `hongloumeng.epub` | 中文长书，有对话，测分角色和广播剧 |
| `great-gatsby.epub`、`pride-and-prejudice.epub` | 英文，测外文功能 |
| `beiying.txt`、`beiying-gbk.txt`、`beiying.epub` | 同一篇短文的三种格式；GBK 测编码识别 |
| `edge-cases.epub` | 各种怪排版 |
| `sample.pdf` | PDF |
| `dash-dialogue.txt` | 用“——”和“某某：”写对话的书 |
| `empty.txt`、`broken.epub` | 空文件、坏文件 |

有声书录音可以用 macOS 自带的语音生成一段：

```sh
say -v Tingting -f fixtures/books/beiying.txt -o /tmp/beiying.m4a --file-format=m4af
```

**不要用自己的真书库测。** Mac 与 Windows 都可以指定一个临时书库（空文件夹就是新装的样子）：

```sh
# Mac（调试版）
open -n <调试版 .app 路径> --args -debugLibrary <一个空文件夹>

# Windows（Debug / Release 均支持）
Yuyue.exe --library <一个空文件夹>
```

Android 用模拟器或测试机，文件用 `adb push <文件> /sdcard/Download/` 推进去再从 App 里导入。跨端自动化主线验收另见 [e2e/midscene/README.md](../../e2e/midscene/README.md) 与 Windows 正式版视觉套件 [tests/midscene/README.md](../../tests/midscene/README.md)。

## 界面快照

书架、阅读条、设置、App 自己画的弹层，用快照库对着 PNG 看，不必把整个 App 拉起来。这一轮只覆盖这些代表画面，不是每个窗口。

两条路，不要混：

| | 界面快照 | 全窗口图册 |
|---|---|---|
| 看什么 | App 里用 SwiftUI / Compose 画的屏幕和弹层 | 系统权限框、Sparkle 安装窗、网页书页（WebView 里的正文） |
| 怎么画 | 夹具数据，单独把那个视图画出来 | `scripts/window-catalog.sh` 启动完整的 Mac App，用调试参数一扇窗一扇窗截 |
| 库 | Apple：`pointfreeco/swift-snapshot-testing`。Android：Compose Preview Screenshot Testing（`com.android.compose.screenshot`） | `scripts/window-catalog.sh`、`scripts/window-gallery.py`、`DebugShots.swift` |

`apple/Yuyue/App/Snapshots.swift`、`apple/Yuyue/Phone/PhoneSnapshot.swift`、`core/yuyue-core/src/snapshot.rs` 是**数据**快照（导出书和划线），不是这套图。

正文是网页控件排的，快照库画不出那一页。阅读条的图底下垫了一段《故乡》的夹具文字，只为了看玻璃条；真书页仍用全窗口图册。

图放在仓库里，用系统的看图或下面的网页对着看：

- Apple：`apple/YuyueSnapshotTests/__Snapshots__/ScreenCatalogTests/`
- Android：`android/app/src/screenshotTestDebug/reference/`（文件名是预览函数的全名加参数摘要）

```sh
python3 scripts/snapshot-gallery.py
# 打开 build/snapshot-catalog/index.html
```

全窗口图册的网页还是 `python3 scripts/window-gallery.py`。

### Apple

在 Mac 上，先按 README 编过核心和书页，再生成工程。模拟器和 macOS 各跑各的，图不要混用：换了一台 Mac、换了一个模拟器，像素会对不上，在那台机器上重录。

```sh
cd apple && xcodegen
# 缺图就记下，这一趟会失败；再跑一次才拿新图比对。
xcodebuild test -project Yuyue.xcodeproj -scheme Yuyue \
  -destination 'platform=macOS' \
  -only-testing:YuyueSnapshotTests \
  CODE_SIGNING_ALLOWED=NO
# 模拟器名字以本机为准：xcrun simctl list devices available
xcodebuild test -project Yuyue.xcodeproj -scheme Yuyue \
  -destination 'platform=iOS Simulator,name=iPhone 16' \
  -only-testing:YuyueSnapshotTests \
  CODE_SIGNING_ALLOWED=NO
```

```sh
TEST_RUNNER_YY_SNAPSHOT_RECORD=all xcodebuild test -project Yuyue.xcodeproj -scheme Yuyue \
  -destination 'platform=iOS Simulator,name=iPhone 16' \
  -only-testing:YuyueSnapshotTests \
  CODE_SIGNING_ALLOWED=NO
```

整套重录（界面故意改了）：在 `xcodebuild test` 前设置 `TEST_RUNNER_YY_SNAPSHOT_RECORD=all`（`xcodebuild` 只会把 `TEST_RUNNER_` 前缀的环境变量传进 XCTest 进程）。只补缺的图不用设这个变量，库会把还没有的 PNG 写下来，然后这次测试失败，再跑一次就对着新图比。

### Android

主机上用 layoutlib 画 `@Preview`，不需要模拟器。先编核心和书页（和打 debug 包一样）。参考图是在哪台系统上录的，比对也在那台系统上：Mac 和 Linux 的中文字形会差一截，换了系统就重录，不要拿两边的 PNG 互相指。

```sh
cd android
./gradlew :app:updateDebugScreenshotTest    # 写出 / 更新 reference PNG
./gradlew :app:validateDebugScreenshotTest  # 再画一遍，和 reference 比
```

对不上时，HTML 报告在 `android/app/build/reports/screenshotTest/preview/debug/index.html`（Reference / Actual / Diff）。

Android Studio 里，`@PreviewTest` 旁边的运行按钮也能录、也能比。

## 一、读书（Mac / Windows / Android / iPhone 都要）

| # | 做什么 | 应该 |
|---|---|---|
| 1 | 第一次打开 | 书架是空状态，有导入入口；不一次要一堆权限 |
| 2 | 导入 hongloumeng.epub、great-gatsby.epub、beiying-gbk.txt、sample.pdf；Mac 上从访达拖一本进窗口，Android 从别的 App 分享一本进来 | 都进书架，封面、书名对，GBK 不乱码 |
| 3 | 导入 empty.txt、broken.epub | 空文件提示“文件是空的”；坏文件说打不开，不进书架、不崩 |
| 4 | 同一个文件再导一次 | 不重复 |
| 5 | 打开红楼梦：翻页、目录跳章、书内搜索、全局搜索 | 跳得准；搜索显示“第 x / n 处”；跳走后有“回到原处” |
| 6 | 排版：纸色、字体、字号、行距、单栏 / 双栏，新建一张“我的纸” | 立即生效；关掉重开还在 |
| 7 | 选一段字划线、换色、写想法 | 都在；Mac 上选中时按空格是朗读，不翻页 |
| 8 | 笔记页按时间 / 按书看，导出 Markdown、导出到 Obsidian（选一个空文件夹） | 文件里有刚才的划线和想法 |
| 9 | 外文：盖茨比里点词、整句翻译、加生词本 | 有释义；没装系统语言包时有提示，开关复位 |
| 10 | 读到中间退出再开；连着快翻 10 页后立刻退出再开 | 两次都回到离开的地方 |
| 11 | 把书标成想读 / 在读 / 读过，导出阅读记录 CSV | 状态和书架一致；CSV 能用表格打开 |
| 12 | 删一本书，点撤销 | 能找回；Mac 上鼠标停在撤销条上时倒计时暂停 |
| 13 | 数据快照：生成一份 → 删两本书 → 导入快照选“合并” | 书、进度、划线回来；另一个平台生成的快照也能导入 |
| 14 | 切英文、深色模式、系统字号调到最大，走一遍书架、阅读、笔记、设置 | 没有中文残留、没有看不清的字、版面不撑破 |
| 15 | 读屏（VoiceOver / TalkBack）：打开书、翻页、朗读 | 每个按钮都有名字 |

平台特有的：

- **Mac**：⌘W 关掉所有窗口后点程序坞图标，书架回来；从访达双击 EPUB 能打开；菜单栏图标能点开。改了书架、阅读条、设置或 App 自己画的弹层，按上面「界面快照」重录 PNG 对着看。系统权限框、Sparkle 安装窗、网页书页仍跑 `scripts/window-catalog.sh`。
- **Windows**：从资源管理器双击 `.epub` / `.mobi` 能打开；切换深浅色与中英文后书架、阅读页、设置页无截断与对比度问题；关闭重开后上次书架标签与阅读位置完整恢复；发版前建议同时跑完 `tests/midscene`（`pnpm run test:release`）。
- **Android**：双击屏幕左右边缘进出沉浸阅读；音量键翻页；预测式返回和系统返回手势回到上一层，不直接退出；横竖屏和分屏版面不乱。
- **iPhone**：装好后先彻底退出再开一次。

## 二、听书

| # | 做什么 | 应该 |
|---|---|---|
| 1 | 系统声音朗读：开始、逐句高亮、自动翻页、上一句 / 下一句、调速度 | 高亮跟着声音走 |
| 2 | 睡眠定时选“本章结束” | 到章末停 |
| 3 | 朗读几句后暂停，退出再开 | 回到正在读的那句 |
| 4 | 关掉书继续听；锁屏、通知栏、耳机按键 | 都能控制 |
| 5 | 新书库首次点朗读与离线语音就绪 | 首次可立刻用免 Key 在线语音或系统声音出声；离线语音（Kokoro / 声音卡）下载就绪后弹出提示球，由用户确认切换，异常或发烫时绝不擅自换声 |
| 6 | 声音列表 | 分组对，试听正常 |
| 7 | 连念 30 分钟 | 不卡、不漏句、不重复 |
| 8 | 断网时点朗读（本机声音没装） | 系统声音照常，不报错、不死等 |
| 9 | 本机克隆：录 10 秒 → 试听 → 用它念一章 | 能用；录音不合格时提示清楚 |

本机声音模型要从 Releases 下载。要测本地打包的模型，在模型所在文件夹 `python3 -m http.server 8765`，然后：

- Mac：启动参数加 `-debugLiveVoiceURL http://127.0.0.1:8765/live-zipvoice-1.zip -debugVoicePackURL http://127.0.0.1:8765/voices-1.zip`
- Android：把两个 zip 推到 `/sdcard/Download/`，启动时加 `-e liveVoicePath <zip 路径> -e voicePackPath <zip 路径>`

## 三、分角色和广播剧

| # | 做什么 | 应该 |
|---|---|---|
| 1 | 打开红楼梦第三回 | 对话气泡对 |
| 2 | 给一个人物换声音 | 下一句生效 |
| 3 | 打开 dash-dialogue.txt | “——”和“某某：”开头的句子成气泡 |
| 4 | 广播剧：一回从头听到尾 | 带环境音；关掉环境音后下一句没有底 |
| 5 | 导出 m4b | 在“图书”等播放器里能看到章节 |

## 四、有声书

| # | 做什么 | 应该 |
|---|---|---|
| 1 | 导入录音，和书架上的电子书配对 | 配对页认出是哪本书 |
| 2 | 点“合成”（第一次会下载约 270 MB 的对齐模型） | 有下载进度，下完自动开始；断网再连会报失败、能重试 |
| 3 | 合成完打开听，跳到中间一章 | 高亮跟着声音走 |
| 4 | 录音里没念的地方（插图说明、注释、目录） | 高亮直接跳过 |
| 5 | 拖一句的起点微调 | 改了就生效 |
| 6 | 把几集录音打包成一个 zip 拖进来 | 当成一本，集的顺序对，名字是 zip 的名字 |
| 7 | 只导录音不配书 | 耳机下一首 / 上一首各跳 15 秒；暂停 3 分钟再放，退回一句开头 |

Android 上的转写要 SenseVoice 模型，本地测试时启动加 `-e asrDir <模型文件夹>`。

## 五、同步

两台设备连同一个同步文件夹（WebDAV、iCloud Drive 或 Google Drive）。

| # | 做什么 | 应该 |
|---|---|---|
| 1 | 一台读到某处、划一条线 | 另一台进度和划线都到 |
| 2 | 两台都改同一条想法，再同步 | 两份都留，让人选 |
| 3 | 有声书：A 合成好，B 打开“有声书录音”同步并连 Wi-Fi | B 不下载模型、不重新合成，高亮直接能用 |
| 4 | A 微调一句的起点 | B 上那一句也改了 |
| 5 | B 删掉一本，再同步 | 不会自己又冒出来；A 不受影响 |
| 6 | 用蜂窝网络 | 不传录音 |

## 六、播客、书伴和导出

要先接一个模型（设置 › 书伴）。

| # | 做什么 | 应该 |
|---|---|---|
| 1 | 配一档节目，点“现在写一集” | 一步步显示进度，几分钟内写完，出现在“最新”里 |
| 2 | 听这一集 | 两个主持人声音不同 |
| 3 | 把节目时间改成 25 分钟后（会提前 20 分钟开始写），⌘W 关掉所有窗口 | 菜单栏有图标；到时弹通知“新一集写好了”，点了直接播 |
| 4 | 合上盖子 30 分钟再打开 | 醒来一两分钟内补写 |
| 5 | 没接模型时打开播客页 | 有提醒“到点写不成” |
| 6 | 另一台设备 | 剧集同步过来，能播，进度记着 |
| 7 | 书伴问一句 | 回答带原文出处，点了能跳 |
| 8 | 导出到 Notion、flomo、Anki、CSV | 各发一次都到 |
| 9 | Wi-Fi 传书：电脑浏览器传一本进手机 | 进书架 |

## 七、软件更新

1. 把 `apple/project.yml`、`android/app/build.gradle.kts` 与 `windows/Yuyue/Yuyue.csproj` 的版本临时改高一档，运行 `RELEASE_BASE=http://localhost:8000 scripts/publish-release.sh --local`（Windows 另跑 `windows/package-release.ps1 -BuildInstaller` 生成 `windows-update-<arch>.json`），再把版本改回去，装上旧版。
2. `cd dist/release-<新版本> && python3 -m http.server 8000`
3. Mac：启动参数加 `-YYUpdateBase http://localhost:8000/`，检查更新并安装。应该验签通过，重开后是新版本，书和划线都在。
4. Android 模拟器：启动加 `-e updateBase http://10.0.2.2:8000/`，检查更新并安装。数据都在。
5. 稍后提醒、跳过此版本、中英文更新日志都对。

## 八、坏情况

- 下载模型时断网再连：报失败，能重试。
- 磁盘快满时合成有声书：提示空间不够，不崩。
- 真机才测得准的：翻页手感、耳机按键、锁屏、墨水屏。没测的在 PR 里写明。
