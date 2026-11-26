#if os(macOS)
import AppKit
import SwiftUI

// MARK: - Anchor & Reveal State

/// Every jumpable setting across macOS 设置. Each case owns its `SettingsPage`,
/// so a search entry can never point at a mismatched page.
enum SettingsAnchor: String, CaseIterable, Hashable, Identifiable, Sendable {
    // 通用
    case generalLanguage
    case generalReopenLast
    case generalLoginItem
    case generalKeepAwake
    case generalImport

    // 外观
    case appearanceMode
    case appearanceTint
    case appearanceIcon
    case appearanceCoverSize
    case appearanceShowProgress
    case appearanceShelfStyle
    case appearanceWoodLedge
    case appearanceLiquidGlass

    // 阅读
    case readingPaper
    case readingFont
    case readingFontSize
    case readingLineHeight
    case readingLayoutMode
    case readingPageTurnAnimation
    case readingPageTurnSound
    case readingTwoColumns
    case readingJustify
    case readingIndent
    case readingCompressPunctuation
    case readingImmersiveShortcut
    case readingImmersiveAlways
    case readingImmersiveLine
    case readingImmersiveMarks
    case readingImmersiveHints
    case readingImmersiveStill
    case readingFocusMode

    // 外文
    case foreignSystemDict
    case foreignAutoSpeak
    case foreignAccent
    case foreignMDict
    case foreignGloss
    case foreignLevel
    case foreignGlossVocabOnly
    case foreignMarkVocab
    case foreignAITranslate
    case foreignSystemTranslate

    // 朗读
    case listeningSources
    case listeningAddSource
    case listeningCloneVoice
    case listeningRate

    // 书伴
    case companionProviders
    case companionAddProvider
    case companionQuickPrompts
    case companionUsage
    case companionTierRouting
    case companionDailyLimit

    // 同步
    case syncStatus
    case syncICloud
    case syncWebDAV
    case syncGoogleDrive
    case syncContent

    // 笔记导出
    case notesObsidian
    case notesNotion
    case notesFlomo
    case notesXinzhi

    // 存储
    case storageOverview
    case storageDeleted
    case storageCache

    // 数据
    case dataSnapshots
    case dataReadingExport

    // 关于
    case aboutVersion
    case aboutChangelog
    case aboutAutoUpdate
    case aboutChannel
    case aboutFeedback
    case aboutAnalytics
    case aboutPrivacy
    case aboutLicences

    var id: String { rawValue }

    var page: SettingsPage {
        switch self {
        case .generalLanguage, .generalReopenLast, .generalLoginItem, .generalKeepAwake, .generalImport:
            .general
        case .appearanceMode, .appearanceTint, .appearanceIcon, .appearanceCoverSize, .appearanceShowProgress,
             .appearanceShelfStyle, .appearanceWoodLedge, .appearanceLiquidGlass:
            .appearance
        case .readingPaper, .readingFont, .readingFontSize, .readingLineHeight, .readingLayoutMode,
             .readingPageTurnAnimation, .readingPageTurnSound, .readingTwoColumns, .readingJustify,
             .readingIndent, .readingCompressPunctuation, .readingImmersiveShortcut, .readingImmersiveAlways,
             .readingImmersiveLine, .readingImmersiveMarks, .readingImmersiveHints, .readingImmersiveStill,
             .readingFocusMode:
            .reading
        case .foreignSystemDict, .foreignAutoSpeak, .foreignAccent, .foreignMDict, .foreignGloss,
             .foreignLevel, .foreignGlossVocabOnly, .foreignMarkVocab, .foreignAITranslate, .foreignSystemTranslate:
            .foreign
        case .listeningSources, .listeningAddSource, .listeningCloneVoice, .listeningRate:
            .listening
        case .companionProviders, .companionAddProvider, .companionQuickPrompts, .companionUsage,
             .companionTierRouting, .companionDailyLimit:
            .companion
        case .syncStatus, .syncICloud, .syncWebDAV, .syncGoogleDrive, .syncContent:
            .sync
        case .notesObsidian, .notesNotion, .notesFlomo, .notesXinzhi:
            .notes
        case .storageOverview, .storageDeleted, .storageCache:
            .storage
        case .dataSnapshots, .dataReadingExport:
            .data
        case .aboutVersion, .aboutChangelog, .aboutAutoUpdate, .aboutChannel, .aboutFeedback,
             .aboutAnalytics, .aboutPrivacy, .aboutLicences:
            .update
        }
    }
}

/// One-shot reveal request so repeated jumps to the same anchor still trigger scroll and pulse.
struct SettingsReveal: Equatable, Identifiable {
    let id = UUID()
    let anchor: SettingsAnchor
}

extension EnvironmentValues {
    /// The setting currently highlighted after jumping from Settings Search.
    @Entry var settingsHighlightedAnchor: SettingsAnchor? = nil
}

/// Marks a setting row or card as a scroll target and draws a soft warm-paper focus pulse when jumped to.
private struct SettingsAnchorModifier: ViewModifier {
    let anchor: SettingsAnchor
    let cornerRadius: CGFloat
    let insetHorizontal: CGFloat
    let insetVertical: CGFloat

    @Environment(\.settingsHighlightedAnchor) private var highlighted
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id

    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var isHighlighted: Bool { highlighted == anchor }

    func body(content: Content) -> some View {
        content
            .overlay {
                if isHighlighted {
                    RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                        .fill(tint.accent.opacity(scheme == .dark ? 0.14 : 0.08))
                        .overlay(
                            RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                                .strokeBorder(tint.accent.opacity(scheme == .dark ? 0.72 : 0.55), lineWidth: 1.5)
                        )
                        .padding(.horizontal, insetHorizontal)
                        .padding(.vertical, insetVertical)
                        .allowsHitTesting(false)
                        .transition(.opacity)
                }
            }
            .animation(YYMotion.standard, value: isHighlighted)
            .id(anchor)
    }
}

extension View {
    /// Registers this view as a searchable settings target.
    /// Set `card: true` on standalone cards (such as sync method cards) so the highlight hugs the card corners.
    func settingsAnchor(_ anchor: SettingsAnchor, card: Bool = false) -> some View {
        modifier(SettingsAnchorModifier(
            anchor: anchor,
            cornerRadius: card ? 12 : 9,
            insetHorizontal: card ? 0 : 4,
            insetVertical: card ? 0 : 2
        ))
    }
}

// MARK: - Search Index & Matching

struct SettingsSearchItem: Identifiable, Hashable {
    let anchor: SettingsAnchor
    let title: String
    let detail: String
    let section: String?
    let keywords: [String]
    let isPopular: Bool

    var id: SettingsAnchor { anchor }
    var page: SettingsPage { anchor.page }

    var breadcrumb: String {
        if let section, !section.isEmpty, section != page.title {
            return "\(page.title) › \(section)"
        }
        return page.title
    }
}

struct SettingsSearchMatch: Identifiable {
    let item: SettingsSearchItem
    let score: Int

    var id: SettingsAnchor { item.anchor }
}

enum SettingsSearchIndex {
    private static let recentKey = "settings.search.recent"
    private static let maxRecent = 4

    /// Builds the full index of macOS settings, excluding controls unavailable on the current system.
    static func allItems(
        liquidGlassSupported: Bool = LiquidGlass.supported
    ) -> [SettingsSearchItem] {
        var items: [SettingsSearchItem] = [
            // MARK: 通用
            SettingsSearchItem(
                anchor: .generalLanguage,
                title: String(localized: "语言"),
                detail: String(localized: "简体中文、English 或跟随系统"),
                section: nil,
                keywords: ["language", "locale", "chinese", "english", "zh", "en", "界面语言", "中文", "英文", "切换语言", "跟随系统"],
                isPopular: true
            ),
            SettingsSearchItem(
                anchor: .generalReopenLast,
                title: String(localized: "启动时继续上次阅读"),
                detail: String(localized: "关闭则进入书架"),
                section: nil,
                keywords: ["startup", "launch", "reopen", "resume", "restore", "session", "启动", "打开", "恢复", "上次", "书架"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .generalLoginItem,
                title: String(localized: "登录时后台启动"),
                detail: String(localized: "常驻菜单栏，不弹出窗口，按时生成播客"),
                section: nil,
                keywords: ["login item", "autostart", "startup", "menubar", "background", "podcast", "开机", "自启", "开机启动", "登录项", "菜单栏", "后台", "播客"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .generalKeepAwake,
                title: String(localized: "朗读时防止系统休眠"),
                detail: String(localized: "合上屏幕仍会暂停朗读"),
                section: nil,
                keywords: ["keep awake", "sleep", "screen", "power", "caffeinate", "休眠", "息屏", "常亮", "防止休眠", "听书"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .generalImport,
                title: String(localized: "拖入书架即可导入"),
                detail: String(localized: "支持 EPUB、PDF、MOBI、AZW3、TXT，文件会复制到书库"),
                section: String(localized: "导入"),
                keywords: ["import", "drag", "drop", "epub", "pdf", "mobi", "azw3", "txt", "导入", "拖拽", "格式", "电子书"],
                isPopular: false
            ),

            // MARK: 外观
            SettingsSearchItem(
                anchor: .appearanceMode,
                title: String(localized: "界面"),
                detail: String(localized: "浅色、深色或跟随系统外观"),
                section: nil,
                keywords: ["appearance", "theme", "light", "dark", "system", "night", "浅色", "深色", "暗黑", "暗色", "夜间模式", "跟随系统", "主题模式"],
                isPopular: true
            ),
            SettingsSearchItem(
                anchor: .appearanceTint,
                title: String(localized: "主题色"),
                detail: String(localized: "用于按钮、开关与进度标记"),
                section: nil,
                keywords: ["tint", "accent", "color", "camel", "sage", "mist", "blush", "配色", "强调色", "颜色", "暖纸", "草木", "烟雨", "海棠"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .appearanceIcon,
                title: String(localized: "应用图标"),
                detail: String(localized: "运行语阅时应用于程序坞；退出后恢复默认宣纸图标"),
                section: nil,
                keywords: ["app icon", "dock", "icon", "图标", "程序坞", "换图标", "宣纸"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .appearanceCoverSize,
                title: String(localized: "书架封面大小"),
                detail: String(localized: "每行本数随窗口宽度自动排布"),
                section: nil,
                keywords: ["shelf", "cover size", "grid", "scale", "封面", "大小", "尺寸", "缩放", "书架"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .appearanceShowProgress,
                title: String(localized: "封面下显示进度"),
                detail: String(localized: "关闭后仅显示书名"),
                section: nil,
                keywords: ["progress", "percentage", "shelf", "cover", "进度", "百分比", "书名", "书架"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .appearanceShelfStyle,
                title: String(localized: "书架样式"),
                detail: String(localized: "切换封面或书脊排列样式"),
                section: nil,
                keywords: ["shelf style", "grid", "spine", "bookshelf", "书架样式", "封面", "书脊"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .appearanceWoodLedge,
                title: String(localized: "浅木层板"),
                detail: String(localized: "为书架每排加上浅木层板与柔和投影"),
                section: nil,
                keywords: ["wood", "ledge", "shelf", "木质", "层板", "木板", "书架", "投影"],
                isPopular: false
            ),
        ]

        if liquidGlassSupported {
            items.append(SettingsSearchItem(
                anchor: .appearanceLiquidGlass,
                title: String(localized: "液态玻璃效果"),
                detail: String(localized: "浮动工具栏与提示条使用系统液态玻璃，关闭则为毛玻璃"),
                section: nil,
                keywords: ["liquid glass", "glass", "blur", "material", "translucent", "液态玻璃", "毛玻璃", "透明", "材质"],
                isPopular: false
            ))
        }

        items.append(contentsOf: [
            // MARK: 阅读
            SettingsSearchItem(
                anchor: .readingPaper,
                title: String(localized: "纸色"),
                detail: String(localized: "素白、暗黑、纸本、芦苇、铅纸、自定义纸与跟随外观"),
                section: nil,
                keywords: ["paper", "background", "plain", "dark", "kami", "sage", "eink", "custom paper", "follow", "纸色", "纸张", "背景色", "护眼", "素白", "纸本", "芦苇", "铅纸", "暗黑", "跟随外观", "自定义纸", "墨水屏"],
                isPopular: true
            ),
            SettingsSearchItem(
                anchor: .readingFont,
                title: String(localized: "字体"),
                detail: String(localized: "选择正文默认字体，或在字样册下载更多字体"),
                section: nil,
                keywords: ["font", "typeface", "serif", "sans", "kai", "wenkai", "songti", "fontbook", "字体", "宋体", "楷体", "霞鹜文楷", "字样册", "更多字体", "排版"],
                isPopular: true
            ),
            SettingsSearchItem(
                anchor: .readingFontSize,
                title: String(localized: "字号"),
                detail: String(localized: "调整新书默认正文字号大小"),
                section: nil,
                keywords: ["font size", "text size", "typography", "字号", "字体大小", "放大", "缩小"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingLineHeight,
                title: String(localized: "行距"),
                detail: String(localized: "紧、适中或松的行间距"),
                section: nil,
                keywords: ["line height", "line spacing", "leading", "行距", "行间距", "排版"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingLayoutMode,
                title: String(localized: "翻页方式"),
                detail: String(localized: "左右翻页或上下滚动"),
                section: nil,
                keywords: ["layout mode", "paginated", "scroll", "page turn", "翻页", "翻页方式", "左右翻页", "上下滚动", "分页", "滚动"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingPageTurnAnimation,
                title: String(localized: "翻页动画"),
                detail: String(localized: "滑动、覆盖或无动画"),
                section: nil,
                keywords: ["page turn", "animation", "slide", "cover", "翻页动画", "滑动", "覆盖", "无动画"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingPageTurnSound,
                title: String(localized: "翻页音效"),
                detail: String(localized: "6 种内置音效与自定义导入/实录，支持音量微调"),
                section: nil,
                keywords: ["sound", "audio", "sfx", "page turn sound", "custom", "volume", "翻页音效", "翻书声", "自定义音效", "纸声", "轻纸", "脆页", "宣纸", "精装", "轻叩", "滴墨", "音量", "音效", "声音"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingTwoColumns,
                title: String(localized: "宽窗口下双栏显示"),
                detail: String(localized: "左右并排显示两页"),
                section: nil,
                keywords: ["two columns", "dual page", "spread", "column", "双栏", "单栏", "并排", "两页", "分栏"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingJustify,
                title: String(localized: "两端对齐"),
                detail: String(localized: "正文段落左右对齐排版"),
                section: nil,
                keywords: ["justify", "alignment", "text align", "两端对齐", "对齐", "排版"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingIndent,
                title: String(localized: "首行缩进两字"),
                detail: String(localized: "中文段落首行空两格"),
                section: nil,
                keywords: ["indent", "first line", "paragraph", "首行缩进", "缩进", "空两格", "段落"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingCompressPunctuation,
                title: String(localized: "中文标点挤压"),
                detail: String(localized: "收紧连续标点的留白"),
                section: nil,
                keywords: ["punctuation", "compress", "cjk", "标点挤压", "标点", "挤压", "排版"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingImmersiveShortcut,
                title: String(localized: "沉浸阅读进入与退出"),
                detail: String(localized: "全屏隐藏界面，指针移至底部唤出控制栏（⌃⌘F 或 Esc）"),
                section: String(localized: "沉浸阅读"),
                keywords: ["immersive", "fullscreen", "shortcut", "zen", "沉浸阅读", "沉浸", "全屏", "快捷键"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingImmersiveAlways,
                title: String(localized: "打开书籍时自动进入"),
                detail: String(localized: "打开书籍时自动进入沉浸阅读"),
                section: String(localized: "沉浸阅读"),
                keywords: ["auto immersive", "fullscreen", "自动进入", "沉浸阅读", "沉浸"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingImmersiveLine,
                title: String(localized: "底部保留进度细线"),
                detail: String(localized: "沉浸阅读时在页面底部显示极细进度条"),
                section: String(localized: "沉浸阅读"),
                keywords: ["progress line", "immersive", "进度细线", "进度条", "沉浸"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingImmersiveMarks,
                title: String(localized: "隐藏划线与想法"),
                detail: String(localized: "退出沉浸后恢复显示"),
                section: String(localized: "沉浸阅读"),
                keywords: ["hide highlights", "marks", "notes", "隐藏划线", "划线", "想法", "标注", "沉浸"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingImmersiveHints,
                title: String(localized: "隐藏书伴提示与难词注释"),
                detail: String(localized: "沉浸阅读时隐藏书伴提示与行间释义"),
                section: String(localized: "沉浸阅读"),
                keywords: ["hide hints", "gloss", "companion", "隐藏提示", "难词注释", "书伴提示", "沉浸"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingImmersiveStill,
                title: String(localized: "关闭翻页动画"),
                detail: String(localized: "直接切换整页，无滑动过渡"),
                section: String(localized: "沉浸阅读"),
                keywords: ["disable animation", "still", "no animation", "关闭动画", "翻页动画", "无过渡", "沉浸"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .readingFocusMode,
                title: String(localized: "专注模式联动"),
                detail: String(localized: "在系统「阅读」专注模式中关联语阅"),
                section: String(localized: "沉浸阅读"),
                keywords: ["focus mode", "dnd", "system settings", "专注模式", "勿扰", "联动"],
                isPopular: false
            ),

            // MARK: 外文
            SettingsSearchItem(
                anchor: .foreignSystemDict,
                title: String(localized: "系统词典"),
                detail: String(localized: "导入词典未命中时使用；启用项与顺序可在系统「词典」中设置"),
                section: String(localized: "查词"),
                keywords: ["dictionary", "lookup", "apple dictionary", "词典", "系统词典", "查词", "查单词"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .foreignAutoSpeak,
                title: String(localized: "查词时自动发音"),
                detail: String(localized: "点词查询时自动朗读单词发音"),
                section: String(localized: "查词"),
                keywords: ["pronounce", "speak", "audio", "lookup", "自动发音", "发音", "读音", "查词"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .foreignAccent,
                title: String(localized: "英文发音"),
                detail: String(localized: "切换英音或美音"),
                section: String(localized: "查词"),
                keywords: ["accent", "british", "american", "uk", "us", "英文发音", "英音", "美音", "口音"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .foreignMDict,
                title: String(localized: "导入词典（.mdx）"),
                detail: String(localized: "按列表顺序优先查词，右键可调整先后或移除"),
                section: String(localized: "MDict 词典"),
                keywords: ["mdict", "mdx", "offline dictionary", "import dictionary", "词典", "离线词典", "导入词典", "自定义词典"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .foreignGloss,
                title: String(localized: "难词行间释义"),
                detail: String(localized: "阅读英文书时，在较难词汇上方标注简短中文词义"),
                section: String(localized: "难词注释"),
                keywords: ["gloss", "inline translation", "hard words", "难词", "行间释义", "注释", "词义", "英文阅读"],
                isPopular: true
            ),
            SettingsSearchItem(
                anchor: .foreignLevel,
                title: String(localized: "词汇难度"),
                detail: String(localized: "等级越高，仅标注更生僻的词"),
                section: String(localized: "难词注释"),
                keywords: ["vocab level", "cet4", "cet6", "ielts", "toefl", "gre", "词汇难度", "难度", "四级", "六级", "考研", "雅思", "托福"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .foreignGlossVocabOnly,
                title: String(localized: "仅标注生词本中的词"),
                detail: String(localized: "难词行间释义仅显示已加入生词本的词"),
                section: String(localized: "难词注释"),
                keywords: ["vocab only", "wordbook", "生词本", "仅标注"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .foreignMarkVocab,
                title: String(localized: "在书中标记生词"),
                detail: String(localized: "「在学」生词在所有书中以虚线标出，标为「已掌握」后不再标记"),
                section: String(localized: "生词"),
                keywords: ["mark vocab", "underline", "dashed", "vocabulary", "生词", "标记生词", "虚线", "生词本"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .foreignAITranslate,
                title: String(localized: "优先用书伴模型翻译"),
                detail: String(localized: "已配置书伴时，用模型生成逐段译文对照（含文言译白话）并按章缓存"),
                section: String(localized: "翻译"),
                keywords: ["ai translate", "llm translation", "classical chinese", "翻译", "书伴翻译", "大模型翻译", "译文对照", "文言文"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .foreignSystemTranslate,
                title: String(localized: "系统离线翻译"),
                detail: String(localized: "本地离线运行，原文不上传；供整页译文与双击整句翻译使用"),
                section: String(localized: "翻译"),
                keywords: ["offline translation", "apple translate", "language pack", "离线翻译", "系统翻译", "整页译文", "翻译包", "下载"],
                isPopular: false
            ),

            // MARK: 朗读
            SettingsSearchItem(
                anchor: .listeningSources,
                title: String(localized: "声音来源"),
                detail: String(localized: "离线 Kokoro、免 Key 在线语音、云端与系统音色"),
                section: nil,
                keywords: ["voice", "tts", "narrator", "kokoro", "edge", "system voice", "speech", "声音", "音色", "朗读", "听书", "离线语音", "系统语音", "播客音色"],
                isPopular: true
            ),
            SettingsSearchItem(
                anchor: .listeningAddSource,
                title: String(localized: "添加声音来源"),
                detail: String(localized: "火山豆包、Fish Audio、小米 MiMo、硅基流动、本机 VoiceStudio 等"),
                section: String(localized: "声音来源"),
                keywords: ["add voice", "cloud tts", "volcengine", "doubao", "fish audio", "xiaomi", "siliconflow", "dashscope", "minimax", "openai", "azure", "elevenlabs", "deepgram", "voicestudio", "voicebox", "mlx", "speaches", "添加声音", "语音服务", "豆包", "火山", "阿里云", "本地语音"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .listeningCloneVoice,
                title: String(localized: "克隆一个声音"),
                detail: String(localized: "念 8 秒录制专属音色，声音不出本机"),
                section: String(localized: "声音来源"),
                keywords: ["clone voice", "custom voice", "record", "克隆", "克隆声音", "音色克隆", "录制声音"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .listeningRate,
                title: String(localized: "语速"),
                detail: String(localized: "调整朗读与听书播放倍速（0.8× – 2.0×）"),
                section: nil,
                keywords: ["speed", "rate", "playback speed", "tempo", "语速", "倍速", "速度", "朗读速度"],
                isPopular: false
            ),

            // MARK: 书伴
            SettingsSearchItem(
                anchor: .companionProviders,
                title: String(localized: "模型服务"),
                detail: String(localized: "配置与切换 Claude、OpenAI、DeepSeek、Gemini、Kimi、通义、Ollama 等"),
                section: nil,
                keywords: ["ai", "llm", "companion", "model", "api key", "apple intelligence", "claude", "openai", "gpt", "deepseek", "gemini", "kimi", "moonshot", "qwen", "dashscope", "openrouter", "ollama", "zhipu", "glm", "doubao", "siliconflow", "minimax", "grok", "书伴", "大模型", "服务商", "密钥", "问答", "播客撰稿"],
                isPopular: true
            ),
            SettingsSearchItem(
                anchor: .companionAddProvider,
                title: String(localized: "添加服务商"),
                detail: String(localized: "接入新的大模型 API 或本机 Ollama 服务"),
                section: String(localized: "模型服务"),
                keywords: ["add provider", "api key", "custom endpoint", "添加服务商", "接入模型", "新建服务商"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .companionQuickPrompts,
                title: String(localized: "常用提问"),
                detail: String(localized: "自定义书伴 / 快捷指令与提问模板"),
                section: nil,
                keywords: ["prompt", "quick prompt", "slash command", "template", "常用提问", "提问", "快捷指令", "提示词", "斜杠命令"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .companionUsage,
                title: String(localized: "用量"),
                detail: String(localized: "查看近 30 天大模型 Token 消耗与云端朗读字数估算"),
                section: nil,
                keywords: ["usage", "token", "cost", "stats", "statistics", "用量", "统计", "账单", "消耗"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .companionTierRouting,
                title: String(localized: "批量任务自动用轻量模型"),
                detail: String(localized: "认人、错字整理、提要优先用同厂轻量模型并自动启用提示词缓存"),
                section: String(localized: "省费"),
                keywords: ["tier routing", "fast model", "cheap", "save cost", "cache", "省费", "轻量模型", "路由", "批量任务", "缓存"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .companionDailyLimit,
                title: String(localized: "每日用量上限"),
                detail: String(localized: "达到后自动暂停后台批量任务"),
                section: String(localized: "省费"),
                keywords: ["daily limit", "budget", "cap", "quota", "用量上限", "限额", "预算", "省费"],
                isPopular: false
            ),

            // MARK: 同步
            SettingsSearchItem(
                anchor: .syncStatus,
                title: String(localized: "同步状态与设备"),
                detail: String(localized: "查看多端同步状态、已连接设备或立即同步"),
                section: nil,
                keywords: ["sync status", "devices", "sync now", "同步状态", "立即同步", "设备", "多端同步"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .syncICloud,
                title: String(localized: "iCloud 同步"),
                detail: String(localized: "使用 iCloud 云盘中的文件夹自动同步"),
                section: String(localized: "同步方式"),
                keywords: ["icloud", "icloud drive", "apple sync", "cloud", "云盘", "同步方式", "同步"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .syncWebDAV,
                title: String(localized: "WebDAV 同步"),
                detail: String(localized: "坚果云、群晖 NAS、Nextcloud、Alist 等个人网盘"),
                section: String(localized: "同步方式"),
                keywords: ["webdav", "jianguoyun", "synology", "nas", "nextcloud", "alist", "坚果云", "群晖", "网盘", "云盘", "自建", "同步"],
                isPopular: true
            ),
            SettingsSearchItem(
                anchor: .syncGoogleDrive,
                title: String(localized: "Google Drive 同步"),
                detail: String(localized: "用你自己的 Google 账号跨平台同步"),
                section: String(localized: "同步方式"),
                keywords: ["google drive", "gdrive", "google", "谷歌网盘", "同步"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .syncContent,
                title: String(localized: "同步内容"),
                detail: String(localized: "进度和书签、笔记和生词、设置和替换规则、书文件、有声书录音"),
                section: nil,
                keywords: ["sync content", "bookmarks", "notes", "vocab", "replace rules", "book files", "recordings", "audiobook", "同步内容", "进度", "书签", "笔记", "生词", "替换规则", "书文件", "有声书录音"],
                isPopular: false
            ),

            // MARK: 笔记导出
            SettingsSearchItem(
                anchor: .notesObsidian,
                title: String(localized: "导出到 Obsidian"),
                detail: String(localized: "选择笔记库文件夹，每本书写入一篇 Markdown"),
                section: String(localized: "导出目标"),
                keywords: ["obsidian", "markdown", "md", "vault", "folder", "导出", "笔记导出", "知识库"],
                isPopular: true
            ),
            SettingsSearchItem(
                anchor: .notesNotion,
                title: String(localized: "导出到 Notion"),
                detail: String(localized: "配置 Notion 集成令牌与放书的页"),
                section: String(localized: "导出目标"),
                keywords: ["notion", "integration", "token", "page", "导出", "笔记导出"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .notesFlomo,
                title: String(localized: "导出到 flomo"),
                detail: String(localized: "粘贴 flomo API 链接，每条划线一条笔记"),
                section: String(localized: "导出目标"),
                keywords: ["flomo", "memo", "api", "webhook", "浮墨", "导出", "笔记导出"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .notesXinzhi,
                title: String(localized: "导出到新枝"),
                detail: String(localized: "粘贴新枝 CLI AccessToken 发送划线与想法"),
                section: String(localized: "导出目标"),
                keywords: ["xinzhi", "token", "新枝", "导出", "笔记导出"],
                isPopular: false
            ),

            // MARK: 存储
            SettingsSearchItem(
                anchor: .storageOverview,
                title: String(localized: "已用空间"),
                detail: String(localized: "查看书库文件夹位置以及书文件、有声书、模型与字体占用"),
                section: nil,
                keywords: ["storage", "disk", "space", "library folder", "finder", "存储", "空间", "磁盘", "占用", "书库位置", "目录", "访达"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .storageDeleted,
                title: String(localized: "最近删除"),
                detail: String(localized: "保留 30 天以便恢复，可立即清空释放空间"),
                section: nil,
                keywords: ["recently deleted", "trash", "purge", "recover", "restore", "最近删除", "回收站", "清空", "恢复书籍"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .storageCache,
                title: String(localized: "书伴问答缓存与离线模型"),
                detail: String(localized: "清理书伴问答缓存，或移除已下载的朗读与对齐模型"),
                section: nil,
                keywords: ["cache", "clear cache", "voice model", "aligner", "kokoro", "缓存", "清理缓存", "模型", "朗读模型", "对齐模型", "书伴缓存"],
                isPopular: false
            ),

            // MARK: 数据
            SettingsSearchItem(
                anchor: .dataSnapshots,
                title: String(localized: "生成快照与导入快照"),
                detail: String(localized: "打包备份或恢复进度、书签、笔记、生词、设置与书文件（.yuyue）"),
                section: String(localized: "生成快照"),
                keywords: ["snapshot", "backup", "restore", "migrate", "yuyue", "快照", "备份", "恢复", "迁移", "导入快照", "生成快照", "换电脑"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .dataReadingExport,
                title: String(localized: "导出阅读记录"),
                detail: String(localized: "导出想读、在读、读完状态与完成日期（CSV），可导入 Goodreads 或 StoryGraph"),
                section: nil,
                keywords: ["export csv", "reading log", "goodreads", "storygraph", "导出阅读记录", "阅读记录", "书单", "csv"],
                isPopular: false
            ),

            // MARK: 关于
            SettingsSearchItem(
                anchor: .aboutVersion,
                title: String(localized: "检查更新与版本"),
                detail: String(localized: "查看当前版本号并手动检查新版本"),
                section: nil,
                keywords: ["version", "update", "check for updates", "about", "sparkle", "版本", "更新", "检查更新", "关于"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .aboutChangelog,
                title: String(localized: "更新日志"),
                detail: String(localized: "查看每一版改了什么"),
                section: nil,
                keywords: ["changelog", "release notes", "history", "更新日志", "版本记录", "改动"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .aboutAutoUpdate,
                title: String(localized: "自动检查更新"),
                detail: String(localized: "每天启动时检查一次，仅读取版本号，不上传本地数据"),
                section: nil,
                keywords: ["auto update", "automatic check", "自动更新", "自动检查"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .aboutChannel,
                title: String(localized: "更新渠道"),
                detail: String(localized: "切换正式版或测试版更新渠道"),
                section: nil,
                keywords: ["channel", "beta", "stable", "release", "更新渠道", "测试版", "正式版"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .aboutFeedback,
                title: String(localized: "问题与建议"),
                detail: String(localized: "可附截图写反馈，无需注册账号，或前往 GitHub"),
                section: nil,
                keywords: ["feedback", "bug", "issue", "github", "support", "反馈", "问题", "建议", "报错"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .aboutAnalytics,
                title: String(localized: "匿名使用统计"),
                detail: String(localized: "仅记录功能使用形态，不关联身份，绝不收集书名、笔记或搜索词"),
                section: nil,
                keywords: ["analytics", "telemetry", "privacy", "opt out", "统计", "匿名统计", "数据收集"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .aboutPrivacy,
                title: String(localized: "隐私说明"),
                detail: String(localized: "书库仅存本机 · 可关匿名统计"),
                section: nil,
                keywords: ["privacy", "policy", "local", "隐私", "隐私说明", "隐私政策"],
                isPopular: false
            ),
            SettingsSearchItem(
                anchor: .aboutLicences,
                title: String(localized: "开源许可与致谢"),
                detail: String(localized: "GPL-3.0、组件清单与源代码仓库"),
                section: nil,
                keywords: ["license", "open source", "gpl", "credits", "github", "source code", "开源", "许可", "致谢", "源代码"],
                isPopular: false
            ),
        ])

        return items
    }

    static func popularItems(from items: [SettingsSearchItem]? = nil) -> [SettingsSearchItem] {
        (items ?? allItems()).filter(\.isPopular)
    }

    /// Searches settings by Chinese text, English keywords, or Pinyin / initials (`"zs"` → `纸色`).
    /// Multi-word queries use AND semantics across whitespace-separated terms, ranked by relevance.
    static func search(_ rawQuery: String, in items: [SettingsSearchItem]? = nil) -> [SettingsSearchMatch] {
        let trimmed = rawQuery.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return [] }
        let terms = trimmed
            .split(whereSeparator: \.isWhitespace)
            .map { String($0).lowercased() }
            .filter { !$0.isEmpty }
        guard !terms.isEmpty else { return [] }

        let fullLower = trimmed.lowercased()
        let matchers = terms.map { (term: $0, matcher: NameMatcher($0)) }
        let pool = items ?? allItems()

        var hits: [(index: Int, match: SettingsSearchMatch)] = []
        hits.reserveCapacity(16)

        for (index, item) in pool.enumerated() {
            var totalScore = 0
            var matchedAll = true
            let titleLower = item.title.lowercased()
            let detailLower = item.detail.lowercased()
            let pageLower = item.page.title.lowercased()
            let sectionLower = item.section?.lowercased()

            if titleLower == fullLower {
                totalScore += 140
            }

            for (term, matcher) in matchers {
                guard let score = termScore(
                    term: term,
                    matcher: matcher,
                    title: item.title,
                    titleLower: titleLower,
                    detail: item.detail,
                    detailLower: detailLower,
                    pageTitle: item.page.title,
                    pageLower: pageLower,
                    section: item.section,
                    sectionLower: sectionLower,
                    keywords: item.keywords
                ) else {
                    matchedAll = false
                    break
                }
                totalScore += score
            }

            if matchedAll {
                hits.append((index, SettingsSearchMatch(item: item, score: totalScore)))
            }
        }

        hits.sort { a, b in
            if a.match.score != b.match.score { return a.match.score > b.match.score }
            return a.index < b.index
        }
        return hits.map(\.match)
    }

    private static func termScore(
        term: String,
        matcher: NameMatcher,
        title: String,
        titleLower: String,
        detail: String,
        detailLower: String,
        pageTitle: String,
        pageLower: String,
        section: String?,
        sectionLower: String?,
        keywords: [String]
    ) -> Int? {
        var best: Int?

        func consider(_ value: Int) {
            if let existing = best {
                if value > existing { best = value }
            } else {
                best = value
            }
        }

        if titleLower == term {
            consider(110)
        } else if titleLower.hasPrefix(term) {
            consider(92)
        } else if titleLower.contains(term) {
            consider(78)
        } else if let range = matcher.match(title) {
            consider(range.lowerBound == 0 ? 70 : 60)
        }

        if let section, let sectionLower {
            if sectionLower == term {
                consider(56)
            } else if sectionLower.contains(term) {
                consider(46)
            } else if matcher.match(section) != nil {
                consider(40)
            }
        }

        if pageLower == term {
            consider(52)
        } else if pageLower.contains(term) {
            consider(42)
        } else if matcher.match(pageTitle) != nil {
            consider(36)
        }

        for kw in keywords {
            let kwLower = kw.lowercased()
            if kwLower == term {
                consider(54)
            } else if kwLower.hasPrefix(term) {
                consider(44)
            } else if kwLower.contains(term) {
                consider(34)
            } else if term.allSatisfy(\.isASCII), !kwLower.allSatisfy(\.isASCII), matcher.match(kw) != nil {
                consider(30)
            }
        }

        if detailLower.contains(term) {
            consider(26)
        } else if matcher.match(detail) != nil {
            consider(20)
        }

        return best
    }

    /// Highlights every matched term (direct substring or Pinyin span) in `accent`.
    static func highlighted(_ text: String, query: String, accent: Color) -> AttributedString {
        var out = AttributedString(text)
        let terms = query
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .split(whereSeparator: \.isWhitespace)
            .map(String.init)
            .filter { !$0.isEmpty }
        guard !terms.isEmpty else { return out }

        let chars = out.characters
        let count = chars.count
        for term in terms {
            let matcher = NameMatcher(term)
            if let range = matcher.match(text), range.lowerBound >= 0, range.upperBound <= count {
                let lower = chars.index(chars.startIndex, offsetBy: range.lowerBound)
                let upper = chars.index(chars.startIndex, offsetBy: range.upperBound)
                out[lower..<upper].foregroundColor = accent
            }
        }
        return out
    }

    // MARK: Recent Searches

    static func recentItems(
        from items: [SettingsSearchItem]? = nil,
        defaults: UserDefaults = .standard
    ) -> [SettingsSearchItem] {
        let raw = defaults.stringArray(forKey: recentKey) ?? []
        guard !raw.isEmpty else { return [] }
        let byAnchor = Dictionary(uniqueKeysWithValues: (items ?? allItems()).map { ($0.anchor, $0) })
        return raw.compactMap { SettingsAnchor(rawValue: $0).flatMap { byAnchor[$0] } }
    }

    static func recordRecent(_ anchor: SettingsAnchor, defaults: UserDefaults = .standard) {
        var ids = (defaults.stringArray(forKey: recentKey) ?? []).filter { $0 != anchor.rawValue }
        ids.insert(anchor.rawValue, at: 0)
        defaults.set(Array(ids.prefix(maxRecent)), forKey: recentKey)
    }

    static func clearRecent(defaults: UserDefaults = .standard) {
        defaults.removeObject(forKey: recentKey)
    }
}

// MARK: - Corner Search Trigger Button

/// Magnifying-glass button sitting beside `SettingsHistoryButton` in `SettingsCorner`.
struct SettingsSearchButton: View {
    @Binding var open: Bool
    @State private var hovering = false

    var body: some View {
        Button {
            withAnimation(open ? YYMotion.exit : YYMotion.standard) {
                open.toggle()
            }
        } label: {
            Image(systemName: "magnifyingglass")
                .font(.yy(size: 13.5, weight: .regular))
                .foregroundStyle(open || hovering ? YYColor.t1 : YYColor.t2)
                .frame(width: 28, height: 28)
                .background(YYColor.t1.opacity(open ? 0.07 : hovering ? 0.05 : 0), in: .rect(cornerRadius: 8))
                .contentShape(.rect)
        }
        .buttonStyle(HistoryPressStyle())
        .onHover { hovering = $0 }
        .help(Text("搜索设置（⌘F）"))
        .accessibilityLabel(Text("搜索设置"))
        .accessibilityIdentifier("settings.search.button")
    }
}

// MARK: - Search Overlay Palette

/// Floating warm-paper palette inside 设置 for finding and jumping straight to any setting.
struct SettingsSearchOverlay: View {
    let initialQuery: String
    let close: () -> Void
    let jump: (SettingsSearchItem) -> Void

    @State private var query: String
    @State private var selectedIndex = 0
    @State private var allItems: [SettingsSearchItem]
    @State private var recentItems: [SettingsSearchItem]
    @FocusState private var fieldFocused: Bool
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage(EInk.key) private var eink = false

    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    init(
        initialQuery: String = "",
        close: @escaping () -> Void,
        jump: @escaping (SettingsSearchItem) -> Void
    ) {
        self.initialQuery = initialQuery
        self.close = close
        self.jump = jump
        let items = SettingsSearchIndex.allItems()
        _query = State(initialValue: initialQuery)
        _allItems = State(initialValue: items)
        _recentItems = State(initialValue: SettingsSearchIndex.recentItems(from: items))
    }

    private var trimmedQuery: String {
        query.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var searchMatches: [SettingsSearchMatch] {
        SettingsSearchIndex.search(trimmedQuery, in: allItems)
    }

    private var popularItems: [SettingsSearchItem] {
        let recentIDs = Set(recentItems.map(\.anchor))
        return SettingsSearchIndex.popularItems(from: allItems).filter { !recentIDs.contains($0.anchor) }
    }

    /// Flat ordered list matching the rendered rows, so keyboard ↑ / ↓ / ↩ maps 1:1.
    private var visibleItems: [SettingsSearchItem] {
        if trimmedQuery.isEmpty {
            return recentItems + popularItems
        }
        return searchMatches.map(\.item)
    }

    var body: some View {
        ZStack(alignment: .top) {
            Color.black.opacity(scheme == .dark ? 0.42 : 0.14)
                .ignoresSafeArea()
                .onTapGesture(perform: close)

            panel
                .padding(.top, 52)
                .padding(.horizontal, 24)
                .transition(YYMotion.reduce ? .opacity : .opacity.combined(with: .scale(scale: 0.96, anchor: .top)))
        }
        .background {
            Button("") { close() }
                .buttonStyle(.plain)
                .frame(width: 0, height: 0)
                .opacity(0)
                .keyboardShortcut(.cancelAction)
        }
        .onChange(of: query) { _, _ in
            selectedIndex = 0
        }
        .task {
            fieldFocused = true
            try? await Task.sleep(for: .milliseconds(80))
            fieldFocused = true
        }
    }

    private var panel: some View {
        let items = visibleItems
        return VStack(spacing: 0) {
            searchField(items: items)
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            resultsList(items: items)
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            footer(matchCount: items.count)
        }
        .frame(width: 480)
        .background(YYColor.card)
        .clipShape(.rect(cornerRadius: 16, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .strokeBorder(
                    scheme == .dark ? Color.white.opacity(0.11) : Color(yy: YYRGBA(51, 46, 41, 0.13)),
                    lineWidth: 0.5
                )
        )
        .shadow(color: .black.opacity(eink ? 0 : (scheme == .dark ? 0.52 : 0.16)), radius: 28, y: 16)
        .shadow(color: .black.opacity(eink ? 0 : (scheme == .dark ? 0.25 : 0.06)), radius: 4, y: 2)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("settings.search.panel")
    }

    private func searchField(items: [SettingsSearchItem]) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass")
                .font(.yy(size: 14.5, weight: .medium))
                .foregroundStyle(YYColor.t2)
                .frame(width: 28)

            TextField("", text: $query, prompt: Text("搜索设置项、功能名或拼音…").foregroundStyle(YYColor.t3))
                .textFieldStyle(.plain)
                .font(.yy(size: 15))
                .foregroundStyle(YYColor.t1)
                .focused($fieldFocused)
                .onKeyPress(.upArrow) {
                    moveSelection(-1, count: items.count)
                    return .handled
                }
                .onKeyPress(.downArrow) {
                    moveSelection(1, count: items.count)
                    return .handled
                }
                .onKeyPress(.escape) {
                    close()
                    return .handled
                }
                .onKeyPress(.return, phases: .down) { _ in
                    pickSelected(from: items)
                    return .handled
                }
                .accessibilityIdentifier("settings.search.field")

            if !query.isEmpty {
                Button {
                    query = ""
                    fieldFocused = true
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.yy(size: 13))
                        .foregroundStyle(YYColor.t3)
                        .frame(width: 22, height: 22)
                        .contentShape(.circle)
                }
                .buttonStyle(.plain)
                .help(Text("清除搜索"))
                .accessibilityLabel(Text("清除搜索"))
            }

            Button(action: close) {
                SettingsKeyCap("esc")
            }
            .buttonStyle(.plain)
            .help(Text("关闭搜索"))
        }
        .padding(.horizontal, 18)
        .frame(height: 48)
        .background(YYColor.bg.opacity(scheme == .dark ? 0.55 : 0.55))
    }

    private func resultsList(items: [SettingsSearchItem]) -> some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    if trimmedQuery.isEmpty {
                        if !recentItems.isEmpty {
                            sectionHeader(String(localized: "最近查找")) {
                                Button {
                                    withAnimation(YYMotion.quick) {
                                        SettingsSearchIndex.clearRecent()
                                        recentItems = []
                                        selectedIndex = 0
                                    }
                                } label: {
                                    Text("清空")
                                        .font(.yy(size: 10.5))
                                        .foregroundStyle(YYColor.t3)
                                }
                                .buttonStyle(.plain)
                            }
                            ForEach(Array(recentItems.enumerated()), id: \.element.id) { idx, item in
                                resultRow(item, index: idx)
                            }
                        }
                        if !popularItems.isEmpty {
                            sectionHeader(String(localized: "常用设置"))
                            ForEach(Array(popularItems.enumerated()), id: \.element.id) { offset, item in
                                resultRow(item, index: recentItems.count + offset)
                            }
                        }
                    } else if items.isEmpty {
                        emptyState
                    } else {
                        ForEach(Array(items.enumerated()), id: \.element.id) { idx, item in
                            resultRow(item, index: idx)
                        }
                    }
                }
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
            }
            .frame(maxHeight: 372)
            .fixedSize(horizontal: false, vertical: true)
            .scrollIndicators(.never)
            .onChange(of: selectedIndex) { _, next in
                guard items.indices.contains(next) else { return }
                withAnimation(YYMotion.quick) {
                    proxy.scrollTo(items[next].id)
                }
            }
        }
    }

    private func sectionHeader<Trailing: View>(
        _ title: String,
        @ViewBuilder trailing: () -> Trailing = { EmptyView() }
    ) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title)
                .font(.yy(size: 10.5, weight: .semibold))
                .foregroundStyle(YYColor.t3)
            Spacer(minLength: 8)
            trailing()
        }
        .padding(.horizontal, 10)
        .padding(.top, 6)
        .padding(.bottom, 4)
    }

    private func resultRow(_ item: SettingsSearchItem, index: Int) -> some View {
        let active = selectedIndex == index
        return HStack(spacing: 10) {
            Image(systemName: item.page.symbol)
                .font(.yy(size: 12.5, weight: .regular))
                .foregroundStyle(active ? tint.accent : YYColor.t2)
                .frame(width: 28, height: 28)
                .background(active ? tint.accentSoft : YYColor.inset, in: .rect(cornerRadius: 8))

            VStack(alignment: .leading, spacing: 2) {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(SettingsSearchIndex.highlighted(item.title, query: trimmedQuery, accent: tint.accent))
                        .font(.yy(size: 13, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                        .layoutPriority(1)
                    Text(SettingsSearchIndex.highlighted(item.breadcrumb, query: trimmedQuery, accent: tint.accent))
                        .font(.yy(size: 11))
                        .foregroundStyle(active ? tint.accent.opacity(0.85) : YYColor.t3)
                        .lineLimit(1)
                }
                Text(SettingsSearchIndex.highlighted(item.detail, query: trimmedQuery, accent: tint.accent))
                    .font(.yy(size: 11))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            if active {
                Text("↩ 前往")
                    .font(.yy(size: 10.5, weight: .semibold))
                    .foregroundStyle(tint.accent)
                    .padding(.horizontal, 7.5)
                    .padding(.vertical, 4)
                    .background(YYColor.card, in: .rect(cornerRadius: 5.5))
                    .overlay(
                        RoundedRectangle(cornerRadius: 5.5)
                            .strokeBorder(tint.accent.opacity(0.3), lineWidth: 0.5)
                    )
                    .fixedSize()
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .frame(minHeight: 46)
        .background(active ? tint.soft.opacity(scheme == .dark ? 0.9 : 0.85) : .clear, in: .rect(cornerRadius: 10, style: .continuous))
        .contentShape(.rect(cornerRadius: 10, style: .continuous))
        .onTapGesture {
            choose(item)
        }
        .onHover { over in
            if over { selectedIndex = index }
        }
        .id(item.id)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(active ? [.isButton, .isSelected] : .isButton)
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 15, weight: .regular))
                .foregroundStyle(YYColor.t3)
                .frame(width: 36, height: 36)
                .background(YYColor.bg, in: .rect(cornerRadius: 10))
                .overlay(
                    RoundedRectangle(cornerRadius: 10)
                        .strokeBorder(scheme == .dark ? Color.white.opacity(0.07) : YYColor.line, lineWidth: 0.5)
                )
            Text(String(localized: "没有找到与“\(trimmedQuery)”相关的设置"))
                .font(.yy(size: 13, weight: .semibold))
                .foregroundStyle(YYColor.t1)
            Text("试试换个关键词，例如：纸色、字体、音色、书伴、同步、快照")
                .font(.yy(size: 11.5))
                .foregroundStyle(YYColor.t3)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 34)
        .padding(.horizontal, 20)
    }

    private func footer(matchCount: Int) -> some View {
        HStack(spacing: 12) {
            HStack(spacing: 4) {
                SettingsKeyCap("↑")
                SettingsKeyCap("↓")
                Text("选择")
            }
            HStack(spacing: 4) {
                SettingsKeyCap("↩")
                Text("前往")
            }
            Spacer(minLength: 8)
            if trimmedQuery.isEmpty {
                Text(String(localized: "共 \(allItems.count) 项设置"))
                    .monospacedDigit()
            } else if matchCount > 0 {
                Text(String(localized: "找到 \(matchCount) 项设置"))
                    .monospacedDigit()
            }
        }
        .font(.yy(size: 10.5))
        .foregroundStyle(YYColor.t3)
        .padding(.horizontal, 18)
        .frame(height: 35)
        .background(YYColor.bg.opacity(scheme == .dark ? 0.45 : 0.45))
    }

    private func moveSelection(_ delta: Int, count: Int) {
        guard count > 0 else { return }
        selectedIndex = min(max(0, selectedIndex + delta), count - 1)
    }

    private func pickSelected(from items: [SettingsSearchItem]) {
        guard items.indices.contains(selectedIndex) else { return }
        choose(items[selectedIndex])
    }

    private func choose(_ item: SettingsSearchItem) {
        SettingsSearchIndex.recordRecent(item.anchor)
        jump(item)
    }
}

private struct SettingsKeyCap: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.yy(size: 10, weight: .medium))
            .monospacedDigit()
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, 5.5)
            .padding(.vertical, 2)
            .background(YYColor.card, in: .rect(cornerRadius: 4.5))
            .overlay(RoundedRectangle(cornerRadius: 4.5).strokeBorder(YYColor.line, lineWidth: 0.5))
    }
}
#endif
