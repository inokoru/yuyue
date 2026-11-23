#if os(iOS)
import SwiftUI

/// 沉浸设置: one switch to go in, then what goes with it, each of which can
/// be kept. From a book the switch is for that book now; from 我的 it is for every book opened.
struct PhoneImmersiveSheet: View {
    var model: ReaderModel?
    @AppStorage(ImmersivePrefs.alwaysKey) private var always = false
    @AppStorage(ImmersivePrefs.statusKey) private var status = true
    @AppStorage(ImmersivePrefs.progressKey) private var progress = true
    @AppStorage(ImmersivePrefs.quietKey) private var quiet = true
    @AppStorage(ImmersivePrefs.hintsKey) private var hints = true
    @AppStorage(ImmersivePrefs.stillKey) private var still = true
    @AppStorage(ImmersivePrefs.marksKey) private var marks = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: YYUI.s(8)) {
                    Image(systemName: "viewfinder").font(YYUI.font(13, .medium)).foregroundStyle(YYColor.t1)
                    Text("沉浸阅读").font(YYUI.font(13, .semibold)).foregroundStyle(YYColor.t1)
                    Spacer()
                    YYSwitch(isOn: Binding { model?.immersive ?? always } set: { on in
                        if let model {
                            model.chromeShown = false
                            model.immersive = on
                        } else {
                            always = on
                        }
                    }, label: String(localized: "沉浸阅读"))
                }
                Text(model == nil ? String(localized: "打开书籍时自动进入沉浸阅读，以下项目将默认隐藏或关闭，也可逐项保留。") : String(localized: "开启后全屏隐藏界面要素，以下项目将默认隐藏或关闭，也可逐项保留。"))
                    .font(YYUI.font(10)).lineSpacing(YYUI.s(3)).foregroundStyle(YYColor.t3)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, YYUI.s(6))
                    .padding(.bottom, YYUI.s(12))
                YYGroup {
                    row(String(localized: "隐藏状态栏、时间和电量"), $status)
                    row(String(localized: "隐藏进度、页码和剩余时间"), $progress)
                    row(String(localized: "专注模式联动"), $quiet)
                    row(String(localized: "隐藏书伴提示与难词注释"), $hints)
                    row(String(localized: "关闭翻页动画"), $still)
                    row(String(localized: "隐藏划线与想法"), $marks)
                }
                if quiet {
                    YYNote(String(localized: "iOS 暂不支持应用自动开启勿扰；请在系统「设置 › 专注模式」中添加「阅读」，阅读前从控制中心开启。"))
                        .transition(.opacity)
                }
                Text("双击屏幕边缘进出，轻点中央临时唤出控制栏")
                    .font(YYUI.font(9.5)).foregroundStyle(YYColor.t3)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: .infinity)
                    .padding(.top, YYUI.s(14))
            }
            .animation(YYMotion.standard, value: quiet)
            .padding(.horizontal, YYUI.s(14))
            .padding(.top, YYUI.sheetGrabberTop)
            .padding(.bottom, YYUI.s(12))
        }
        .scrollBounceBehavior(.basedOnSize)
        .background(YYColor.bg)
        .presentationDetents([.height(YYUI.s(440)), .large])
        .presentationDragIndicator(.visible)
        // Each change shows at once on the page behind.
        .onChange(of: [hints, still, marks]) { if let model { model.immersive = model.immersive } }
    }

    private func row(_ title: String, _ on: Binding<Bool>) -> some View {
        YYRow(title: title) { YYSwitch(isOn: on, label: title) }
    }
}
#endif

#if os(iOS)
/// 我的 › 墨水屏: local display mode toggle, optional grayscale for colour e-ink, and live preview.
struct PhoneEInk: View {
    let library: Library
    @AppStorage(EInk.key) private var on = false
    @AppStorage(EInk.grayscaleKey) private var grayscale = true

    var body: some View {
        YYPage {
            YYGroup {
                YYRow(title: String(localized: "墨水屏模式"), icon: "rectangle.portrait.on.rectangle.portrait") {
                    YYSwitch(isOn: Binding { on } set: { EInk.set($0) }, label: String(localized: "墨水屏模式"))
                }
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("封面与插图转为灰度").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                        Text("彩墨屏可关闭以保留原色").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: YYUI.s(8))
                    YYSwitch(isOn: Binding { grayscale } set: { EInk.setGrayscale($0) }, label: String(localized: "封面与插图转为灰度"))
                }
                .padding(.vertical, YYUI.s(8))
                .opacity(on ? 1 : 0.45)
                .disabled(!on)
            }
            .padding(.top, YYUI.s(10))
            sample.padding(.top, YYUI.s(14))
            YYNote(String(localized: "仅在本机生效：阅读页切换为纯白底与纯黑字（无颗粒噪点），划线转为黑线，并关闭翻页动画、阴影与毛玻璃效果；不改动跨设备同步的纸色，关闭后恢复原纸色。"))
        }
        .navigationTitle("墨水屏")
        .navigationBarTitleDisplayMode(.inline)
    }

    /// A few lines on the current paper: when 墨水屏模式 is on, pure black on pure white with black underlines;
    /// when off, the reader's current shared paper so flipping the switch previews the change live.
    private var sample: some View {
        let shared = (try? library.core.readerSettings()) ?? .standard
        let shownPaper = on ? EInk.paper : shared.paperTokens
        let size = YYUI.s(13.5)
        var text = AttributedString(String(localized: "父亲说：“事已如此，不必难过，"))
        var marked = AttributedString(String(localized: "好在天无绝人之路！”"))
        if on {
            marked.underlineStyle = Text.LineStyle(pattern: .solid, color: shownPaper.ink)
        } else {
            marked.backgroundColor = shownPaper.hl
        }
        text += marked
        var wave = AttributedString(String(localized: "回家变卖典质，父亲还了亏空；"))
        wave.underlineStyle = Text.LineStyle(pattern: .dot, color: on ? shownPaper.ink : shownPaper.wave)
        var second = AttributedString("\n")
        second += wave
        second += AttributedString(String(localized: "又借钱办了丧事。"))
        return VStack(alignment: .leading, spacing: YYUI.s(8)) {
            Text("背影").font(YYUI.serif(YYUI.s(18), .semibold)).tracking(YYUI.s(18) * 0.05)
            Text(text + second).font(YYUI.serif(size)).lineSpacing(size * 1.05)
        }
        .foregroundStyle(shownPaper.ink)
        .padding(YYUI.s(18))
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(shownPaper.bg, in: .rect(cornerRadius: YYUI.s(12)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(on ? shownPaper.ink : shownPaper.line, lineWidth: on ? 1 : 0.75))
        .accessibilityHidden(true)
    }
}
#endif
