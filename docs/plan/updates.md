# 语阅 · 软件更新方案

> 2026-11-14 ｜ 现在发布是 `scripts/package-release.sh` 打出三样东西，放 GitHub Release：Mac 的 `.dmg`（临时签名，没有开发者证书）、iPhone 未签名的 `.ipa`、Android 的 `.apk`。以后上美区 App Store 和 Google Play。App 里还没有任何检查更新的代码。
> 要求：有新版本时告诉用户，更新的时候能看更新日志；平时也能翻以前每一版的日志。

---

## 一、定了的

1. **更新日志只写一份**：仓库根目录 `CHANGELOG.md`，中英两段。GitHub Release 的说明、Mac 更新窗口、Android 更新卡片、App 里的“更新日志”页都从它生成，不手抄第二遍。
2. **Mac 用 Sparkle 2**。这是 Mac 上非商店 App 的通用做法：后台查、下载、校验签名、退出替换、重开。更新包用 EdDSA 签名，不需要苹果开发者证书，和现在的临时签名能一起用。
3. **Android 的 GitHub 版自己更新**：查到新版后在 App 里下载 APK，交给系统安装器，系统会弹“要安装这个更新吗”。Google Play 版不带这段代码，由商店更新（Play 不允许 App 自己装 APK）。两版用 Gradle 的 `github` / `play` 两个 flavor 分开打。
4. **iPhone 不自己更新**。App Store 版交给商店；自己签名装 `.ipa` 的用户，App 只提示“有新版本”，附日志和下载页链接，装要用户自己重签。
5. **不需要服务器**。版本信息放在 GitHub Release 的附件里，用固定地址 `https://github.com/inokoru/yuyue/releases/latest/download/<文件>` 取，这个地址永远指向最新一版：
   - Mac：`appcast.xml`（Sparkle 的格式）
   - Android、iPhone：`update.json`
6. **查的频率**：打开 App 时查一次，之后每天最多一次。设置里有“自动检查更新”开关（默认开），旁边一个“现在检查”。只取一个小文件，不带任何设备信息，和“不收集任何数据”的隐私说明一致；隐私说明里补一句“会向 GitHub 查询有没有新版本”。
7. **商店版不自己查**：从 App Store / Google Play 装的版本，这些代码都不编进去（Apple 用编译开关，Android 用 flavor）。商店不许自带更新，商店本身也会提示更新。

---

## 二、更新日志怎么写

`CHANGELOG.md`，最新一版在最上面：

```markdown
## 0.2.0 · 2026-11-20

### zh
- 有声书没有对照表时，耳机上一首、下一首前后都是 15 秒。
- 广播剧换人说话时音量不跳。

### en
- Without a sync map, headphone previous / next jump 15 seconds either way.
- Volume no longer jumps when the speaker changes in a drama.
```

- 一版一节；标题是版本号和日期，版本号要和 `apple/project.yml`、`android/app/build.gradle.kts` 里的对上（打包脚本已经在核对这两处，再加上这一处）。
- 每条一句话，写用户能感觉到的变化，不写内部改动。语气和提交说明一样。
- 界面按系统语言显示中文或英文段，缺哪段就用另一段。
- 核心里写一个小解析器（`core/yuyue-core/src/changelog.rs`）：把 `CHANGELOG.md` 解析成 `Vec<Release { version, date, notes_zh, notes_en }>`，三端共用。打包时把整份 `CHANGELOG.md` 放进 App，断网也能看历史日志。

---

## 三、各端怎么做

### 3.1 Mac（Sparkle 2）

- 用 Swift Package 引入 Sparkle 2（`project.yml` 加包依赖），`Info.plist` 加：
  - `SUFeedURL` = `https://github.com/inokoru/yuyue/releases/latest/download/appcast.xml`
  - `SUPublicEDKey` = 公钥（第一次用 Sparkle 的 `generate_keys` 生成，私钥存在本机钥匙串，不进仓库）
  - `SUEnableAutomaticChecks` = YES，`SUScheduledCheckInterval` = 86400
- 菜单“语阅”下加“检查更新…”，在“关于语阅”下面一行。设置“通用”加“自动检查更新”开关、“现在检查”。
- **看日志**：appcast 里每一版的 `<description>` 放这一版的日志（生成成 HTML，中英两段用 `xml:lang` 分开，Sparkle 按系统语言挑）。Sparkle 自带的更新窗口上半部分就是日志，下面三个按钮：“安装更新”“跳过这个版本”“以后提醒我”。
- 窗口的样式先用 Sparkle 自带的，只做两件事：日志 HTML 套 App 的字体和纸色样式表（宋体标题、暖白底，夜间跟系统深色）。不重写更新窗口。
- **装更新**：下载完，Sparkle 校验 EdDSA 签名，用户点“安装并重新打开”后退出、替换、重开。如果正在念书或合成，先弹一张卡：“正在朗读，装好后从这句接着念”。进度本来就会保存，重开后接得上。
- 临时签名的 App 换成新版后，macOS 会把它当作同一个 App 吗？Sparkle 下载的文件不带隔离标记，不会再弹“无法验证开发者”。但钥匙串里存的 Key（云端声音、笔记导出的令牌）可能要重新允许一次，要实测（见第六节）。

### 3.2 Android（GitHub 版）

- `build.gradle.kts` 加 flavor：`github`（带更新、`REQUEST_INSTALL_PACKAGES` 权限）和 `play`（不带）。打包脚本默认打 `github`，传 `play` 时打 `.aab` 交商店。
- `update.json`：

  ```json
  {
    "version": "0.2.0",
    "code": 12,
    "date": "2026-11-20",
    "apk": "https://github.com/inokoru/yuyue/releases/download/v0.2.0/Yuyue-0.2.0-android.apk",
    "size": 58123456,
    "sha256": "…",
    "notes": { "zh": ["…"], "en": ["…"] }
  }
  ```

- 查：打开 App 时、每天最多一次，用 `versionCode` 比较。
- **看日志**：“我的”最上面出一张卡，“新版本 0.2.0”，下面列这一版的日志（最多显示 4 条，“全部”展开）；卡上两个按钮“更新”“以后”。跳过好几版的，日志把中间各版都按版本分段列出来（从 `update.json` 只能拿到最新一版，所以中间各版从下载来的新 `CHANGELOG.md` 里取。办法：`update.json` 再带一个 `changelog` 地址，指向 Release 附件里的 `CHANGELOG.md`）。
- **装**：用 `DownloadManager` 下到 App 自己的目录，显示进度；下完校验 sha256 和 APK 签名证书（必须和当前装的这版一样，不一样就不装）→ `PackageInstaller` 会话装上。第一次装时系统会要“允许安装未知应用”，卡片上用一句话说明并给跳转。
- 正在听书时不打断：下载在后台进行，下好以后卡片上写“已下好，点一下安装”。

### 3.3 iPhone

- App Store 版：什么都不做。
- 自己签名装的版本（编译开关 `SIDELOAD`，打 `.ipa` 时打开）：和 Android 一样读 `update.json`，有新版就在“我的”顶上出同样的卡，列日志；按钮是“去下载”（打开 Release 页面）和“以后”。

### 3.4 “更新日志”页（三端）

- 位置：Mac 在“关于语阅”窗口里加一行“更新日志”；iPhone、Android 在“我的 → 关于”里，版本号那行下面加“更新日志”。
- 内容：一版一段，版本号加日期作小标题，下面一行一条，用书页的排字（宋体、行距和纸色跟随设置）。当前版本标一个小字“现在用的”；查到的新版本排在最上面，标“新版本”。
- 装完新版第一次打开：不弹窗，只在“关于”那一行旁边点一个小点，看过就消失。（更新时已经看过日志了，不再打扰一次。）

---

## 四、发布流程（脚本化）

`scripts/package-release.sh` 后面加 `scripts/publish-release.sh`：

1. 检查 `CHANGELOG.md` 最上面一版的版本号和 `project.yml`、`build.gradle.kts` 一致，没写日志就停下。
2. 打 Mac `.dmg`、`.ipa`、`.apk`（已有）。
3. Mac：另外打一个 `.zip`（Sparkle 更新用 zip 比 dmg 稳），用 Sparkle 的 `sign_update` 签名；用 `generate_appcast` 生成 `appcast.xml`，再把日志写进每一版的 `<description>`。
4. 生成 `update.json`（版本、versionCode、APK 地址、大小、sha256、日志、`CHANGELOG.md` 地址）。
5. `gh release create v<版本>`：Release 说明直接用这一版的日志；附件是 dmg、zip、ipa、apk、`appcast.xml`、`update.json`、`CHANGELOG.md`。
6. 发布前先建一个草稿 Release，自己装上旧版点“检查更新”走一遍，没问题再改成正式。

---

## 五、分步

| 步 | 做什么 | 能不能不测就做完 |
|---|---|---|
| 1 | `CHANGELOG.md` 格式、核心解析器 + 单元测试；把已有提交整理成 0.1.0 的日志 | 能 |
| 2 | “更新日志”页三端（先出原型给你看） | 原型要你确认 |
| 3 | `publish-release.sh`：生成 `update.json`、`appcast.xml`、Release | 能写完；第一次发布要你同意（会公开） |
| 4 | Android flavor、查更新、更新卡、下载校验安装 | 能写完，装要真机试 |
| 5 | Mac 接 Sparkle、菜单项、设置开关、日志样式 | 能写完，替换要实测 |
| 6 | iPhone 自签版的提示卡 | 能 |
| 7 | 隐私说明补一句 | 能 |

---

## 六、要你测的

| 项 | 怎么测 | 过线 |
|---|---|---|
| Mac 更新一遍 | 装 0.1.0，发一个草稿 0.1.1，点“检查更新” | 窗口里看到 0.1.1 的日志；装完自动重开，书库、进度、设置都在 |
| Mac 钥匙串 | 更新前存过云端声音的 Key，更新后念一句 | 不弹钥匙串，或只弹一次 |
| Mac 正在朗读时更新 | 念书时点安装 | 先提示；重开后从这句接着念 |
| Android 更新一遍 | 真机装 GitHub 版旧版，发草稿新版 | 卡片显示日志；第一次引导“允许安装未知应用”；装完书库还在 |
| Android 签名不同 | 用调试密钥签的 APK 当新版 | 不装，提示“安装包签名不对” |
| 跳了几版 | 从 0.1.0 直接看到 0.1.2 | 日志按版本分段，0.1.1 和 0.1.2 都在 |
| 断网 | 关网打开“更新日志”页 | 历史日志照样能看；检查更新写“连不上，稍后再试” |

---

## 七、要你决定的（只有这一条）

- 第一次正式发布会把 dmg、apk、ipa 公开放到 GitHub Release，仓库要是公开的。现在仓库是否已经公开、什么时候开始用 Release 发版，你说了算；在那之前，我把第 1–7 步写完，只在本机用草稿 Release 测。

---

## 进度（2026-11-14）

用户定了：**一律只建草稿 Release**，在 GitHub 网页上点“发布”之前，App 都查不到它。

| 部分 | 状态 | 说明 |
|---|---|---|
| `CHANGELOG.md` | 已做 | 仓库根目录，写了 0.1.0 |
| 核心解析器 `changelog.rs` | 已做 | 解析日志、比版本号、取“这版之后的各版”、读 `update.json`；单元测试 5 个 |
| 命令行 | 已做 | `yuyue release-notes <日志> <版本> md|html-zh|html-en`、`yuyue update-json …` |
| `scripts/publish-release.sh` | 已做 | 核对版本号和编号（Mac 的 CURRENT_PROJECT_VERSION 和 Android 的 versionCode 必须一样，每版一起加一）→ 打包 → Sparkle 的 zip 和签名 → 中英两页日志（宋体、纸色、深色跟系统）→ `appcast.xml` → `update.json` → `gh release create --draft`。`--local` 只生成到 `dist/release-<版本>/`，本机试用 |
| 签名密钥 | 已做 | Ed25519；私钥在 `~/.config/yuyue/sparkle-ed25519.key`（不进仓库，**要备份**，丢了已装的 Mac 版就收不到更新），公钥写在 `apple/project.yml` |
| Mac：Sparkle 2.10.1 | 接上了 | 只给 macOS 链接；菜单“语阅 → 检查更新…”；沙盒里装更新用 Sparkle 的安装服务（entitlements 加了两个 mach-lookup）；`-YYUpdateBase` 指到本机文件夹 |
| Android | 逻辑已做，界面没做 | 不用 flavor，改用 `-Pplay`：打 Google Play 版时关掉 `SELF_UPDATE`、去掉安装权限（`src/play/AndroidManifest.xml`）。查（每天一次）、拿中间各版的日志、下载、校验 sha256 和签名证书、`PackageInstaller` 安装；`-e updateBase` 调试 |
| iPhone 自签版 | 逻辑已做，界面没做 | 打 `.ipa` 时加 `SIDELOAD`；查到新版只给日志和 Release 页链接 |
| 隐私说明 | 已做 | 网页、iPhone、Android 都补了“检查更新”一条（中英） |
| 界面 | 出了原型，等确认 | 设置“更新”一组、Mac 更新窗口里的日志页、更新日志页、“我的”顶上的新版本卡、“关于”里一行 |

和方案原文不同的地方：
- Android 不分 flavor，用 `-Pplay` 开关：不用改现有的打包、编译命令。
- Mac 更新前不另弹“正在朗读”卡片：Sparkle 正常退出 App，进度本来就随时存着，重开接着读。
- Mac 的 Key 存在沙盒里的文件里（不是钥匙串），更新后不会再要授权；但临时签名换了版本后，系统可能问一次“语阅想访问其他 App 的数据”，要实测。

本机试一遍更新（不碰 GitHub）：
1. 装好 0.1.0（编号 1）。
2. 把版本改成 0.1.1、编号 2，`CHANGELOG.md` 加一节。
3. `RELEASE_BASE=http://localhost:8000 scripts/publish-release.sh --local`
4. `cd dist/release-0.1.1 && python3 -m http.server 8000`
5. Mac：`open -a 语阅 --args -YYUpdateBase http://localhost:8000/`，点“检查更新…”。Android 模拟器：`RELEASE_BASE` 用 `http://10.0.2.2:8000`，装旧版后 `-e updateBase http://10.0.2.2:8000/`。
