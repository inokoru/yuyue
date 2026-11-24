# 更新日志 · Changelog

每一版一节，最新的在最上面。App 里的“更新日志”和更新提示都从这里来。

Each version has a section, newest first. The app's changelog page and update notices come from here.

## 0.1.3 · 2026-11-24

### zh
- 上下滚动拉动切章：在 Mac、iPhone / iPad 与 Android 的上下滚动阅读模式下，滑到章首或章末继续上拉/下拉即可顺滑切换上一章或下一章（支持触觉反馈、纸面拉动提示，以及读至全书末尾时的读完提示），并在滚动模式下统一纵向翻章动效。
- 国内更新与下载加速：新增国内高速更新镜像（CNB），中文环境下检查更新与下载安装包将优先走国内线路，网络不可用时自动回退至 GitHub。
- Mac 与 iPhone / iPad 性能优化：修复 macOS 设置页从“外观”切到“通用”时的卡顿，将语音合成、云同步与监听文件夹扫描等耗时操作移出主线程，并优化阅读目录缓存、纸张纹理渲染、PDF 内存回收与播客阅读页滚动流畅度。
- 版本号与更新状态修复：区分版本版次与显示版本号，升级到新版本后自动清理旧的更新检查残留状态。

### en
- Pull-to-cross chapters in scrolled mode: on Mac, iPhone / iPad, and Android, pull past the top or bottom of a chapter in vertical scrolled mode to cross smoothly into the previous or next chapter (with haptic arming, edge pull cues, vertical chapter transitions, and a finished-book prompt at book end).
- Faster China update mirror: added a China mirror (CNB) for release downloads and in-app updates, prioritized automatically for mainland Chinese locales with seamless fallback to GitHub.
- Mac & iOS performance improvements: fixed stutter when switching from Appearance to General in macOS Settings, moved speech synthesis, sync, and watched-folder scanning off the main thread, and optimized reader TOC caching, paper textures, PDF memory cleanup, and podcast reading scroll smoothness.
- Update state & version display fixes: separated release edition ordinal from the display version string and cleared stale update-check state after upgrading.

## 0.1.2 · 2026-11-24

### zh
- Android 打开书籍闪退：修复 0.1.1 版本在 Android 上点击书籍（EPUB、TXT、MOBI 等，包含旧版本已导入的书籍）进入阅读时直接闪退回桌面的问题。
- 部分 MOBI 无法导入：修复含长章节、旧版编码或特殊文件头的 MOBI 书籍导入失败的问题，并补齐 Android 打开 MOBI 文件的格式识别。
- 部分 EPUB 误报 DRM：修复正文实际未加密、仅残留加密标记或仅锁定内嵌字体的 EPUB 书籍（含常见网传及多看来源版本）导入时被误判为“有 DRM 无法导入”的问题。
- 其他稳定性修复：同步修复多项跨端朗读与语音下载、Wi‑Fi 传书、自定义字体、应用内更新、旧版苹果系统兼容以及界面与多语言细节问题。

### en
- Android book-opening crash: fixed a 0.1.1 regression on Android that crashed back to the home screen when opening books (EPUB, TXT, MOBI, etc., including books imported in earlier versions).
- MOBI import failures: fixed import failures on MOBI files with long chapters, legacy encodings, or older header layouts, and added Android file-type associations for opening MOBI files.
- False EPUB DRM warnings: fixed unencrypted EPUBs (such as files with leftover DRM metadata or encrypted/obfuscated embedded fonts) being wrongly rejected as DRM-protected on import.
- Other stability fixes: improved cross-platform read-aloud and voice downloads, Wi‑Fi transfer, custom fonts, in-app update installation, macOS 14 / iOS 17 compatibility, and UI/localization details.

## 0.1.1 · 2026-11-24

### zh
- Intel Mac 与旧系统支持：新增 Mac Intel（`x86_64`）安装包与独立更新通道；Mac 最低系统要求下调至 macOS 14（Sonoma），iPhone / iPad 下调至 iOS / iPadOS 17。
- 朗读声音增强：Mac、iPhone / iPad 与 Android 新增免 Key 的 Microsoft Edge 在线神经朗读来源（内置 22 个精选音色）；修复 Android 正式包下本机离线语音（Kokoro / 声音卡）闪退以及部分免费在线语音不可用的问题。
- EPUB 字体与外观跟随：修复部分 EPUB 书籍因内联字体规则或错误语言标记导致切换字体不生效、以及含空格/中文名导入字体无法加载的问题；阅读纸张支持随应用深浅外观自动切换。
- 稳定性与交互修复：修复 Mac 下载本机离线语音模型时的界面卡顿，修复各端听读切句与睡眠定时音量恢复、系统声音试听、有声书播放、OPDS / WebDAV 远程书库、应用内更新安装以及多处英文界面截断问题。

### en
- Intel Mac & broader Apple OS support: added Intel (`x86_64`) Mac builds with a dedicated Sparkle feed, and lowered minimum requirements to macOS 14 (Sonoma) and iOS / iPadOS 17.
- Read-aloud voices: added a keyless Microsoft Edge neural TTS provider (22 curated voices) on Mac, iOS and Android; fixed release-build crashes for offline ONNX voices (Kokoro / voice cards) on Android and broken free online routes.
- Reliable EPUB fonts & appearance matching: fixed publisher inline font rules and mislabeled language tags overriding reader font choices, fixed URL-encoded custom font filenames, and added automatic reading-paper matching with app light/dark appearance.
- Stability & UI fixes: eliminated macOS UI freezes during offline voice downloads, and fixed playback/sleep-timer volume recovery, system voice previews, audiobook streaming, OPDS / WebDAV remote libraries, in-app update handling, and English UI truncation across platforms.

## 0.1.0 · 2026-11-23

### zh
- 第一版：在 Mac、Android、iPhone 和 iPad 上读 EPUB、PDF、TXT、MOBI / AZW3（Windows 端开发中），支持 Wi‑Fi 传书、监听文件夹和 OPDS / WebDAV 远程书库，书和笔记都留在你自己的设备上。
- 逐句听读：用本机离线声音、系统声音或你自己接的云端声音朗读，墨线一句一句跟着走、自动翻页；锁屏和耳机都能控制，听到一半拿起来看正好停在同一行。
- 播客与有声空间：支持订阅外部播客（RSS、OPML、单集链接）或导入有声书与长音频，排成可查词、可划原声切片的阅读页（有声书录音对齐、分角色朗读与书房电台等进阶能力亦在持续打磨中）。
- 书伴与外文精读：外文书支持离线查词、行间难词简注、双击整句翻译与生词本；书伴可接端侧模型、本机或局域网模型（Ollama、LM Studio 等）或自有大模型 API，默认只在已读过的部分里回答，每句都指回原文。
- 记书与导出：多样式划线、想法、今日回顾与书摘卡片；记录在读、读过、想读（纸书也能记）；支持导出到 Markdown、Obsidian、Notion、flomo、新枝、Anki、带划线的 EPUB 和阅读记录 CSV。
- 同步与快照：用 iCloud Drive 或 WebDAV 在设备之间同步进度、笔记和播客（冲突并排可自选，Google Drive 即将支持），支持整库数据快照导入导出。
- 应用内反馈：遇到问题或有建议可在书架、“我的”或菜单栏直接提交；正文与公开截图会发到 GitHub 问题板，不收集邮箱，非公开附件先在本机加密再发出。

### en
- First release: read EPUB, PDF, TXT and DRM-free MOBI / AZW3 on Mac, Android, iPhone and iPad (Windows version in development), with Wi‑Fi transfer, watched folders and OPDS / WebDAV remote libraries; your books and notes stay on your own devices.
- Sentence-by-sentence read-aloud: listen with local offline voices, system voices or a cloud voice you connect, tracked line by line with automatic page turns and lock-screen / headphone controls.
- Podcasts and audiobooks: subscribe to podcasts (RSS, OPML, episode links) or import audiobooks into readable pages where you can look up words and save audio clips (forced alignment, character voices and studio radio continue in active polish).
- Foreign reading and companion: offline dictionary lookup, interlinear glosses, sentence translation and word list; connect an on-device model, a local LAN server (Ollama, LM Studio) or your own API key for spoiler-free answers grounded strictly in what you have read.
- Notes, book tracking and export: multi-style highlights, thoughts, daily review and quote cards; track reading, read and to-read books (paper books included); export to Markdown, Obsidian, Notion, flomo, Xinzhi, Anki, highlighted EPUB and reading-log CSV.
- Sync and snapshots: sync progress, notes and podcasts across devices via iCloud Drive or WebDAV with side-by-side conflict resolution (Google Drive coming soon), plus full-library snapshot import and export.
- In-app feedback: send bug reports or suggestions right from the shelf, Me or the menu bar; text and public screenshots post to GitHub Issues without collecting email, while private attachments are encrypted on device before sending.
