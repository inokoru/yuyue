import SwiftUI

// The update screens (design pixels, scaled like the rest of the app).
// The theme colour in 外观 is what the buttons and the paper's marks use.

enum UpdatePaper {
    static let bg = Color.yyDynamic(light: YYRGBA(245, 244, 237), dark: YYRGBA(27, 26, 23))
    static let ink = Color.yyDynamic(light: YYRGBA(38, 37, 31), dark: YYRGBA(227, 222, 208))
    static let ink2 = Color.yyDynamic(light: YYRGBA(113, 110, 98), dark: YYRGBA(140, 135, 121))
    static let line = Color.yyDynamic(light: YYRGBA(38, 37, 31, 0.14), dark: YYRGBA(227, 222, 208, 0.14))
}

struct YuyueMark: View {
    var px: CGFloat = 64

    var body: some View {
        let s = YYUI.s(px)
        ZStack {
            RoundedRectangle(cornerRadius: s * 0.23, style: .continuous)
                .fill(Color(yy: YYRGBA(251, 250, 247)))
                .shadow(color: Color(yy: YYRGBA(30, 28, 24, 0.14)), radius: s * 0.12, y: s * 0.08)
                .overlay(RoundedRectangle(cornerRadius: s * 0.23, style: .continuous).strokeBorder(Color.black.opacity(0.10), lineWidth: 0.5))
            Text("阅")
                .font(YYUI.serif(s * 0.46, .bold))
                .foregroundStyle(Color(yy: YYRGBA(28, 29, 32)))
                .offset(y: -s * 0.05)
            Capsule()
                .fill(Color(yy: YYRGBA(43, 76, 126)))
                .frame(width: s * 0.375, height: max(2, s * 0.04))
                .offset(y: s * 0.27)
        }
        .frame(width: s, height: s)
        .accessibilityHidden(true)
    }
}

struct UpdateButton: View {
    enum Kind { case main, quiet, ghost }
    var title: String
    var kind: Kind = .quiet
    var wide = false
    var enabled = true
    var action: () -> Void
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        let tall = wide && kind == .main
        Button(action: action) {
            Text(title)
                .font(YYUI.font(tall ? 15 : 13, kind == .quiet ? .medium : .semibold))
                .foregroundStyle(foreground)
                .frame(maxWidth: wide ? .infinity : nil)
                .frame(height: YYUI.s(tall ? 40 : 32))
                .padding(.horizontal, wide ? 0 : YYUI.s(14))
                .background { if kind == .main { Capsule().fill(tint.black) } }
                .overlay { if kind == .main { Capsule().strokeBorder(tint.edge, lineWidth: 0.5) } }
                .opacity(enabled ? 1 : 0.55)
        }
        .buttonStyle(YYPressStyle())
        .disabled(!enabled)
    }

    private var foreground: Color {
        switch kind {
        case .main, .ghost: tint.accent
        case .quiet: YYColor.t2
        }
    }
}

struct UpdateNotesCard: View {
    let offer: UpdateOffer
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if !offer.notes.lede.isEmpty {
                Text(offer.notes.lede)
                    .font(YYUI.serif(YYUI.s(16.5), .regular))
                    .foregroundStyle(UpdatePaper.ink)
                    .lineSpacing(YYUI.s(4))
                    .fixedSize(horizontal: false, vertical: true)
            }
            ForEach(Array(offer.notes.sections.enumerated()), id: \.offset) { _, section in
                if !section.title.isEmpty {
                    Text(section.title)
                        .font(YYUI.font(11, .semibold))
                        .tracking(1.6)
                        .foregroundStyle(tint.accent)
                        .padding(.top, YYUI.s(16))
                        .padding(.bottom, YYUI.s(6))
                }
                ForEach(Array(section.items.enumerated()), id: \.offset) { _, item in
                    HStack(alignment: .firstTextBaseline, spacing: YYUI.s(8)) {
                        Circle().fill(tint.accent.opacity(0.85)).frame(width: 4, height: 4).alignmentGuide(.firstTextBaseline) { $0.height }
                        Text(item).font(YYUI.font(13.5)).foregroundStyle(UpdatePaper.ink).fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(.vertical, YYUI.s(3))
                }
            }
            if offer.notes.lede.isEmpty, offer.notes.sections.isEmpty {
                Text("这一版的说明写在发布页上。")
                    .font(YYUI.serif(YYUI.s(16.5), .regular))
                    .foregroundStyle(UpdatePaper.ink)
            }
            UpdateReleaseLink(url: offer.htmlURL)
                .padding(.top, YYUI.s(12))
            HStack {
                Text("语阅 · 第 \(offer.display) 版")
                Spacer()
                Text(UpdateRules.monthDay(offer.publishedAt) ?? "")
            }
            .font(YYUI.serif(YYUI.s(11), .regular))
            .foregroundStyle(UpdatePaper.ink2)
            .padding(.top, YYUI.s(10))
            .padding(.top, YYUI.s(14))
            .overlay(alignment: .top) { Rectangle().fill(UpdatePaper.line).frame(height: 0.5) }
        }
        .padding(YYUI.s(18))
        .background(UpdatePaper.bg, in: RoundedRectangle(cornerRadius: YYUI.s(18), style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(18), style: .continuous).strokeBorder(UpdatePaper.line, lineWidth: 0.5))
    }
}

/// The "完整说明" line is one sentence, with 发布页 in the theme colour.
struct UpdateReleaseLink: View {
    let url: URL
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        Link(destination: url) {
            (Text("完整说明在").foregroundStyle(UpdatePaper.ink2) + Text("发布页").foregroundStyle(tint.accent) + Text(verbatim: AppLanguage.running == .zh ? "。" : ".").foregroundStyle(UpdatePaper.ink2))
                .font(YYUI.font(12))
        }
    }
}

struct UpdateToggles: View {
    var mac: Bool = false
    private var center: UpdateCenter { UpdateCenter.shared }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            row(String(localized: "自动检查"), autoDetail, center.autoOn) { center.autoOn = $0 }
            Divider().overlay(YYColor.line)
            channelRow
        }
        .padding(.horizontal, mac ? 0 : YYUI.s(14))
        .background(mac ? Color.clear : YYColor.card, in: RoundedRectangle(cornerRadius: YYUI.s(16), style: .continuous))
        .overlay {
            if !mac {
                RoundedRectangle(cornerRadius: YYUI.s(16), style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5)
            }
        }
    }

    private var autoDetail: String {
        mac
            ? String(localized: "每天启动时自动检查；已跳过或延后提醒的版本不显示红点")
            : String(localized: "每天启动时自动检查；无新版本时不提醒")
    }

    private var channelRow: some View {
        HStack(alignment: .center, spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: 2) {
                Text("更新渠道").font(YYUI.font(15, .medium))
                Text(UpdateChannel.detail).font(YYUI.font(11.5)).foregroundStyle(YYColor.t3).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            Picker("更新渠道", selection: Binding(get: { center.channel }, set: { center.channel = $0 })) {
                ForEach(UpdateChannel.allCases) { Text($0.title).tag($0) }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .tint(YYColor.t2)
        }
        .padding(.vertical, YYUI.s(8))
        .frame(minHeight: YYUI.s(62))
    }

    private func row(_ title: String, _ detail: String, _ on: Bool, _ set: @escaping @MainActor (Bool) -> Void) -> some View {
        HStack(alignment: .center, spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(YYUI.font(15, .medium))
                Text(detail).font(YYUI.font(11.5)).foregroundStyle(YYColor.t3).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            Toggle(title, isOn: Binding(get: { on }, set: { val in set(val) }))
                .labelsHidden()
                .tint(YYTint.standard.fill)
        }
        .padding(.vertical, YYUI.s(8))
        .frame(minHeight: YYUI.s(62))
    }
}

#if os(iOS)
struct PhoneUpdate: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var howTo = false
    private var center: UpdateCenter { UpdateCenter.shared }

    var body: some View {
        YYPage {
            VStack(alignment: .leading, spacing: 0) { bodyContent }
                .frame(maxWidth: sizeClass == .regular ? 520 : .infinity)
                .frame(maxWidth: .infinity)
        }
        .navigationTitle(howTo ? "更新方法" : "检查更新")
        .navigationBarTitleDisplayMode(.inline)
        .navigationBarBackButtonHidden(showsClose)
        .toolbar {
            if showsClose {
                ToolbarItem(placement: .topBarLeading) {
                    Button("关闭") { dismiss() }.foregroundStyle(YYTint.standard.accent)
                }
            }
        }
        .navigationDestination(isPresented: $howTo) { PhoneUpdateHowTo() }
        .onAppear { center.boot() }
    }

    private var showsClose: Bool { fullRelease && !howTo }

    private var fullRelease: Bool {
        center.phase == .release && center.offer != nil
    }

    @ViewBuilder private var bodyContent: some View {
        switch center.phase {
        case .checking: status(checking: true)
        case .checkFailed: failedCheck
        case .skipped: skipped
        case .later: later
        case .release:
            if let offer = center.offer { release(offer) } else { status(checking: false) }
        case .current, .idle: status(checking: false)
        }
    }

    private func release(_ offer: UpdateOffer) -> some View {
        VStack(spacing: 0) {
            hero(kicker: String(localized: "新版本"), kickerWarn: false, version: offer.display, meta: meta(offer))
            UpdateNotesCard(offer: offer).padding(.top, YYUI.s(18))
            Text("这一版是签名装上的，系统不让它自己安装更新。新版本要在电脑上重新装。")
                .font(YYUI.font(13.5))
                .foregroundStyle(YYColor.t1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.top, YYUI.s(14))
            UpdateButton(title: String(localized: "查看更新方法"), kind: .main, wide: true) { howTo = true }
                .padding(.top, YYUI.s(14))
            HStack(spacing: YYUI.s(8)) {
                UpdateButton(title: String(localized: "稍后提醒")) { center.remindLater() }
                UpdateButton(title: String(localized: "跳过此版本")) { center.skip() }
            }
            .frame(maxWidth: .infinity)
            .padding(.top, YYUI.s(8))
        }
    }

    private var skipped: some View {
        VStack(alignment: .leading, spacing: 0) {
            card {
                HStack {
                    Text(center.offer?.display ?? "").font(YYUI.serif(YYUI.s(22), .semibold))
                    Spacer()
                    Text("已跳过").font(YYUI.font(12)).foregroundStyle(YYColor.t3)
                }
                Text("红点不再为这一版本亮起，有新版本时仍会提醒（仅保存在本机）。")
                    .font(YYUI.font(13)).foregroundStyle(YYColor.t2).padding(.top, YYUI.s(8))
                    .fixedSize(horizontal: false, vertical: true)
                UpdateButton(title: String(localized: "查看此版本"), kind: .main) { center.lookAnyway() }
                    .padding(.top, YYUI.s(8))
            }
            facts.padding(.top, YYUI.s(12))
            toggles.padding(.top, YYUI.s(12))
        }
    }

    private var later: some View {
        VStack(alignment: .leading, spacing: 0) {
            card {
                HStack {
                    Text(center.offer?.display ?? "").font(YYUI.serif(YYUI.s(22), .semibold))
                    Spacer()
                    Text(center.remindUntil.map(UpdateWhen.monthDay) ?? "").font(YYUI.font(12)).foregroundStyle(YYColor.t3).monospacedDigit()
                }
                Text("你选了稍后。到那天之前，红点不出现。想现在看，从这里进。")
                    .font(YYUI.font(13)).foregroundStyle(YYColor.t2).padding(.top, YYUI.s(8))
                HStack(spacing: YYUI.s(8)) {
                    UpdateButton(title: String(localized: "现在查看"), kind: .main) { center.lookAnyway() }
                    UpdateButton(title: String(localized: "不再提醒")) { center.skip() }
                }
                .padding(.top, YYUI.s(8))
            }
            Text("「稍后」是 3 天。日子只记在这台设备上，不进同步。")
                .font(YYUI.font(11.5)).foregroundStyle(YYColor.t3).padding(.top, YYUI.s(10))
            toggles.padding(.top, YYUI.s(12))
        }
    }

    private func status(checking: Bool) -> some View {
        VStack(spacing: 0) {
            hero(kicker: nil, kickerWarn: false, version: nil, meta: center.currentLabel, name: String(localized: "语阅"))
            if checking {
                Text("正在检查更新…")
                    .font(YYUI.font(12.5)).foregroundStyle(YYColor.t2).padding(.top, YYUI.s(8))
            } else if center.lastWasCurrent, center.lastSuccess != nil {
                Text(center.checkedLine())
                    .font(YYUI.font(13, .semibold)).foregroundStyle(YYColor.ok).padding(.top, YYUI.s(8))
            }
            UpdateButton(title: checking ? String(localized: "正在检查…") : String(localized: "检查更新"), kind: .main, wide: true, enabled: !checking) {
                Task { await center.check(manual: true) }
            }
            .padding(.top, YYUI.s(14))
            toggles.padding(.top, YYUI.s(12))
            Text("更新检查仅获取版本信息，不上传任何本地数据。")
                .font(YYUI.font(11.5)).foregroundStyle(YYColor.t3).padding(.top, YYUI.s(10))
        }
    }

    private var failedCheck: some View {
        VStack(spacing: 0) {
            hero(kicker: nil, kickerWarn: false, version: nil, meta: center.currentLabel, name: String(localized: "语阅"))
            Text("无法检查更新，请检查网络连接后重试。")
                .font(YYUI.font(13, .semibold)).foregroundStyle(YYColor.danger)
                .multilineTextAlignment(.center).padding(.top, YYUI.s(8))
            UpdateButton(title: String(localized: "重试"), kind: .main, wide: true) { Task { await center.check(manual: true) } }
                .padding(.top, YYUI.s(14))
            Text(center.offlineFine())
                .font(YYUI.font(11.5)).foregroundStyle(YYColor.t3).multilineTextAlignment(.center).padding(.top, YYUI.s(10))
            toggles.padding(.top, YYUI.s(12))
        }
    }

    private var toggles: some View { UpdateToggles().padding(.top, YYUI.s(4)) }

    private var facts: some View {
        VStack(spacing: 0) {
            fact(String(localized: "当前版本"), center.currentLabel)
            fact(String(localized: "上次检查"), center.lastSuccess.map(UpdateWhen.phrase) ?? String(localized: "尚未检查"))
        }
        .padding(.horizontal, YYUI.s(14))
        .background(YYColor.card, in: RoundedRectangle(cornerRadius: YYUI.s(16)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(16)).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private func fact(_ title: String, _ value: String) -> some View {
        HStack {
            Text(title).font(YYUI.font(15))
            Spacer()
            Text(value).font(YYUI.font(13)).foregroundStyle(YYColor.t3).monospacedDigit()
        }
        .frame(minHeight: YYUI.s(46))
    }

    private func card<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 0) { content() }
            .padding(YYUI.s(14))
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(YYColor.card, in: RoundedRectangle(cornerRadius: YYUI.s(16)))
            .overlay(RoundedRectangle(cornerRadius: YYUI.s(16)).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private func hero(kicker: String?, kickerWarn: Bool, version: String?, meta: String?, name: String? = nil) -> some View {
        VStack(spacing: 0) {
            YuyueMark().padding(.bottom, YYUI.s(12))
            if let kicker {
                Text(kicker).font(YYUI.font(12, .semibold)).tracking(kickerWarn ? 1.2 : 1.8)
                    .foregroundStyle(kickerWarn ? YYColor.warn : YYTint.standard.accent)
            }
            if let name {
                Text(name).font(YYUI.font(17, .bold)).padding(.top, YYUI.s(8))
            }
            if let version {
                Text(version).font(YYUI.serif(YYUI.s(42), .semibold)).monospacedDigit().padding(.top, YYUI.s(4))
            }
            if let meta {
                Text(meta).font(YYUI.font(12.5)).foregroundStyle(YYColor.t3).monospacedDigit().padding(.top, YYUI.s(6))
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, YYUI.s(8))
    }

    private func meta(_ offer: UpdateOffer) -> String {
        let day = UpdateRules.monthDay(offer.publishedAt).map { " · \($0)" } ?? ""
        return String(localized: "从 \(UpdateCenter.current)\(day)")
    }
}

struct PhoneUpdateHowTo: View {
    @Environment(\.dismiss) private var dismiss
    @State private var copied = false
    private var url: URL { UpdateCenter.shared.offer?.htmlURL ?? URL(string: "https://github.com/\(UpdateRules.repo)/releases")! }

    var body: some View {
        YYPage {
            VStack(alignment: .leading, spacing: 0) {
                Text("iPhone 装不上自己下载的安装包。新的 .ipa 要在电脑上签名，再装回来。")
                    .font(YYUI.font(14.5))
                    .foregroundStyle(YYColor.t1)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, YYUI.s(8))
                step("1", String(localized: "在电脑上下载"), String(localized: "打开这一版的发布页，下载 .ipa。手机上下不了，也装不上。"))
                step("2", String(localized: "用你的 Apple ID 签名"), String(localized: "Impactor 可以签并装上。也可以用 Xcode 从源代码编译。"))
                step("3", String(localized: "打开开发者模式"), String(localized: "免费 Apple ID 的签名 7 天后过期，到时要重新签。"))
                Text("Impactor 是别人做的工具，要输入 Apple ID，建议用小号。这种装法苹果可能改，语阅不保证一直能用。")
                    .font(YYUI.font(12.5)).foregroundStyle(YYColor.t2)
                    .padding(YYUI.s(12))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(YYColor.card, in: RoundedRectangle(cornerRadius: YYUI.s(16)))
                    .overlay(RoundedRectangle(cornerRadius: YYUI.s(16)).strokeBorder(YYColor.line, lineWidth: 0.5))
                    .padding(.top, YYUI.s(14))
                UpdateButton(title: copied ? String(localized: "已复制") : String(localized: "复制发布页链接"), kind: .main, wide: true) {
                    UIPasteboard.general.string = url.absoluteString
                    copied = true
                }
                .padding(.top, YYUI.s(14))
                UpdateButton(title: String(localized: "在浏览器打开"), kind: .ghost, wide: true) { UIApplication.shared.open(url) }
                Text("发布页打不开时，用你平时打开 GitHub 的办法，在电脑上下同一个 .ipa。语阅不内置镜像。")
                    .font(YYUI.font(11.5)).foregroundStyle(YYColor.t3).padding(.top, YYUI.s(10))
            }
        }
        .navigationTitle("更新方法")
        .navigationBarTitleDisplayMode(.inline)
        .navigationBarBackButtonHidden(true)
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                Button { dismiss() } label: {
                    HStack(spacing: 2) {
                        Image(systemName: "chevron.left")
                        Text("更新")
                    }
                    .foregroundStyle(YYTint.standard.accent)
                }
            }
        }
    }

    private func step(_ n: String, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: YYUI.s(12)) {
            Text(n)
                .font(YYUI.font(12, .semibold))
                .foregroundStyle(YYTint.standard.accent)
                .frame(width: YYUI.s(22), height: YYUI.s(22))
                .background(YYTint.standard.black, in: Circle())
                .overlay(Circle().strokeBorder(YYTint.standard.edge, lineWidth: 0.5))
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(YYUI.font(14.5, .semibold))
                Text(detail).font(YYUI.font(12.5)).foregroundStyle(YYColor.t2).fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.vertical, YYUI.s(12))
        .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
    }
}

struct PhoneSignature: View {
    @Environment(\.dismiss) private var dismiss
    @State private var howTo = false

    var body: some View {
        NavigationStack {
            YYPage {
                VStack(alignment: .leading, spacing: 0) {
                    VStack(spacing: 0) {
                        YuyueMark().padding(.bottom, YYUI.s(12))
                        Text("签名快到期")
                            .font(YYUI.font(12, .semibold)).tracking(1.2).foregroundStyle(YYColor.warn)
                        Text("\(UpdateCenter.shared.signatureDaysLeft ?? 0) 天")
                            .font(YYUI.serif(YYUI.s(42), .semibold)).padding(.top, YYUI.s(4))
                    }
                    .frame(maxWidth: .infinity)
                    Text("免费 Apple ID 签的语阅，满 7 天就打不开。这是按这台\(UpdateDevice.name)第一次打开估算的，语阅问不到苹果。")
                        .font(YYUI.font(14.5)).foregroundStyle(YYColor.t1)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.top, YYUI.s(16))
                    Text("重签还是要在电脑上，用 Impactor 或 Xcode，和当初安装一样。有新版本的话，顺手签新的那一版。")
                        .font(YYUI.font(12.5)).foregroundStyle(YYColor.t2)
                        .padding(YYUI.s(12))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(YYColor.card, in: RoundedRectangle(cornerRadius: YYUI.s(16)))
                        .overlay(RoundedRectangle(cornerRadius: YYUI.s(16)).strokeBorder(YYColor.line, lineWidth: 0.5))
                        .padding(.top, YYUI.s(14))
                    UpdateButton(title: String(localized: "查看重签步骤"), kind: .main, wide: true) { howTo = true }
                        .padding(.top, YYUI.s(14))
                    UpdateButton(title: String(localized: "我刚重新签过"), kind: .ghost, wide: true) {
                        UpdateCenter.shared.resignNoted()
                        dismiss()
                    }
                    Text("点过「我刚重新签过」，这 7 天从头算。只记在这台\(UpdateDevice.name)上。")
                        .font(YYUI.font(11.5)).foregroundStyle(YYColor.t3).padding(.top, YYUI.s(10))
                }
            }
            .navigationTitle("签名")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("关闭") { dismiss() }.foregroundStyle(YYTint.standard.accent)
                }
            }
            .navigationDestination(isPresented: $howTo) { PhoneUpdateHowTo() }
        }
    }
}
#endif
