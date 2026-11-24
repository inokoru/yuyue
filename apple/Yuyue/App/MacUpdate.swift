#if os(macOS)
import SwiftUI
import YuyueCore

/// The shelf dialog (检查更新…) and the 设置 page. 下载并安装 hands over to Sparkle, whose own
/// window shows the download and swaps the app.
struct MacUpdateDialog: View {
    private var center: UpdateCenter { UpdateCenter.shared }

    var body: some View {
        ZStack {
            Color(yy: YYRGBA(42, 38, 34, 0.28))
                .ignoresSafeArea()
                .onTapGesture { center.dialog = .none }
            dialog
                .frame(width: center.dialog == .current || center.dialog == .offline ? 340 : 460)
                .padding(22)
                .background(YYColor.bg, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Color.black.opacity(0.12), lineWidth: 0.5))
                .shadow(color: .black.opacity(0.28), radius: 32, y: 16)
                .yyPopOnAppear(from: .center)
                .padding(22)
        }
        // checking → a release or 已是最新; the card resizes to it rather than jumping.
        .animation(YYMotion.layout, value: center.dialog)
        .animation(YYMotion.standard, value: center.phase)
    }

    @ViewBuilder private var dialog: some View {
        switch center.dialog {
        case .current: current
        case .offline: offline
        case .flow: center.phase == .checking ? AnyView(checking) : AnyView(release)
        case .none: EmptyView()
        }
    }

    private var release: some View {
        VStack(alignment: .leading, spacing: 0) {
            head(kicker: String(localized: "新版本"), version: center.offer?.display ?? "")
            if let offer = center.offer {
                Text(meta(offer)).font(YYUI.font(12.5)).foregroundStyle(YYColor.t3).monospacedDigit().padding(.top, 6)
                ScrollView { UpdateNotesCard(offer: offer) }
                    .frame(maxHeight: 360)
                    .padding(.top, 14)
            }
            UpdateButton(title: String(localized: "下载并安装"), kind: .main, wide: true) { center.install() }
                .padding(.top, 14)
            HStack { Spacer(); UpdateButton(title: String(localized: "稍后提醒")) { center.remindLater() }; UpdateButton(title: String(localized: "跳过此版本")) { center.skip() }; Spacer() }
                .padding(.top, 8)
            Text("下好后核对签名，换掉语阅，再打开。正在听的会停一下，进度还在。")
                .font(YYUI.font(11.5)).foregroundStyle(YYColor.t3).padding(.top, 10)
        }
    }

    private var checking: some View {
        VStack(spacing: 12) {
            YuyueMark(px: 44)
            Text("正在检查更新…").font(YYUI.font(13)).foregroundStyle(YYColor.t2)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 12)
    }

    private var current: some View {
        VStack(spacing: 0) {
            YuyueMark(px: 44).padding(.bottom, 12)
            Text("已是最新版本").font(YYUI.serif(YYUI.s(22), .semibold))
            Text("正在用 \(center.currentLabel)。\n\(center.lastSuccess.map { String(localized: "\(UpdateWhen.phrase($0)) 检查过。") } ?? "")")
                .font(YYUI.font(12.5)).foregroundStyle(YYColor.t2).multilineTextAlignment(.center).padding(.top, 8)
            UpdateButton(title: String(localized: "好"), kind: .main) { center.dialog = .none }
                .padding(.top, 16)
        }
        .frame(maxWidth: .infinity)
    }

    private var offline: some View {
        VStack(spacing: 0) {
            YuyueMark(px: 44).padding(.bottom, 12)
            Text("无法检查更新").font(YYUI.serif(YYUI.s(22), .semibold))
            Text("请检查网络连接后重试。")
                .font(YYUI.font(12.5)).foregroundStyle(YYColor.t2).multilineTextAlignment(.center).padding(.top, 8)
            UpdateButton(title: String(localized: "重试"), kind: .main) { center.checkFromMenu() }
                .padding(.top, 16)
        }
        .frame(maxWidth: .infinity)
    }

    private func head(kicker: String, version: String) -> some View {
        HStack(alignment: .center, spacing: 12) {
            YuyueMark(px: 44)
            VStack(alignment: .leading, spacing: 0) {
                Text(kicker).font(YYUI.font(12, .semibold)).tracking(1.4)
                    .foregroundStyle(YYTint.standard.accent)
                Text(version).font(YYUI.serif(YYUI.s(version.count > 8 ? 28 : 32), .semibold)).monospacedDigit()
            }
            Spacer(minLength: 0)
        }
    }

    private func meta(_ offer: UpdateOffer) -> String {
        let day = UpdateRules.monthDay(offer.publishedAt).map { " · \($0)" } ?? ""
        return String(localized: "从 \(UpdateCenter.current)\(day)")
    }
}

extension View {
    /// 检查更新… over the shelf, and the day's check when it opens.
    func updateDialog() -> some View {
        overlay {
            if UpdateCenter.shared.dialog != .none { MacUpdateDialog().transition(.opacity) }
        }
        .animation(YYMotion.standard, value: UpdateCenter.shared.dialog != .none)
        .task { UpdateCenter.shared.boot() }
    }
}

/// 设置 › 关于: a two-page bookplate layout (书扉 + 牌记). The left folio carries the 阅 seal,
/// Songti title, three-line creed, and graphite signature; the right folio holds version & updates,
/// changelog, feedback, privacy, and open-source credits without stretching across 750pt.
struct MacUpdateSettings: View {
    fileprivate enum SheetKind: String, Identifiable {
        case changelog, feedback, privacy, licences
        var id: String { rawValue }
    }

    var windowHeight: CGFloat = 640

    private var center: UpdateCenter { UpdateCenter.shared }
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    @State private var activeSheet: SheetKind?
    @State private var hoverAuthor = false
    @State private var hoverSig = false
    @State private var hoverSkip = false
    @State private var rightHeight: CGFloat = 0
    /// A click on the signature or the line under it writes the signature out.
    @State private var signRun = 0
    @State private var releases: [Release] = Self.loadBundledReleases()

    private static let author = URL(string: "https://github.com/nowtin")!
    private static let source = URL(string: "https://github.com/inokoru/yuyue")!
    private static let issues = URL(string: "https://github.com/inokoru/yuyue/issues")!
    private static let releasesURL = URL(string: "https://github.com/inokoru/yuyue/releases")!
    private static let noticeURL = URL(string: "https://github.com/inokoru/yuyue/blob/main/NOTICE")!
    private static let hair = Color.yyDynamic(light: YYRGBA(223, 219, 211), dark: YYRGBA(53, 50, 46))

    private var buildNumber: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "1"
    }

    var body: some View {
        let minTrackHeight = max(400, windowHeight - 110)
        let trackHeight = max(minTrackHeight, rightHeight)
        HStack(alignment: .top, spacing: 38) {
            leftFolio
                .frame(width: 214, height: trackHeight, alignment: .topLeading)
            rightFolio
                .frame(width: 468, alignment: .topLeading)
                .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { rightHeight = $0 }
        }
        .padding(.leading, 18)
        .padding(.top, 28)
        .padding(.bottom, 8)
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .onAppear { center.boot() }
        .sheet(item: $activeSheet) { kind in
            sheetView(for: kind)
        }
    }

    // MARK: Left Folio (书扉)

    private var leftFolio: some View {
        let isEn = AppLanguage.running == .en
        return VStack(alignment: .leading, spacing: 0) {
            YuyueMark(px: 54 / YYUI.k)
            if isEn {
                Text("语阅")
                    .font(.system(size: 28, weight: .semibold, design: .serif))
                    .tracking(0.6)
                    .foregroundStyle(YYColor.t1)
                    .padding(.top, 18)
                Text(verbatim: "语阅 · for macOS")
                    .font(.system(size: 13, design: .serif).italic())
                    .tracking(0.26)
                    .foregroundStyle(YYColor.t3)
                    .padding(.top, 5)
            } else {
                Text("语阅")
                    .font(YYUI.serif(28, .semibold))
                    .tracking(3.92)
                    .foregroundStyle(YYColor.t1)
                    .padding(.top, 16)
                Text(verbatim: "Yuyue for macOS")
                    .font(.system(size: 13, design: .serif).italic())
                    .tracking(0.26)
                    .foregroundStyle(YYColor.t3)
                    .padding(.top, 5)
            }
            Rectangle()
                .fill(Self.hair)
                .frame(width: 24, height: 1.5)
                .padding(.top, 18)
                .padding(.bottom, 16)
            Text("读着听，听着读，进度接得上。\n没有服务器，不收集数据，\n书与笔记都留在你手里。")
                .font(isEn ? .system(size: 13, design: .serif) : YYUI.serif(13.5))
                .lineSpacing(isEn ? 5.5 : 7.5)
                .foregroundStyle(YYColor.t2)
                .fixedSize(horizontal: false, vertical: true)

            Spacer(minLength: 20)

            PencilSignature(size: 58, run: signRun)
                .opacity(hoverSig ? 1 : 0.86)
                .onHover { hoverSig = $0 }
                .animation(YYMotion.quick, value: hoverSig)
            HStack(spacing: 4) {
                Text(verbatim: "by")
                    .foregroundStyle(YYColor.t3)
                Button {
                    NSWorkspace.shared.open(Self.author)
                } label: {
                    Text(verbatim: "nowtin")
                        .font(.yy(size: 11, weight: .medium))
                        .underline(hoverAuthor, color: YYColor.t3)
                        .foregroundStyle(YYColor.t1)
                }
                .buttonStyle(.plain)
                .onHover { hoverAuthor = $0 }
                Text(verbatim: "· GPL-3.0")
                    .foregroundStyle(YYColor.t3)
            }
            .font(.yy(size: 11))
            .tracking(0.22)
            .padding(.top, 4)
            .contentShape(.rect)
            .onTapGesture { signRun += 1 }
        }
        .padding(.top, 6)
        .frame(maxHeight: .infinity, alignment: .topLeading)
    }

    // MARK: Right Folio (牌记与操作)

    private var rightFolio: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let offer = center.offer, center.phase == .release {
                offerCard(offer)
            } else if center.phase == .skipped {
                AboutCard {
                    AboutRow(String(localized: "已跳过 \(center.offer?.display ?? "")"),
                             detail: String(localized: "有新版本时仍会提醒")) {
                        Button("查看此版本") { center.lookAnyway() }
                    }
                }
            } else if center.phase == .later {
                AboutCard {
                    AboutRow(String(localized: "\(center.offer?.display ?? "") · 稍后提醒"),
                             detail: String(localized: "\(center.remindUntil.map(UpdateWhen.monthDay) ?? "") 之前不显示红点")) {
                        Button("现在查看") { center.lookAnyway() }
                    }
                }
            }

            AboutCard {
                versionHeaderRow
                AboutNavRow(title: String(localized: "更新日志"), value: changelogSummary) {
                    activeSheet = .changelog
                }
                AboutRow(String(localized: "自动检查更新"),
                         detail: String(localized: "每天启动时检查一次，仅读取版本号，不上传本地数据")) {
                    SettingsSwitch(isOn: Binding(get: { center.autoOn }, set: { center.autoOn = $0 }))
                }
                AboutRow(String(localized: "更新渠道"),
                         detail: String(localized: "测试版先拿到新功能；换回正式版后随下个正式版恢复")) {
                    Menu {
                        ForEach(UpdateChannel.allCases) { c in
                            Button { center.channel = c } label: {
                                if c == center.channel { Label(c.title, systemImage: "checkmark") } else { Text(c.title) }
                            }
                        }
                    } label: {
                        HStack(spacing: 4) {
                            Text(center.channel.title).font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                            Image(systemName: "chevron.up.chevron.down").font(.yy(size: 8.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                        }
                    }
                    .menuStyle(.button)
                    .buttonStyle(.plain)
                    .menuIndicator(.hidden)
                    .fixedSize()
                }
                .help(UpdateChannel.detail)
            }

            AboutCard {
                AboutRow(String(localized: "问题与建议"),
                         detail: String(localized: "可附截图或文件，无需注册账号")) {
                    HStack(spacing: 10) {
                        AboutLinkButton(title: "GitHub", prominent: false) {
                            NSWorkspace.shared.open(Self.issues)
                        }
                        AboutPillButton(title: String(localized: "写反馈…"), small: true) {
                            activeSheet = .feedback
                        }
                    }
                }
                AboutNavRow(title: String(localized: "隐私说明"),
                            value: String(localized: "仅存本机 · 无统计")) {
                    activeSheet = .privacy
                }
                AboutNavRow(title: String(localized: "开源许可与致谢"),
                            value: String(localized: "GPL-3.0 与组件清单")) {
                    activeSheet = .licences
                }
                HStack(alignment: .center, spacing: 12) {
                    Text("源代码")
                        .font(.yy(size: 12.5, weight: .medium))
                        .foregroundStyle(YYColor.t1)
                    Spacer(minLength: 12)
                    AboutLinkButton(title: "github.com/inokoru/yuyue", prominent: true) {
                        NSWorkspace.shared.open(Self.source)
                    }
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 7)
                .frame(minHeight: 39)
            }
        }
        .animation(YYMotion.standard, value: center.phase)
    }

    private var versionHeaderRow: some View {
        let isEn = AppLanguage.running == .en
        return HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("第 \(UpdateCenter.current) 版")
                        .font(isEn ? .system(size: 17, weight: .semibold, design: .serif) : YYUI.serif(17, .semibold))
                        .monospacedDigit()
                        .foregroundStyle(YYColor.t1)
                    Text(verbatim: "build \(buildNumber)")
                        .font(.system(size: 11.5, design: .monospaced))
                        .foregroundStyle(YYColor.t3)
                }
                if center.phase != .release {
                    versionStatusLine
                }
            }
            Spacer(minLength: 10)
            if center.phase != .release {
                AboutPillButton(title: checkButtonTitle) {
                    Task { await center.check(manual: true) }
                }
                .disabled(center.phase == .checking)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
        .animation(YYMotion.standard, value: center.phase)
    }

    @ViewBuilder private var versionStatusLine: some View {
        if center.phase == .checking {
            Text("正在检查更新…")
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t2)
        } else if center.phase == .checkFailed {
            Text("无法检查更新，请检查网络连接后重试。")
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.danger)
        } else if center.lastWasCurrent, center.lastSuccess != nil {
            Text(center.checkedLine())
                .font(.yy(size: 12, weight: .medium))
                .foregroundStyle(YYColor.ok)
        } else if center.lastSuccess != nil {
            Text(center.checkedLine())
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t3)
        } else {
            Text("尚未检查更新")
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t3)
        }
    }

    private var checkButtonTitle: String {
        switch center.phase {
        case .checking: String(localized: "正在检查…")
        case .checkFailed: String(localized: "重试")
        default: String(localized: "检查更新")
        }
    }

    private var changelogSummary: String {
        guard let latest = releases.first else { return UpdateCenter.current }
        let items = Self.notes(for: latest)
        if !latest.date.isEmpty, !items.isEmpty {
            let day = latest.date.replacingOccurrences(of: "-", with: ".")
            return "\(day) · " + String(localized: "\(items.count) 项")
        }
        return latest.version
    }

    private func offerCard(_ offer: UpdateOffer) -> some View {
        let items = offer.notes.sections.flatMap(\.items)
        let meta = [UpdateRules.monthDay(offer.publishedAt), offer.assetSize > 0 ? UpdateRules.megabytes(offer.assetSize) : nil]
            .compactMap { $0 }
            .joined(separator: " · ")
        return VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(offer.display)
                    .font(YYUI.serif(18, .semibold))
                    .monospacedDigit()
                    .foregroundStyle(UpdatePaper.ink)
                Text("可以安装")
                    .font(.yy(size: 11, weight: .semibold))
                    .foregroundStyle(tint.accent)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 1.5)
                    .background(tint.black, in: Capsule())
                    .overlay(Capsule().strokeBorder(tint.edge, lineWidth: 0.5))
                Spacer(minLength: 8)
                if !meta.isEmpty {
                    Text(meta)
                        .font(.yy(size: 12))
                        .monospacedDigit()
                        .foregroundStyle(UpdatePaper.ink2)
                }
            }
            VStack(alignment: .leading, spacing: 5) {
                if !offer.notes.lede.isEmpty {
                    Text(offer.notes.lede)
                        .font(.yy(size: 12.5))
                        .lineSpacing(3)
                        .foregroundStyle(UpdatePaper.ink)
                        .fixedSize(horizontal: false, vertical: true)
                }
                ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                    HStack(alignment: .firstTextBaseline, spacing: 7) {
                        Circle()
                            .fill(tint.accent)
                            .frame(width: 4, height: 4)
                            .alignmentGuide(.firstTextBaseline) { $0.height }
                        Text(item)
                            .font(.yy(size: 12.5))
                            .lineSpacing(3)
                            .foregroundStyle(UpdatePaper.ink)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(.leading, 2)
                }
                if offer.notes.lede.isEmpty, items.isEmpty {
                    Text("这一版的说明写在发布页上。")
                        .font(.yy(size: 12.5))
                        .foregroundStyle(UpdatePaper.ink)
                }
            }
            .padding(.top, 7)
            .padding(.bottom, 10)
            HStack(spacing: 8) {
                AboutPillButton(title: String(localized: "下载并安装"), primary: true) {
                    center.install()
                }
                AboutPillButton(title: String(localized: "稍后提醒")) {
                    center.remindLater()
                }
                Button(String(localized: "跳过此版本")) {
                    center.skip()
                }
                .buttonStyle(.plain)
                .font(.yy(size: 12))
                .foregroundStyle(hoverSkip ? YYColor.t1 : YYColor.t3)
                .onHover { hoverSkip = $0 }
                .animation(YYMotion.quick, value: hoverSkip)
                .padding(.leading, 4)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 13)
        .background(UpdatePaper.bg, in: RoundedRectangle(cornerRadius: 13, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 13, style: .continuous).strokeBorder(UpdatePaper.line, lineWidth: 0.5))
    }

    // MARK: Sheets

    @ViewBuilder private func sheetView(for kind: SheetKind) -> some View {
        switch kind {
        case .changelog:
            AboutPaperSheet(title: String(localized: "更新日志"),
                            subtitle: String(localized: "每一版改了什么"),
                            close: { activeSheet = nil }) {
                ChangelogSheetContent(releases: releases, offer: center.offer, tint: tint, releasesURL: Self.releasesURL)
            }
        case .feedback:
            FeedbackForm { activeSheet = nil }
                .frame(width: 500)
                .frame(minHeight: 520, idealHeight: 532)
                .buttonStyle(.automatic)
                .paperWindow()
        case .privacy:
            AboutPaperSheet(title: String(localized: "隐私说明"),
                            subtitle: String(localized: "没有服务器，不收集任何数据"),
                            close: { activeSheet = nil }) {
                PrivacySheetContent()
            }
        case .licences:
            AboutPaperSheet(title: String(localized: "开源许可与致谢"),
                            subtitle: "GPL-3.0 · NOTICE",
                            close: { activeSheet = nil }) {
                LicencesSheetContent(tint: tint, noticeURL: Self.noticeURL)
            }
        }
    }

    fileprivate static func loadBundledReleases() -> [Release] {
        guard let url = Bundle.main.url(forResource: "CHANGELOG", withExtension: "md"),
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            return []
        }
        return parseChangelog(text: text)
    }

    fileprivate static func notes(for release: Release) -> [String] {
        let isEn = AppLanguage.running == .en
        let primary = isEn ? release.notesEn : release.notesZh
        let fallback = isEn ? release.notesZh : release.notesEn
        return primary.isEmpty ? fallback : primary
    }
}

// MARK: - About Row Pieces & Sheets

private struct AboutCard<Content: View>: View {
    @ViewBuilder let content: Content
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        YYDividedVStack(inset: 16) { content }
            .background(YYColor.card, in: .rect(cornerRadius: 13))
        .overlay(RoundedRectangle(cornerRadius: 13).strokeBorder(scheme == .dark ? Color(yy: YYRGBA(255, 255, 255, 0.06)) : YYColor.line, lineWidth: 0.5))
        .shadow(color: scheme == .dark ? .clear : Color(yy: YYRGBA(51, 46, 41, 0.04)), radius: 1, y: 1)
    }
}

private struct AboutRow<Trailing: View>: View {
    let title: String
    var detail: String?
    @ViewBuilder let trailing: Trailing

    init(_ title: String, detail: String? = nil, @ViewBuilder trailing: () -> Trailing) {
        self.title = title
        self.detail = detail
        self.trailing = trailing()
    }

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.yy(size: 12.5, weight: .medium))
                    .foregroundStyle(YYColor.t1)
                if let detail {
                    Text(detail)
                        .font(.yy(size: 11))
                        .lineSpacing(2.5)
                        .foregroundStyle(YYColor.t3)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 8)
            trailing
                .environment(\.settingsRowTitle, title)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 9)
        .frame(minHeight: 46)
    }
}

private struct AboutPillButton: View {
    let title: String
    var primary = false
    var small = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.yy(size: 12, weight: primary ? .semibold : .medium))
                .foregroundStyle(primary ? YYTint.standard.accent : YYColor.t1)
                .padding(.horizontal, small ? 11 : 14)
                .frame(height: small ? 25 : 28)
        }
        .buttonStyle(YYPillChrome(role: primary ? .primary : .secondary))
    }
}

private struct AboutNavRow: View {
    let title: String
    var value: String?
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(alignment: .center, spacing: 12) {
                Text(title)
                    .font(.yy(size: 12.5, weight: .medium))
                    .foregroundStyle(YYColor.t1)
                Spacer(minLength: 12)
                HStack(spacing: 3) {
                    if let value {
                        Text(value)
                            .font(.yy(size: 12))
                            .monospacedDigit()
                            .foregroundStyle(hover ? YYColor.t1 : YYColor.t3)
                    }
                    Image(systemName: "chevron.right")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(hover ? YYColor.t1 : YYColor.t3)
                        .offset(x: hover && !YYMotion.reduce ? 1.5 : 0)
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 7)
            .frame(minHeight: 39)
            .background(YYColor.t1.opacity(hover ? 0.028 : 0))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .animation(YYMotion.quick, value: hover)
    }
}

private struct AboutLinkButton: View {
    let title: String
    var prominent = false
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 3) {
                Text(verbatim: title)
                    .underline(hover, color: YYColor.t3)
                Image(systemName: "arrow.up.right")
                    .font(.system(size: 9.5, weight: .semibold))
            }
            .font(.yy(size: 12, weight: prominent ? .medium : .regular))
            .foregroundStyle(prominent ? YYColor.t1 : (hover ? YYColor.t1 : YYColor.t3))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .animation(YYMotion.quick, value: hover)
    }
}

private struct AboutPaperSheet<Content: View>: View {
    let title: String
    var subtitle: String?
    let close: () -> Void
    @ViewBuilder let content: Content

    var body: some View {
        VStack(spacing: 0) {
            YYSheetHeader(title: title, subtitle: subtitle, close: close)
                .padding(.leading, YYUI.s(20))
                .padding(.trailing, YYUI.s(16))
                .padding(.top, YYUI.s(15))
                .padding(.bottom, YYUI.s(10))
            Rectangle()
                .fill(YYColor.line)
                .frame(height: 0.5)
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    content
                }
                .padding(.horizontal, YYUI.s(20))
                .padding(.vertical, YYUI.s(16))
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .frame(width: 500, height: 470)
        .background(YYColor.bg)
        .buttonStyle(.automatic)
        .paperWindow()
    }
}

private struct ChangelogSheetContent: View {
    let releases: [Release]
    let offer: UpdateOffer?
    let tint: YYTint
    let releasesURL: URL

    var body: some View {
        let isEn = AppLanguage.running == .en
        if let offer, releases.first?.version != offer.display {
            VStack(alignment: .leading, spacing: 8) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("第 \(offer.display) 版")
                        .font(isEn ? .system(size: 16, weight: .semibold, design: .serif) : YYUI.serif(16, .semibold))
                        .monospacedDigit()
                        .foregroundStyle(YYColor.t1)
                    badge(String(localized: "有更新"))
                    Spacer()
                    if let day = UpdateRules.monthDay(offer.publishedAt) {
                        Text(day).font(.yy(size: 11.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                    }
                }
                UpdateNotesCard(offer: offer)
            }
        }
        ForEach(Array(releases.enumerated()), id: \.offset) { _, release in
            let items = MacUpdateSettings.notes(for: release)
            VStack(alignment: .leading, spacing: 8) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("第 \(release.version) 版")
                        .font(isEn ? .system(size: 16, weight: .semibold, design: .serif) : YYUI.serif(16, .semibold))
                        .monospacedDigit()
                        .foregroundStyle(YYColor.t1)
                    if release.version == UpdateCenter.current {
                        badge(String(localized: "当前版本"))
                    }
                    Spacer()
                    if let day = UpdateRules.monthDay(release.date) {
                        Text(day).font(.yy(size: 11.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                    }
                }
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                        HStack(alignment: .firstTextBaseline, spacing: 8) {
                            Circle()
                                .fill(tint.accent.opacity(0.85))
                                .frame(width: 4, height: 4)
                                .alignmentGuide(.firstTextBaseline) { $0.height }
                            Text(item)
                                .font(.yy(size: 12.5))
                                .lineSpacing(3)
                                .foregroundStyle(UpdatePaper.ink)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                }
                .padding(14)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(UpdatePaper.bg, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(UpdatePaper.line, lineWidth: 0.5))
            }
        }
        HStack {
            Spacer()
            AboutLinkButton(title: String(localized: "在 GitHub 查看全部发布记录")) {
                NSWorkspace.shared.open(releasesURL)
            }
            Spacer()
        }
        .padding(.top, 4)
    }

    private func badge(_ text: String) -> some View {
        Text(text)
            .font(.yy(size: 10, weight: .semibold))
            .foregroundStyle(tint.accent)
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .background(tint.black, in: Capsule())
            .overlay(Capsule().strokeBorder(tint.edge, lineWidth: 0.5))
    }
}

private struct PrivacySheetContent: View {
    private var items: [(String, String)] {
        [
            (String(localized: "没有账号，没有统计"),
             String(localized: "语阅没有服务器，也不收集任何数据：没有账号、没有统计、没有广告。")),
            (String(localized: "书与笔记留在本地"),
             String(localized: "书、进度、笔记和生词都存在这台设备上。打开同步时，它们只在你的设备和你自己选的网盘（iCloud 云盘、WebDAV，Google Drive 即将支持）之间传，语阅看不到。")),
            (String(localized: "你自己的服务密钥"),
             String(localized: "书伴默认用设备自带的模型，不联网。你填了自己的 Key 时，提问和书里相关的几段原文会直接发给你选的服务商，费用和隐私条款按你和它的约定；Key 只存在这台 Mac 上 App 沙盒文件夹里的明文文件中，只有你的用户账号能读，不同步、不上传。")),
            (String(localized: "云端声音与笔记导出"),
             String(localized: "免费在线语音会把当前要读的句子发给在线语音服务；自行配置的云端声音和笔记导出（Notion、flomo、新枝）只在你选用并填了 Key 或链接时联网，要读的句子或要发的笔记直接发给你选的服务。读过的声音存在这台设备上。")),
            (String(localized: "离线翻译与本地语音"),
             String(localized: "外文查词与翻译使用系统离线翻译；按需从 GitHub Releases、魔搭（ModelScope）或 Hugging Face 下载的对齐模型、声音包、开源字体与本地语音模型都在设备上离线运行（字体预览样字按需从 Google Fonts 加载），原文与录音不出本机。")),
            (String(localized: "问题反馈"),
             String(localized: "只有在你主动提交反馈时才会联网，通过 ntfy.sh 中转为公开的 GitHub Issue；正文与附件均会公开展示，不收集邮箱。")),
            (String(localized: "匿名检查更新与签名校验"),
             String(localized: "自动检查更新每天只从 GitHub 读取一次版本号文件，不带任何关于你和设备的信息（可在「关于」页关闭）；安装包经 Sparkle EdDSA 签名校验后才会替换。")),
        ]
    }

    var body: some View {
        SettingsCard {
            ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                VStack(alignment: .leading, spacing: 4) {
                    Text(item.0)
                        .font(.yy(size: 12.5, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                    Text(item.1)
                        .font(.yy(size: 11.5))
                        .lineSpacing(3)
                        .foregroundStyle(YYColor.t2)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 11)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }
}

private struct LicencesSheetContent: View {
    let tint: YYTint
    let noticeURL: URL

    private let coreLibraries: [(String, String)] = [
        ("Sparkle 2", "MIT"),
        ("UniFFI", "MPL-2.0"),
        ("SQLite · rusqlite", "Public Domain · MIT"),
        ("serde · ICU4X · blake3 · html5ever · roxmltree · lewton …", "MIT / Apache-2.0"),
        ("rars · rust-lzo1x · foliate-js", "MIT"),
        ("zhconv", "GPL-2.0-or-later"),
        ("OpenCC dictionaries", "Apache-2.0"),
        ("ONNX Runtime", "MIT"),
        ("ECDICT · pinyin / pypinyin", "MIT"),
        ("Simple Icons", "CC0 1.0"),
    ]

    private var voiceAndModels: [(String, String, String)] {
        [
            (String(localized: "内置声音包 voices-1 · 声音素材库 sounds-1"),
             String(localized: "由模型按文字描述生成，不含真人录音"),
             "CC0 1.0"),
            ("Kokoro-82M-v1.1-zh",
             String(localized: "按需下载的本地中英离线旁白语音模型"),
             "Apache-2.0"),
            ("3D-Speaker CAM++",
             String(localized: "按需下载的本地声纹识别模型"),
             "Apache-2.0"),
            (String(localized: "ZipVoice 权重 · Meta MMS 对齐模型"),
             String(localized: "按需下载，仅限非商业用途（Vocos 声码器为 MIT）"),
             "CC-BY-NC 4.0"),
        ]
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(verbatim: "语阅 Yuyue")
                    .font(YYUI.serif(15, .semibold))
                    .foregroundStyle(UpdatePaper.ink)
                Spacer()
                Text(verbatim: "GPL-3.0")
                    .font(.system(size: 11, weight: .medium, design: .monospaced))
                    .foregroundStyle(tint.accent)
            }
            Text("自由软件：你可以按 GNU General Public License 第 3 版的条款自由使用、修改和再分发。完整的许可文本随源代码一起发布。")
                .font(.yy(size: 11.5))
                .lineSpacing(3)
                .foregroundStyle(UpdatePaper.ink2)
                .fixedSize(horizontal: false, vertical: true)
            AboutLinkButton(title: String(localized: "在 GitHub 查看 NOTICE")) {
                NSWorkspace.shared.open(noticeURL)
            }
            .padding(.top, 2)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(UpdatePaper.bg, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(UpdatePaper.line, lineWidth: 0.5))

        Text("开源组件")
            .font(.yy(size: 11.5, weight: .semibold))
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, 2)

        SettingsCard {
            ForEach(Array(coreLibraries.enumerated()), id: \.offset) { _, item in
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    Text(verbatim: item.0)
                        .font(.yy(size: 12))
                        .foregroundStyle(YYColor.t1)
                    Spacer(minLength: 8)
                    Text(verbatim: item.1)
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(YYColor.t3)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 8.5)
            }
        }

        Text("声音与按需下载模型")
            .font(.yy(size: 11.5, weight: .semibold))
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, 2)

        SettingsCard {
            ForEach(Array(voiceAndModels.enumerated()), id: \.offset) { _, item in
                HStack(alignment: .center, spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.0)
                            .font(.yy(size: 12, weight: .medium))
                            .foregroundStyle(YYColor.t1)
                        Text(item.1)
                            .font(.yy(size: 10.5))
                            .foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 8)
                    Text(verbatim: item.2)
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(YYColor.t3)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 9)
            }
        }
    }
}
#endif
