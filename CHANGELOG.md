# 更新日志 · Changelog

每一版一节，最新的在最上面。App 里的“更新日志”和更新提示都从这里来。

Each version has a section, newest first. The app's changelog page and update notices come from here.

## 0.1.8 · 2026-11-27

### zh
- 翻页稳定性与启动续读：修复 Android 分页模式跨章后点击翻页卡死与连击误丢，加固 iOS 跨章预加载与点击排队；全端（iOS / Android / macOS / Windows）默认开启「启动时继续上次阅读」，退出时停留在阅读页则重启直接回到上次书籍。
- Android 离线语音与系统 TTS 优化：在线声音断网出错时改为友好提示并优先引导一键「换成离线声音」，修复点击下载系统语音无响应的问题；Kokoro 离线语音详情页补齐当前朗读来源状态与一键切换入口，并修复分角色朗读出错时的音色归因与精准恢复。
- Windows 交互修复：修复关于页签名点击绘制与弹窗按 Esc 关闭等细节问题。

### en
- Page-turn reliability & reopen-last-book: fixed Android paginated tap page turns getting stuck after chapter crossings and dropping rapid taps, hardened iOS chapter-edge preloading and tap queuing, and enabled "Continue last book on launch" by default across iOS, Android, macOS, and Windows so relaunching from a book returns directly to it.
- Android offline TTS & system voice polish: replaced raw offline network errors with friendly guidance that offers one-tap switching to a downloaded offline voice, fixed broken system TTS download navigation, added active-voice status and a one-tap switch banner on the Kokoro offline voice page, and fixed per-segment failure attribution and recovery in manual character-voice mode.
- Windows interaction fixes: fixed click-to-draw signature behavior on the About page and Esc key dismissal for modals.

## 0.1.7 · 2026-11-27

### zh
- Windows 正式版同步发布：同步至 `0.1.7` 并补齐自定义翻页音效（6 种拟真声学预设、音量调节、麦克风录音与音频导入裁剪）、阅读页与设置页 Fluent 2 界面打磨、品牌图标统一及 150+ 项英文界面补全；修复书架双击打开竞态与批量操作栏弹出导致的布局跳动。
- 书架标签记忆：全端（iOS / Android / macOS / Windows）记住上次停留的书架分类标签（书、播客、音频、收藏集等），重启后自动恢复；修复 Android 从笔记、发现或个人页切回首页时自动重置回「书」的问题，并在对应分类隐藏或删除时自动回退。
- 播客往期拉取与排序：移除 RSS 仅保留近 14 天节目的硬限制，订阅与刷新改为拉取最近 20 期并按发布时间正确倒序展示；进入单个节目视图可查看全部已拉取单集，并新增按需「加载更多往期」分批拉取更早的历史节目。
- Android 稳定性与系统语音修复：拆分自定义翻页音效弹窗组件并新增 Release 包 DEX 寄存器上限校验，避免 R8 压缩触发启动 `VerifyError`；优化 Android 切换系统默认 TTS 引擎后的热检测，并在返回页面时自动刷新音色列表。

### en
- Windows 0.1.7 release & parity: shipped Windows alongside Apple and Android with custom page-turn sounds (6 acoustic presets, volume slider, mic recording & audio import with auto-trim/normalization), Fluent 2 polish across Settings, Reader, Library, Audiobook, and Podcast views, 150+ completed English UI translations, and fixes for shelf double-click races and batch-bar layout shifts.
- Shelf tab persistence: remembered the last active Shelf sub-tab (Books, Podcasts, Audio, Collections, etc.) across app restarts on iOS, Android, macOS, and Windows, fixed Android resetting back to Books when returning from other bottom tabs, and automatically fall back when a sub-tab is promoted, hidden, or deleted.
- Podcast archive fetching & sorting: removed the 14-day hard cutoff on RSS ingestion so subscribing and refreshing keep up to 20 recent episodes sorted newest-first, showed all fetched episodes when filtering by a single show across all platforms, and added on-demand "Load older episodes" to fetch earlier back-catalog batches.
- Android release stability & system TTS refresh: split the custom page-turn sound sheet and added a release DEX register check (`< 256` registers) to guard against R8 `VerifyError` crashes, and refreshed Android system TTS voices on resume when switching OEM default TTS engines.

## 0.1.6 · 2026-11-27

### zh
- 广播剧旁白与语音修复：修复开启广播剧模式后旁白被切回系统自带声音、设置页与阅读页偏好键（`zh`/`zh-Hans`）不同步以及旧预合成片段未随旁白刷新的全端问题；扩展 Kokoro 离线音色库并彻底清除 INT8 底噪与高频蜂鸣，优化 Android 系统 TTS（三星、一加及第三方引擎）默认直连与语言匹配。
- 翻页音效与阅读细节：新增自定义翻页音效、音量调节与触觉反馈；改进广播剧对话气泡拆分与展示；统一 macOS 阅读器翻译浮窗样式，并把全端「播客」图标统一为收音机图标。
- 自定义底部导航与设置搜索：iPhone / Android 新增可视化拖拽定制底部导航（支持排序、替换、折叠进书架与隐藏）及 Android 液态玻璃底栏；macOS 设置页新增全局即时搜索（支持中文、拼音首字母与英文关键词）。

### en
- Audio drama narrator & voice fixes: fixed turning on Audio Drama mode unexpectedly switching the narrator back to a system voice, unified canonical voice preference keys across Settings and Reader, and refreshed Live casting plans before reading stale cached clips across Apple, Android, and Windows; expanded curated Kokoro offline voices to 32 while eliminating INT8 DC/carrier buzz, and improved Android OEM system TTS detection and locale matching.
- Custom page-turn sounds & reader polish: added custom page-turn audio, volume control, and haptics; refined dialogue speech bubbles and spans; aligned the macOS reader translation popover with Windows; and unified the Podcast icon across iOS, Android, and Windows to match macOS's radio icon.
- Customizable bottom navigation & macOS Settings search: added direct-manipulation bottom tab customization on iOS and Android (reorder, replace, fold into Shelf, hide) plus Android Liquid Glass tab bar support, and added instant global search (Chinese, pinyin initials, and English keywords) to macOS Settings.

## 0.1.5 · 2026-11-26

### zh
- 本机声音克隆修复：修复 Android 端在切换默认离线旁白后缺失声音克隆模型（ZipVoice）下载入口、导致录完音仍报「合成失败」的问题；在克隆、详情、选声、朗读面板与离线语音设置中全面补齐自动准备与手动下载进度条，Apple 端同步补齐引导。
- 绝不擅自换声：全端移除广播剧隐藏替换云端旁白的逻辑，取消设备发烫时自动切回系统机械音（改为放慢后台预合成并轻提示）；广播剧、角色分声、跨章语言不兼容或云端克隆异常时均停下明确告知，由用户决定重试或更换声音；修复 Apple 端跨章语言标签与后台同步导致的静默切声。
- TXT 识别与翻页体验：改进网文 TXT 行内书名/作者解析并过滤下载站广告，增强卷标、紧凑标题与纯数字章节目录识别；修复 Android 分页阅读点击翻页与跨章预加载衔接稳定性。
- Apple 书伴优化：在不支持 Apple 智能的设备上自动隐藏该项并优先选用已配置的第三方模型或展示引导配置卡片。

### en
- Local voice cloning fix: restored the missing on-device voice cloning model (ZipVoice) download triggers and inline progress bars across Android and Apple cloning, detail, voice picker, listen, and settings screens, replacing misleading synthesis failure errors with clear download status.
- Never switch voices without consent: removed hidden cloud-narrator substitution in drama mode across all platforms, stopped thermal throttling from auto-switching to system voices (throttling background pre-rendering with a gentle notice instead), and ensured drama, character, saved-voice, and ElevenLabs errors pause with clear choices rather than silently falling back; fixed Apple canonical language cache keys and mid-playback cache clears.
- TXT parsing & tap page turns: improved inline title/author parsing while filtering promo lines, broadened TXT chapter heading detection, and fixed Android paginated WebView tap page turns and cross-section settling.
- Apple Companion polish: hid unsupported Apple Intelligence options on older devices and added a clear setup card when no third-party model provider is configured.

## 0.1.4 · 2026-11-26

### zh
- 阅读快捷控制与护眼柔光：重构 Mac、iPhone / iPad 与 Android 的阅读页「更多」菜单，新增独立亮度调节与跟随系统开关、保留各自纸色偏好的「日间 / 夜间」一键切换，以及可叠加于任意纸色之上的独立「护眼柔光」暖色滤蓝光模式。
- 三态译文与 AI 书伴升级：译文支持「原文 / 双语对照 / 仅译文」三态即时切换（macOS 顶栏 `译` 升级为带图标三态菜单，并精简 `Aa` 与顶栏按钮）；AI 书伴新增快捷指令、多选保存与段落翻译。
- 翻页动效、书架样式与外观增强：全端新增「滑动 / 覆盖阴影 / 无动画」三档翻页动效及可选轻翻页音效；手机端书架新增「书脊排列」与「浅木层板」样式；Android 端补齐液态玻璃质感开关；Apple 端新增 6 款液态玻璃应用图标及年度阅读热力图。
- 同步与稳定性修复：新增 WebDAV 同步检查视图，优化离线语音就绪提示球与选词浮卡定位，并修复多项跨端朗读、字体、反馈与多语言问题。

### en
- Redesigned reader More controls & Eye-Care Warmth: rebuilt the reader `···` menu across Mac, iPhone / iPad, and Android with an independent brightness bar + Follow System switch, a Day/Night polarity toggle that preserves your `Aa` paper choices, and an independent warm blue-light Eye-Care filter.
- Three-mode translation & Book Companion upgrades: added Original / Bilingual / Translation-Only instant switching (with an icon-equipped toolbar menu on macOS and streamlined `Aa` / toolbar controls), plus Book Companion slash commands, multi-select save, and AI paragraph translation.
- Page turns, shelf styles & visual polish: added Slide / Cover (with edge shadow) / None page turns and optional turn sound; added Spine view and Light Wood Ledge shelf styles on mobile; brought Liquid Glass controls to Android; and added six Liquid Glass app icons and an annual reading heatmap on Apple.
- Sync & stability fixes: added a WebDAV sync inspector, refined the offline-voice ready orb and floating selection cards, and resolved cross-platform voice, font, feedback, and localization issues.

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
