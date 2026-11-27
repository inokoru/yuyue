# README 截图和宣传短片

给 README 和一条 30–45 秒竖屏宣传片用的素材。各端一条命令，Hei 在自己的 Mac 和 Windows 上跑。脚本只用隔离的演示书库，不打开、不改、不删真实书库。

拍完不用剪。短片是几段几秒钟的原始画面，剪辑表在下面。

## 一条命令

在仓库根目录：

```sh
# Mac 应用（需要本机 Debug 构建）
scripts/capture-readme.sh macos

# iPhone 模拟器
scripts/capture-readme.sh ios

# iPad 模拟器
scripts/capture-readme.sh ipad

# Android 模拟器（Mac / Windows / Linux 都行，只要有 adb）
scripts/capture-readme.sh android

# 真机要显式加上 --device。脚本先备份 app 私有目录，拍完还原。
scripts/capture-readme.sh android --device
```

Windows 应用在 PowerShell 里：

```powershell
pwsh scripts/capture-readme.ps1
```

每条命令拍完会试着跑合成。全部拍完再跑一次，把各端并到同一套图里：

```sh
python3 scripts/readme-shots.py compose
```

只要截图、或只要短片：加上 `--shots` 或 `--clips`（Windows 用 `-Shots` / `-Clips`）。演示书库已经种好、只想重拍时加 `--keep`（Windows `-Keep`）。

## 准备

| | 需要 |
|---|---|
| 各端 | Python 3。脚本一开始就检查 Pillow，没有就退出，不会去截空书架。安装：`python3 -m pip install --user pillow`（Mac 上 pip 不可用时 `brew install pillow`；Windows：`py -m pip install pillow`）。要录短片还要有 ffmpeg（Mac：`brew install ffmpeg`；Windows：`winget install Gyan.FFmpeg`），只截图可加 `--shots` / `-Shots`。 |
| Mac 应用 | Xcode。Debug 构建：`cd apple && xcodegen && xcodebuild -project Yuyue.xcodeproj -scheme Yuyue -configuration Debug -destination 'platform=macOS' -derivedDataPath build/dd build`。系统外观用浅色。终端需要「辅助功能」，短片里的点击才点得下去。若看到 `Window move completed without beginning`，那是系统在窗口还没开始移动时收到一次定位的警告，窗口仍会打开，可以忽略。 |
| iPhone / iPad | 同一套 Xcode。模拟器 Debug 包：`xcodebuild ... -destination 'platform=iOS Simulator,name=iPhone 16' -derivedDataPath build/sim build`。脚本自己选已安装的 iPhone 16 或 iPad Pro 11 寸，把状态栏设成 9:41，拍完恢复。 |
| Android | 已安装的 **Debug** 包（`assembleDebug`），这样 `run-as` 才能进应用目录。优先模拟器。 |
| Windows | 已安装的官方 **0.1.7**（`%LOCALAPPDATA%\Programs\Yuyue\Yuyue.exe` 或开始菜单里的安装位置）。没有安装包时用本机 Release / Debug。短片另要 ffmpeg（`winget install Gyan.FFmpeg`）。截图不需要 ffmpeg。 |

界面语言是简体中文。Mac / 模拟器启动参数里带了 `-AppleLanguages "(zh-Hans)"`。Windows 写在演示书库的 `windows-prefs.json` 里。

已经编好的 `.app` 可以用 `APP=/绝对路径/Yuyue.app` 指给脚本。Windows 用 `-Exe`。

## 演示书库

`python3 scripts/readme-shots.py books` 写出正好 14 本，7 本中文、7 本英文。中文是简体全文，来自维基文库（抓取脚本会把繁体转成简体）：野草、朝花夕拾（鲁迅），呼兰河传（萧红），浮生六记（沈复，含文库所收的后两卷），人间词话（王国维），志摩的诗（徐志摩；文库《志摩的诗》分类下的诗，沙扬娜拉用初版那一组），世说新语（刘义庆，含刘孝标注）。英文来自 Project Gutenberg 或维基文库：The Great Gatsby、Pride and Prejudice、Mrs Dalloway、Walden、Meditations（Marcus Aurelius，George Long 译本）、Dubliners、The Prophet。正文存在 `fixtures/readme-shelf/`，不在拍摄时现下。

《了不起的盖茨比》和《傲慢与偏见》连封面原样保留。其余 12 本是衬线排的纸面或布面，不是界面稿。书架上没有《背影》，也没有《红楼梦》。

在读是呼兰河传、Walden、盖茨比。读完里有朝花夕拾、人间词话、野草。同步页不拍：没有真实网盘就不做成「已经开着同步」的样子。

要先看封面，可以 `python3 scripts/readme-shots.py covers`，图在 `build/readme-shots/covers/`。

中文阅读和听书都用《呼兰河传》里祖父的园子。「花开了，就像花睡醒了似的。」是那一节的最后一句，划在这句上，笔记是「花是自己醒的。」听完这一句会翻到下一节（「鸟飞了，就像鸟上天了似的。」）。各端打开同一句，写在书库旁的 `position.json` 里。英文那张截图打开 Walden。

## 输出

| | 路径 |
|---|---|
| Mac 截图 | `docs/screenshots/raw/macos/01-shelf.png` `02-listening.png` `03-notes.png` `04-english.png` |
| Windows 截图 | `docs/screenshots/raw/windows/` 同名 |
| iPhone 截图 | `docs/screenshots/raw/iphone/01-shelf.png` `02-reading.png` `03-listening.png` `04-notes.png` |
| iPad 截图 | `docs/screenshots/raw/ipad/01-shelf.png` `02-reading.png` |
| Android 截图 | `docs/screenshots/raw/android/01-shelf.png` `03-listening.png` |
| 短片 | `docs/screenshots/clips/<macos\|windows\|iphone\|ipad\|android>/` |
| 合成后的 README 图 | `docs/screenshots/composed/` |
| 临时书库 | Mac：`build/readme-shots/library`。模拟器：该 App 容器里的 `readme-lib`（不是「应用支持」里的真书库）。Windows：`%LOCALAPPDATA%\YuyueReadmeShots\library`。Android：只动 `app.yuyue` 的私有目录，先备份再还原。 |

窗口大小：Mac 书架约 1320×900 点，阅读窗约 1200×820 点，ScreenCaptureKit / `screencapture` 按系统倍率存。Windows 用同样的 DIP，像素跟着显示器缩放，合成时会拉到和 Mac 一样高。iPhone / iPad 用模拟器屏幕原尺寸，状态栏 9:41。Android 用设备的 `wm size`，不改分辨率。

点不准时可以改这几个比例（0 到 1，Android 也认 0–100 的百分数）：

```sh
YY_TAP_BOOK_X=0.28 YY_TAP_BOOK_Y=0.34 \
YY_TAP_SENTENCE_X=0.50 YY_TAP_SENTENCE_Y=0.42 \
scripts/capture-readme.sh ios --clips
```

## 短片分镜

每段都是真应用。光标只在要点、要拖、要滑的时候留在画面里。模拟器打开了单指触点。不录桌面其他窗口。

| 文件 | 大约 | 画面 |
|---|---|---|
| `01-shelf-scroll` | 5 秒 | 浅色书架，在读几本，下面格子是满的。向下滚，封面陆续过去。 |
| `02-listen-follow` | 9 秒 | 打开《呼兰河传》「花开了，就像花睡醒了似的。」这一句。Mac / Windows 右键「从这句读」；手机长按后点「从这听」；Android 用调试参数从这一句开始念。墨线跟着走，这一句念完翻到下一节。 |
| `03-highlight-note` | 11 秒 | 在这一句上划线，按 `1` 划成黄色，点「想法」，贴上「花是自己醒的。」Mac、Windows、iPhone 会做这一段。Android 的输入法贴不进中文，这一段不在 Android 上录。 |
| `04-open-same-position` | 6 秒 | 从书架打开《呼兰河传》，停在 `position.json` 里的那一句。桌面一段、手机一段，剪的时候对在一起。 |

帧率：Windows 的 ffmpeg `gdigrab` 用 60 fps。Mac 的 `screencapture -v` 跟系统录屏（当前系统一般是 60）。模拟器的 `xcrun simctl io recordVideo` 在支持 `--fps` 时用 60，否则用模拟器自己的帧率。Android `screenrecord` 跟屏幕刷新，多数是 60 或 30，脚本不假装能强制 60。

## 合成

`scripts/readme-shots.py compose` 只拼已经拍到的 PNG：暖纸底、窗口本身的圆角、一层淡阴影。书架下半截如果还是空的，会裁掉再圆一次角。Mac 和 Windows 的书架并排；听书和笔记并排；iPhone 和 Android 并排。某一端还没拍，那一张就不出现，README 里留一句它该放在哪。旧的 `docs/screenshots/01`–`11` 还在，作为还没重拍时的底。同步那张（「尚未开启同步」）不再放进 README。

现在仓库里的合成图，用的是已经提交的 Mac / iPhone 截图裁过、垫过纸。Windows、iPad、Android 重拍之后再跑一次 `compose`，同一批文件名会换成并排的新图。
