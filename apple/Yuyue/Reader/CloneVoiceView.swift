import AVFoundation
import SwiftUI
import UniformTypeIdentifiers
import YuyueCore

/// 克隆一个声音。
/// 先勾选本人或知情同意 → 照着固定短句念约 8 秒（停顿 1.2 秒自动收尾，或导入录音）→
/// `cardCheck` 实时把关 → 自动用新声音试念当前书的下一句 → 起名存成本机 `VoiceCard`。
struct CloneVoiceView: View {
    let core: YuyueCore
    let dataDir: URL
    let language: String
    var castContext: CastContext? = nil
    var forName: String? = nil
    var pathBinding: Binding<[CastSheet.Route]>? = nil
    var existingID: String? = nil
    var initialName: String = ""
    var previewSentence: String? = nil
    /// The book the try-out line comes from: 正在用你的声音念《…》下一句.
    var bookTitle: String? = nil
    var onSaved: ((VoiceCard) -> Void)? = nil
    var onClose: (() -> Void)? = nil
    @Environment(\.dismiss) private var dismiss

    @State private var recorder = SampleRecorder()
    @State private var consent = false
    @State private var importing = false
    @State private var imported = false
    @State private var checking = false
    @State private var decodedPCM: (samples: [Float], rate: UInt32)?
    @State private var transcript = ""
    @State private var checkResult: CardCheck?
    @State private var tempWavPath: String?
    @State private var previewingSample = false
    /// The try-out line is sounding: the bars move only then.
    @State private var sampleSounding = false
    @State private var name = ""
    @State private var saving = false
    @State private var savedCard: VoiceCard?
    @State private var failure: String?

    init(context: CastContext, forName: String?, path: Binding<[CastSheet.Route]>) {
        self.core = context.core
        self.dataDir = LiveVoiceModel.context?.dataDir ?? ClonedVoices.dir.deletingLastPathComponent()
        self.language = context.language
        self.castContext = context
        self.forName = forName
        self.pathBinding = path
        self.previewSentence = Self.nextBookSentence(core: context.core, bookID: context.bookID, section: context.section, player: context.player, language: context.language)
        self.bookTitle = (try? context.core.book(id: context.bookID))?.summary.title
    }

    init(
        core: YuyueCore,
        dataDir: URL,
        language: String,
        existingID: String? = nil,
        initialName: String = "",
        previewSentence: String? = nil,
        bookTitle: String? = nil,
        onSaved: ((VoiceCard) -> Void)? = nil,
        onClose: (() -> Void)? = nil
    ) {
        self.bookTitle = bookTitle
        self.core = core
        self.dataDir = dataDir
        self.language = language
        self.existingID = existingID
        self.initialName = initialName
        self.previewSentence = previewSentence
        self.onSaved = onSaved
        self.onClose = onClose
    }

    private var canonicalLang: String {
        CardVoice.canonicalLanguage(language)
    }

    private var promptScript: String {
        core.cardScript(lang: canonicalLang, style: .neutral)
    }

    private var nextLineToPreview: String {
        if let previewSentence, !previewSentence.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return previewSentence
        }
        return canonicalLang == "en"
            ? "The lamp on the desk was still warm, and outside the rain had just stopped."
            : "窗外的雨不知什么时候停了，屋里只剩下翻书的轻响。"
    }

    var body: some View {
        CloneCardPage {
            VStack(alignment: .leading, spacing: YYUI.s(12)) {
                header
                    #if os(iOS)
                    .padding(.top, YYUI.s(8))
                    #endif
                if decodedPCM == nil { consentCard }
                if checkResult?.ok != true { promptCard }
                Group {
                    if decodedPCM == nil {
                        recordingControls
                    } else if checking {
                        checkingCard
                    } else if let checkResult, !checkResult.ok {
                        faultCard(checkResult)
                    } else if checkResult?.ok == true {
                        passReviewSection
                    }
                }
                .transition(YYMotion.swap)
            }
            // 录 → 检查 → 不行 / 能用: each step takes the last one's place, not in a cut.
            .animation(YYMotion.standard, value: decodedPCM == nil)
            .animation(YYMotion.standard, value: checking)
            .animation(YYMotion.standard, value: checkResult?.ok)
        }
        #if DEBUG
        // `-debugCloneStep fault|pass`: the 不合格 / 能用 steps without a microphone.
        .task {
            guard let step = UserDefaults.standard.string(forKey: "debugCloneStep") else { return }
            consent = true
            decodedPCM = (Array(repeating: 0, count: 16000 * 8), 16000)
            transcript = promptScript
            checkResult = step == "fault"
                ? CardCheck(ok: false, seconds: 3.8, speechDb: -38, snrDb: 9, clippedRatio: 0, cer: nil,
                            faults: [.tooShort, .noisy], messages: [String(localized: "太短了，再多念一点"), String(localized: "背景有点吵")])
                : CardCheck(ok: true, seconds: 8.4, speechDb: -22, snrDb: 31, clippedRatio: 0, cer: 0.02, faults: [], messages: [])
        }
        #endif
        // The page's own head carries the title and 取消, as one card.
        .toolbar(.hidden)
        .navigationBarBackButtonHidden(true)
        .fileImporter(isPresented: $importing, allowedContentTypes: [.audio]) { result in
            guard case .success(let url) = result else { return }
            let ok = url.startAccessingSecurityScopedResource()
            defer { if ok { url.stopAccessingSecurityScopedResource() } }
            let ext = url.pathExtension.isEmpty ? "m4a" : url.pathExtension
            let copy = FileManager.default.temporaryDirectory.appending(path: "yuyue-import-\(UUID().uuidString).\(ext)")
            if (try? FileManager.default.copyItem(at: url, to: copy)) != nil {
                evaluateRecording(copy, importedFile: true)
            }
        }
        .yyNotice(
            String(localized: "没存下"),
            message: failure ?? "",
            isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })
        )
        .onAppear {
            if name.isEmpty {
                name = initialName.isEmpty ? (forName ?? String(localized: "我的声音")) : initialName
            }
            recorder.onSilenceStop = {
                stopRecording()
            }
        }
        .onDisappear {
            recorder.stop()
            Voices.stopPreview()
            cleanupTempWav()
        }
    }

    // MARK: - Sections

    /// 克隆一个声音 · 取消; once it passes, 听听像不像 · ✓ 录音合格 · 8.4 秒.
    private var header: some View {
        HStack {
            Text(checkResult?.ok == true
                 ? String(localized: "听听像不像")
                 : existingID != nil ? String(localized: "重新录一段") : String(localized: "克隆一个声音"))
                .font(YYUI.font(15, .semibold))
                .foregroundStyle(YYColor.t1)
            Spacer(minLength: 8)
            if let check = checkResult, check.ok {
                Text("✓ 录音合格 · \(String(format: "%.1f", check.seconds)) 秒")
                    .font(YYUI.font(11, .semibold))
                    .monospacedDigit()
                    .foregroundStyle(YYTint.standard.accent)
            } else {
                Button(String(localized: "取消"), action: cancel)
                    .buttonStyle(.plain)
                    .font(YYUI.font(12))
                    .foregroundStyle(YYColor.t3)
                    .keyboardShortcut(.cancelAction)
            }
        }
        .frame(minHeight: YYUI.s(24))
    }

    private func cancel() {
        recorder.stop()
        if pathBinding == nil, let onClose { onClose() } else { dismiss() }
    }

    private var consentCard: some View {
        Button { consent.toggle() } label: {
            HStack(alignment: .top, spacing: YYUI.s(8)) {
                ZStack {
                    RoundedRectangle(cornerRadius: 5)
                        .fill(consent ? YYTint.standard.accent : YYColor.card)
                    RoundedRectangle(cornerRadius: 5)
                        .strokeBorder(consent ? .clear : YYColor.t3, lineWidth: 1.2)
                    if consent {
                        Image(systemName: "checkmark")
                            .font(.system(size: 9, weight: .bold))
                            .foregroundStyle(.white)
                    }
                }
                .frame(width: 16, height: 16)
                .padding(.top, 1)
                VStack(alignment: .leading, spacing: 2) {
                    Text("这是我的声音，或者我得到了本人同意")
                        .font(YYUI.font(11.5))
                        .foregroundStyle(YYColor.t1)
                    if !consent {
                        Text("先勾上这一项才能开始录音或导入音频")
                            .font(YYUI.font(10))
                            .foregroundStyle(YYColor.t3)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, YYUI.s(11))
            .padding(.vertical, YYUI.s(9))
            .background(YYColor.bg, in: .rect(cornerRadius: YYUI.s(11)))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .animation(YYMotion.quick, value: consent)
    }

    private var promptCard: some View {
        VStack(alignment: .leading, spacing: YYUI.s(6)) {
            if imported && decodedPCM != nil {
                YYLabel(String(localized: "录音里念的字"))
                YYGroup {
                    TextField(String(localized: "把这段录音对应的原话填准，合成更稳"), text: $transcript, axis: .vertical)
                        .lineLimit(2 ... 5)
                        .font(YYUI.serif(14))
                        .padding(.vertical, YYUI.s(8))
                        .onSubmit { recheckImportedTranscript() }
                }
            } else {
                VStack(alignment: .leading, spacing: YYUI.s(5)) {
                    HStack {
                        Text(decodedPCM == nil ? String(localized: "请用平常自然的语气念这一句") : String(localized: "刚才念的话"))
                        Spacer()
                        Text(decodedPCM == nil ? String(localized: "约 8 秒") : String(localized: "重录即可"))
                    }
                    .font(YYUI.font(10.5))
                    .foregroundStyle(YYColor.t3)
                    Text(promptScript)
                        .font(YYUI.serif(15))
                        .lineSpacing(9)
                        .foregroundStyle(YYPaper.kami.ink)
                }
                .padding(.horizontal, YYUI.s(14))
                .padding(.vertical, YYUI.s(13))
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(YYPaper.kami.bg, in: .rect(cornerRadius: YYUI.s(13)))
                .overlay(RoundedRectangle(cornerRadius: YYUI.s(13)).strokeBorder(Color(red: 80/255, green: 75/255, blue: 55/255).opacity(0.15), lineWidth: 0.5))
            }
        }
    }

    /// ①：中间一颗圆的录音键，下面九根电平和一句提示；底下一行“不出设备”和导入。
    private var recordingControls: some View {
        VStack(spacing: YYUI.s(12)) {
            VStack(spacing: YYUI.s(8)) {
                Button {
                    recorder.recording ? stopRecording() : startRecording()
                } label: {
                    ZStack {
                        Circle()
                            .fill(recorder.recording ? YYColor.danger.opacity(0.08) : YYTint.standard.soft)
                        Circle()
                            .strokeBorder(recorder.recording ? YYColor.danger.opacity(0.35) : YYTint.standard.edge, lineWidth: 1)
                        if recorder.recording {
                            RoundedRectangle(cornerRadius: 3)
                                .fill(YYColor.danger)
                                .frame(width: YYUI.s(14), height: YYUI.s(14))
                        } else {
                            Image(systemName: "mic")
                                .font(YYUI.font(19))
                                .foregroundStyle(YYTint.standard.accent)
                        }
                    }
                    .frame(width: YYUI.s(54), height: YYUI.s(54))
                    .shadow(color: YYTint.standard.accent.opacity(recorder.recording ? 0 : 0.12), radius: 6, y: 4)
                    .contentShape(.circle)
                }
                .buttonStyle(YYPressStyle())
                .disabled(!consent)
                .opacity(consent ? 1 : 0.38)
                .accessibilityLabel(Text(recorder.recording ? "停下" : "开始录"))

                HStack(spacing: 3) {
                    let levels = Array(recorder.levels.suffix(9))
                    ForEach(0 ..< 9, id: \.self) { i in
                        let l = i < levels.count ? CGFloat(levels[levels.count - 1 - i]) : 0
                        Capsule()
                            .fill(recorder.recording ? YYTint.standard.accent : YYColor.line)
                            .frame(width: 3, height: recorder.recording ? max(5, l * 18) : 5)
                    }
                }
                .frame(height: 18)
                .animation(.easeOut(duration: 0.15), value: recorder.levels.count)

                Text(recorder.recording
                     ? String(localized: "正在录音 \(Self.time(recorder.seconds)) · 念完停顿 1.2 秒自动停")
                     : String(localized: "点一下开始录，念完停顿一秒自动结束"))
                    .font(YYUI.font(11.5))
                    .monospacedDigit()
                    .foregroundStyle(YYColor.t2)
            }
            .frame(maxWidth: .infinity)
            .padding(.top, YYUI.s(10))
            .padding(.bottom, YYUI.s(4))
            .animation(YYMotion.quick, value: recorder.recording)
            .animation(YYMotion.standard, value: consent)

            Rectangle().fill(YYColor.line).frame(height: 0.5)
            HStack {
                Text("不联网，录音只留在这台设备")
                    .font(YYUI.font(11))
                    .foregroundStyle(YYColor.t3)
                Spacer()
                Button(String(localized: "导入一段录音…")) { importing = true }
                    .buttonStyle(.plain)
                    .font(YYUI.font(11, .medium))
                    .foregroundStyle(YYTint.standard.accent)
                    .disabled(!consent)
                    .opacity(consent ? 1 : 0.42)
            }
        }
    }

    private var checkingCard: some View {
        HStack(spacing: YYUI.s(10)) {
            ProgressView().controlSize(.small)
            Text("正在听清不清楚…")
                .font(YYUI.font(12))
                .foregroundStyle(YYColor.t2)
            Spacer()
        }
        .padding(.horizontal, YYUI.s(14))
        .padding(.vertical, YYUI.s(14))
        .yyCard()
    }

    private func faultCard(_ check: CardCheck) -> some View {
        VStack(alignment: .leading, spacing: YYUI.s(10)) {
            HStack(alignment: .top, spacing: YYUI.s(10)) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(check.messages.first ?? String(localized: "这段录音不太清楚，再录一次试试"))
                        .font(YYUI.font(12, .semibold))
                        .foregroundStyle(YYColor.danger)
                    ForEach(Self.faultNumbers(check), id: \.self) { line in
                        Text(line)
                            .font(YYUI.font(11))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t2)
                            .lineLimit(1)
                            .minimumScaleFactor(0.85)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, YYUI.s(13)).padding(.vertical, YYUI.s(11))
            .background(YYColor.danger.opacity(0.08), in: .rect(cornerRadius: YYUI.s(12)))
            .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.danger.opacity(0.24), lineWidth: 0.5))

            HStack(spacing: YYUI.s(8)) {
                Spacer(minLength: 0)
                if imported {
                    Button(String(localized: "改好文字再检一次")) { recheckImportedTranscript() }
                        .buttonStyle(YYButtonStyle(kind: .plain, size: .regular, height: 28))
                }
                Button(action: redo) { Text("再录一次").fontWeight(.semibold) }
                    .buttonStyle(YYButtonStyle(kind: .soft, size: .regular, height: 28))
            }
        }
    }

    /// ②：每条不合格后面跟上量出来的数，说清差多少。
    static func faultNumbers(_ check: CardCheck) -> [String] {
        check.faults.map { fault in
            switch fault {
            case .tooShort:
                String(localized: "只录了 \(String(format: "%.1f", check.seconds)) 秒 · 念完上面那句话（6–15 秒）会更稳")
            case .tooLong:
                String(localized: "录了 \(String(format: "%.1f", check.seconds)) 秒 · 15 秒以内就够")
            case .tooQuiet:
                String(localized: "说话音量 \(Int(check.speechDb.rounded())) dBFS · 离麦克风近一点再念")
            case .tooLoud:
                String(localized: "说话音量 \(Int(check.speechDb.rounded())) dBFS · 稍微拿远一点")
            case .clipped:
                String(localized: "有 \(String(format: "%.1f", check.clippedRatio * 100))% 的采样削波 · 稍微拿远一点")
            case .noisy:
                String(localized: "信噪比 \(Int(check.snrDb.rounded())) dB（需 ≥ 20 dB）")
            case .wrongWords:
                String(localized: "听写核对错字率 \(Int(((check.cer ?? 0) * 100).rounded()))% · 照着卡片上的字念即可")
            }
        }
    }

    private var passReviewSection: some View {
        VStack(alignment: .leading, spacing: YYUI.s(12)) {
            Button(action: playSamplePreview) {
                VStack(alignment: .leading, spacing: YYUI.s(4)) {
                    HStack {
                        Text(bookTitle.map { String(localized: "正在用你的声音念《\($0)》下一句") } ?? String(localized: "正在用你的声音念下一句"))
                            .lineLimit(1)
                        Spacer(minLength: 6)
                        if previewingSample {
                            ProgressView().controlSize(.mini)
                        } else {
                            SpeakingBars(paused: !sampleSounding)
                        }
                    }
                    .font(YYUI.font(10.5))
                    .foregroundStyle(YYColor.t3)
                    Text("“\(nextLineToPreview)”")
                        .font(YYUI.serif(14))
                        .lineSpacing(7)
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(3)
                        .multilineTextAlignment(.leading)
                }
                .padding(.horizontal, YYUI.s(13)).padding(.vertical, YYUI.s(12))
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(YYColor.bg, in: .rect(cornerRadius: YYUI.s(12)))
                .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.line, lineWidth: 0.5))
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .disabled(previewingSample)
            .help(String(localized: "再听一遍"))

            HStack(spacing: YYUI.s(8)) {
                Text("名字").font(YYUI.font(11.5)).foregroundStyle(YYColor.t3)
                TextField(String(localized: "我的声音"), text: $name)
                    .textFieldStyle(.plain)
                    .font(YYUI.font(13, .medium))
            }
            .padding(.horizontal, YYUI.s(11)).padding(.vertical, YYUI.s(8))
            .background(YYColor.bg, in: .rect(cornerRadius: YYUI.s(10)))
            .overlay(RoundedRectangle(cornerRadius: YYUI.s(10)).strokeBorder(YYColor.line, lineWidth: 0.5))

            if let card = savedCard, let ctx = castContext {
                useWhere(card, ctx)
            } else {
            HStack(spacing: YYUI.s(8)) {
                Spacer(minLength: 0)
                Button(String(localized: "再录一次"), action: redo)
                    .buttonStyle(YYButtonStyle(kind: .plain, size: .regular, height: 28))
                Button(action: saveCard) {
                    HStack(spacing: 5) {
                        if saving { ProgressView().controlSize(.mini) }
                        Text("用这个声音").fontWeight(.semibold)
                    }
                }
                .buttonStyle(YYButtonStyle(kind: .soft, size: .regular, height: 28))
                .disabled(saving || name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            }
        }
    }

    /// Saved from 人物声音: 用在哪, as the card's last row instead of a system dialog.
    private func useWhere(_ card: VoiceCard, _ ctx: CastContext) -> some View {
        VStack(alignment: .leading, spacing: YYUI.s(8)) {
            Text("已经存在这台设备上。用在哪？")
                .font(YYUI.font(11.5))
                .foregroundStyle(YYColor.t2)
            HStack(spacing: YYUI.s(8)) {
                Spacer(minLength: 0)
                Button(String(localized: "先放着")) { finishCastFlow() }
                    .buttonStyle(YYButtonStyle(kind: .plain, size: .regular, height: 28))
                Button(String(localized: "全书（旁白）")) {
                    let voice = ClonedVoices.resolveVoice(card.id, bookID: ctx.bookID) ?? CardVoice(card: card, bookID: ctx.bookID)
                    ctx.player?.choose(voice, for: ctx.language)
                    finishCastFlow()
                }
                .buttonStyle(YYButtonStyle(kind: forName == nil ? .soft : .plain, size: .regular, height: 28))
                if let forName {
                    Button { 
                        var v = ctx.role(forName) ?? RoleVoice(voice: nil, rate: 1, pitch: 0, right: false)
                        v.voice = card.id
                        ctx.set(forName, v)
                        finishCastFlow()
                    } label: { Text("给\(forName)").fontWeight(.semibold) }
                    .buttonStyle(YYButtonStyle(kind: .soft, size: .regular, height: 28))
                }
            }
        }
    }

    // MARK: - Actions

    private func startRecording() {
        guard consent else { return }
        Task {
            guard await SampleRecorder.allowed() else {
                failure = String(localized: "语阅没有用麦克风的权限。在系统设置的“隐私与安全性 › 麦克风”里打开。")
                return
            }
            castContext?.player?.pause()
            Voices.stopPreview()
            recorder.start()
        }
    }

    private func stopRecording() {
        guard let url = recorder.stop(), recorder.seconds >= 1.0 else { return }
        evaluateRecording(url, importedFile: false)
    }

    private func evaluateRecording(_ url: URL, importedFile: Bool) {
        imported = importedFile
        checking = true
        checkResult = nil
        cleanupTempWav()
        if name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            name = !initialName.isEmpty
                ? initialName
                : (importedFile ? url.deletingPathExtension().lastPathComponent : String(localized: "我的声音"))
        }
        let targetScript = promptScript
        let lang = canonicalLang
        let core = core
        Task {
            guard let decoded = ClonedVoices.decodeAudioFile(url) else {
                checking = false
                decodedPCM = ([], 24_000)
                checkResult = CardCheck(
                    ok: false,
                    seconds: 0,
                    speechDb: -60,
                    snrDb: 0,
                    clippedRatio: 0,
                    cer: 1,
                    faults: [.tooShort],
                    messages: [String(localized: "没读出这段音频，换一段常见的 m4a 或 wav 试试")]
                )
                return
            }
            decodedPCM = decoded
            let secs = Double(decoded.samples.count) / Double(max(1, decoded.rate))
            var heard: String? = nil
            if Transcriber.available {
                let r = await Transcriber.words(
                    url: url,
                    spans: [TimeSpan(t0: 0, t1: min(secs, 35))],
                    lang: lang,
                    offset: 0,
                    punctuated: importedFile
                )
                if case let .transcribe(words) = r, !words.isEmpty {
                    heard = words.map(\.text).joined(separator: lang == "en" ? " " : "")
                }
            }
            let spokenText: String
            if importedFile {
                spokenText = (heard ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                transcript = spokenText
            } else {
                spokenText = targetScript
                transcript = targetScript
            }
            if importedFile && spokenText.isEmpty {
                checking = false
                checkResult = CardCheck(
                    ok: false,
                    seconds: Float(secs),
                    speechDb: -20,
                    snrDb: 25,
                    clippedRatio: 0,
                    cer: nil,
                    faults: [],
                    messages: [String(localized: "请先在上方填上这段录音里说的原话，再点“改好文字再检一次”")]
                )
                return
            }
            let check = core.cardCheck(
                samples: decoded.samples,
                rate: decoded.rate,
                text: spokenText,
                heard: importedFile ? nil : heard
            )
            checkResult = check
            checking = false
            if check.ok {
                prepareTempAndPreview(samples: decoded.samples, rate: decoded.rate, text: spokenText)
            }
        }
    }

    private func recheckImportedTranscript() {
        guard let decoded = decodedPCM else { return }
        let clean = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else { return }
        let check = core.cardCheck(samples: decoded.samples, rate: decoded.rate, text: clean, heard: nil)
        checkResult = check
        if check.ok {
            prepareTempAndPreview(samples: decoded.samples, rate: decoded.rate, text: clean)
        }
    }

    private func prepareTempAndPreview(samples: [Float], rate: UInt32, text: String) {
        cleanupTempWav()
        let wav = FileManager.default.temporaryDirectory.appending(path: "yuyue-clone-prev-\(UUID().uuidString).wav")
        if (try? core.cardWriteWav(path: wav.path(percentEncoded: false), samples: samples, rate: rate)) != nil {
            tempWavPath = wav.path(percentEncoded: false)
            playSamplePreview()
        }
    }

    /// Plays the try-out and keeps the bars moving for as long as it lasts.
    private func sound(_ speech: Speech) async {
        ClonePreviewPlayer.shared.play(speech)
        previewingSample = false
        sampleSounding = true
        try? await Task.sleep(for: .seconds(speech.seconds))
        sampleSounding = false
    }

    private func playSamplePreview() {
        guard let wavPath = tempWavPath else { return }
        let refText = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        let line = nextLineToPreview
        let lang = canonicalLang
        previewingSample = true
        Voices.stopPreview()
        Task {
            defer { previewingSample = false }
            if let speech = try? await CardVoice.previewReference(
                path: wavPath,
                refText: refText,
                refLang: lang,
                text: line,
                language: lang
            ) {
                await sound(speech)
            } else if let fallback = SystemVoice.best(for: lang),
                      let speech = try? await fallback.render(line, language: lang) {
                await sound(speech)
            }
        }
    }

    private func redo() {
        Voices.stopPreview()
        ClonePreviewPlayer.shared.stop()
        cleanupTempWav()
        decodedPCM = nil
        checkResult = nil
        imported = false
        recorder.discard()
    }

    private func saveCard() {
        guard let decoded = decodedPCM else { return }
        let cleanName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let cleanText = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleanName.isEmpty, !cleanText.isEmpty else { return }
        saving = true
        ClonePreviewPlayer.shared.stop()
        let core = core
        let dataDir = dataDir
        let existingID = existingID
        let lang = canonicalLang
        Task {
            defer { saving = false }
            do {
                let card = try await ClonedVoices.saveLocalCard(
                    core: core,
                    dataDir: dataDir,
                    existingID: existingID,
                    samples: decoded.samples,
                    rate: decoded.rate,
                    text: cleanText,
                    lang: lang,
                    name: cleanName
                )
                onSaved?(card)
                if castContext != nil {
                    if let ctx = castContext, forName == nil {
                        // 从“旁白的声音”进入时直接设为当前书语言旁白并返回。
                        let voice = ClonedVoices.resolveVoice(card.id, bookID: ctx.bookID) ?? CardVoice(card: card, bookID: ctx.bookID)
                        ctx.player?.choose(voice, for: ctx.language)
                        NotificationCenter.default.post(name: CastContext.changed, object: nil)
                        finishCastFlow()
                    } else {
                        savedCard = card
                    }
                } else {
                    onClose?()
                }
            } catch {
                failure = error.localizedDescription
            }
        }
    }

    private func finishCastFlow() {
        savedCard = nil
        if var p = pathBinding?.wrappedValue, !p.isEmpty {
            p.removeLast()
            pathBinding?.wrappedValue = p
        } else {
            onClose?()
        }
    }

    private func cleanupTempWav() {
        if let p = tempWavPath {
            try? FileManager.default.removeItem(atPath: p)
            tempWavPath = nil
        }
    }

    static func nextBookSentence(core: YuyueCore, bookID: String, section: UInt32, player: Player?, language: String) -> String {
        let sec = player?.spot?.section ?? section
        let all = (try? core.spokenSentences(bookId: bookID, section: sec)) ?? []
        let startIdx = (player?.spot?.sentence ?? 0) + 1
        if all.indices.contains(startIdx) {
            let candidate = all[startIdx].trimmingCharacters(in: .whitespacesAndNewlines)
            if candidate.count >= 6 { return candidate }
        }
        if let good = all.first(where: { $0.trimmingCharacters(in: .whitespacesAndNewlines).count >= 8 }) {
            return good
        }
        return Voices.sampleText(language)
    }

    static func time(_ s: Double) -> String {
        String(format: "%d:%02d", Int(s) / 60, Int(s) % 60)
    }
}

// MARK: - 克隆声音详情（补录六档语气、同步、ElevenLabs 备份、重命名、删除）

struct ClonedCardDetailView: View {
    let entryID: String
    let core: YuyueCore
    let dataDir: URL
    let language: String
    var onDeleted: (() -> Void)? = nil

    @Environment(\.dismiss) private var dismiss
    @State private var entry: ClonedEntry?
    @State private var recordingStyle: CardStyle?
    @State private var showingRerecord = false
    @State private var renaming = false
    @State private var newName = ""
    @State private var confirmingDelete: YYConfirm?
    @State private var uploadingEleven = false
    @State private var notice: String?

    private static let extraStyles: [(style: CardStyle, title: String)] = [
        (.joy, "高兴"),
        (.anger, "生气"),
        (.sad, "难过"),
        (.tense, "紧张"),
        (.soft, "低声"),
        (.shout, "喊"),
    ]

    var body: some View {
        CloneCardPage {
            if let entry {
              VStack(alignment: .leading, spacing: YYUI.s(12)) {
                headerCard(entry)
                    #if os(iOS)
                    .padding(.top, YYUI.s(8))
                    #endif

                if let card = entry.card {
                    HStack {
                        Text("加上语气").font(YYUI.font(11, .semibold)).foregroundStyle(YYColor.t1)
                        Spacer()
                        Text("可只录几档，没录的用平常语气").font(YYUI.font(11)).foregroundStyle(YYColor.t2)
                    }
                    .padding(.bottom, -YYUI.s(4))
                    VStack(spacing: 0) {
                        ForEach(Array(Self.extraStyles.enumerated()), id: \.element.style) { i, item in
                            if i > 0 { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                            styleRow(card: card, style: item.style, label: String(localized: String.LocalizationValue(item.title)))
                        }
                    }
                    .padding(.horizontal, YYUI.s(10)).padding(.vertical, YYUI.s(2))
                    .background(YYColor.bg, in: .rect(cornerRadius: YYUI.s(12)))
                    .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.line, lineWidth: 0.5))

                    VStack(spacing: 0) {
                        syncRow(card: card)
                        if CloneEngine.available {
                            Rectangle().fill(YYColor.line).frame(height: 0.5)
                            elevenLabsRow(entry: entry)
                        }
                    }
                    .padding(.horizontal, YYUI.s(2))
                } else {
                    YYGroup {
                        VStack(alignment: .leading, spacing: YYUI.s(8)) {
                            Text("这段旧克隆录音还没有本机文字特征，重录一句 8 秒的话即可在本机离线使用。")
                                .font(YYUI.font(11.5))
                                .foregroundStyle(YYColor.t2)
                            Button {
                                showingRerecord = true
                            } label: {
                                Text("重录一句")
                                    .font(YYUI.font(12, .semibold))
                                    .foregroundStyle(YYTint.standard.onBlack)
                                    .padding(.horizontal, YYUI.s(14))
                                    .frame(height: YYUI.s(32))
                                    .background(YYTint.standard.black, in: .capsule)
                            }
                            .buttonStyle(.plain)
                        }
                        .padding(.vertical, YYUI.s(10))
                    }
                    .padding(.top, YYUI.s(10))
                }

                Rectangle().fill(YYColor.line).frame(height: 0.5)
                HStack {
                    Button(String(localized: "重命名")) {
                        newName = entry.name
                        renaming = true
                    }
                    .buttonStyle(YYButtonStyle(kind: .plain, size: .regular, height: 28))
                    Spacer()
                    Button(String(localized: "删除")) {
                        confirmingDelete = YYConfirm(
                            title: String(localized: "删除“\(entry.name)”？"),
                            message: String(localized: "这台设备上的参考录音会一并删掉。"),
                            button: String(localized: "删除")
                        ) {
                            ClonedVoices.deleteCard(id: entry.id, core: core)
                            onDeleted?()
                            dismiss()
                        }
                    }
                    .buttonStyle(YYButtonStyle(kind: .danger, size: .regular, height: 28))
                }
              }
            }
        }
        .toolbar(.hidden)
        .navigationBarBackButtonHidden(true)
        .onAppear(perform: reload)
        .onReceive(NotificationCenter.default.publisher(for: ClonedVoices.changed)) { _ in reload() }
        .sheet(item: Binding(
            get: { recordingStyle.map(IdentifiableStyle.init) },
            set: { recordingStyle = $0?.style }
        )) { item in
            if let card = entry?.card {
                NavigationStack {
                    StyleClipRecordSheet(core: core, dataDir: dataDir, card: card, style: item.style) {
                        recordingStyle = nil
                        reload()
                    }
                }
                .presentationBackground(YYColor.bg)
            }
        }
        .sheet(isPresented: $showingRerecord) {
            NavigationStack {
                CloneVoiceView(
                    core: core,
                    dataDir: dataDir,
                    language: language,
                    existingID: entryID,
                    initialName: entry?.name ?? "",
                    onSaved: { _ in
                        showingRerecord = false
                        reload()
                    },
                    onClose: { showingRerecord = false }
                )
            }
            .presentationBackground(YYColor.bg)
        }
        .yyPrompt(
            String(localized: "重命名"),
            text: $newName,
            prompt: String(localized: "名字"),
            isPresented: $renaming,
            confirm: String(localized: "保存")
        ) {
            try? ClonedVoices.renameCard(id: entryID, to: newName, core: core)
            reload()
        }
        .yyConfirm($confirmingDelete)
        .yyNotice(
            String(localized: "提示"),
            message: notice ?? "",
            isPresented: Binding(get: { notice != nil }, set: { if !$0 { notice = nil } })
        )
    }

    private func reload() {
        #if DEBUG
        if entryID == "debug", var card = LiveVoiceModel.context?.core.cardsBuiltin().first {
            card.name = String(localized: "我的声音")
            card.origin = .cloned
            card.clips = card.clips.filter { [.neutral, .joy, .anger].contains($0.style) }
            entry = ClonedEntry(id: "debug", name: card.name, card: card, meta: nil)
            return
        }
        #endif
        entry = ClonedVoices.entries().first { $0.id == entryID }
    }

    /// ‹ 我的声音 [克隆] · ▶ 试听.
    private func headerCard(_ entry: ClonedEntry) -> some View {
        HStack(spacing: YYUI.s(6)) {
            Button { dismiss() } label: {
                Image(systemName: "chevron.left")
                    .font(YYUI.font(12, .semibold))
                    .foregroundStyle(YYColor.t2)
                    .frame(width: YYUI.s(20), height: YYUI.s(24))
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.cancelAction)
            .accessibilityLabel(Text("返回"))
            Text(entry.name).font(YYUI.font(15, .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
            Text("克隆")
                .font(YYUI.font(9.5, .medium))
                .foregroundStyle(YYColor.t2)
                .padding(.horizontal, 5).padding(.vertical, 1)
                .background(YYColor.inset, in: .rect(cornerRadius: 4))
            Spacer()
            if let voice = ClonedVoices.resolveVoice(entry.id), entry.isPlayable {
                Button {
                    Voices.preview(voice, text: Voices.sampleText(language))
                } label: {
                    Text("▶ 试听")
                }
                .buttonStyle(YYButtonStyle(kind: .plain, size: .small, height: 24))
            }
        }
    }

    /// 高兴 · 已录 · 7.6 秒 · ▶ 试听 · 重录, or 难过 · 未录 · 录一句.
    private func styleRow(card: VoiceCard, style: CardStyle, label: String) -> some View {
        let clip = card.clips.first { $0.style == style }
        return HStack(spacing: YYUI.s(8)) {
            Text(label).font(YYUI.font(12, .semibold)).foregroundStyle(YYColor.t1)
                .frame(width: YYUI.s(36), alignment: .leading)
            Text(clip.map { String(localized: "已录 · \(String(format: "%.1f", $0.seconds)) 秒") } ?? String(localized: "未录"))
                .font(YYUI.font(11))
                .monospacedDigit()
                .foregroundStyle(clip != nil ? YYTint.standard.accent : YYColor.t3)
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
            if clip != nil {
                Button {
                    Task {
                        let cv = CardVoice(card: card, bookID: nil)
                        if let sp = try? await cv.render(core.cardScript(lang: card.lang, style: style), language: card.lang, style: style) {
                            ClonePreviewPlayer.shared.play(sp)
                        }
                    }
                } label: {
                    Text("▶ 试听")
                }
                .buttonStyle(YYButtonStyle(kind: .plain, size: .small, height: 22))
                .accessibilityLabel(Text("试听\(label)"))
                Button(String(localized: "重录")) { recordingStyle = style }
                    .buttonStyle(.plain)
                    .font(YYUI.font(11))
                    .foregroundStyle(YYColor.t3)
            } else {
                Button(String(localized: "录一句")) { recordingStyle = style }
                    .buttonStyle(YYButtonStyle(kind: .plain, size: .small, height: 22))
            }
        }
        .padding(.horizontal, YYUI.s(2))
        .padding(.vertical, YYUI.s(7))
    }

    private func syncRow(card: VoiceCard) -> some View {
        let b = Binding<Bool>(
            get: { card.sync },
            set: { on in
                _ = try? ClonedVoices.setSync(card, on, core: core)
                reload()
            }
        )
        return HStack(spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: 2) {
                Text("跟着同步").font(YYUI.font(12, .medium)).foregroundStyle(YYColor.t1)
                Text("同步到你的其他设备（默认关）").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
            }
            Spacer()
            YYSwitch(isOn: b, label: String(localized: "跟着同步"))
        }
        .padding(.vertical, YYUI.s(8))
    }

    private func elevenLabsRow(entry: ClonedEntry) -> some View {
        let b = Binding<Bool>(
            get: { entry.useElevenLabs },
            set: { on in
                uploadingEleven = true
                let id = entry.id
                let name = entry.name
                Task {
                    defer { uploadingEleven = false; reload() }
                    do {
                        _ = try await ClonedVoices.setUseElevenLabs(id: id, name: name, on: on)
                    } catch {
                        notice = error.localizedDescription
                    }
                }
            }
        )
        return HStack(spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: 2) {
                Text("也用 ElevenLabs 念").font(YYUI.font(12, .medium)).foregroundStyle(YYColor.t1)
                Text("打开时才会把参考录音传到你自己的 ElevenLabs 账号").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
            }
            Spacer()
            if uploadingEleven {
                ProgressView().controlSize(.small)
            } else {
                YYSwitch(isOn: b, label: String(localized: "也用 ElevenLabs 念"))
            }
        }
        .padding(.vertical, YYUI.s(8))
    }

    private struct IdentifiableStyle: Identifiable {
        let style: CardStyle
        var id: CardStyle { style }
    }
}

/// 补录单档语气的小面板：固定短句 + 1.2 秒静音自动收尾 + `cardCheck` + `saveStyleClip`。
private struct StyleClipRecordSheet: View {
    let core: YuyueCore
    let dataDir: URL
    let card: VoiceCard
    let style: CardStyle
    let onDone: () -> Void

    @State private var recorder = SampleRecorder()
    @State private var checking = false
    @State private var faultMessages: [String] = []
    @State private var saving = false

    private var scriptText: String {
        core.cardScript(lang: card.lang, style: style)
    }

    var body: some View {
        YYPage {
            VStack(alignment: .leading, spacing: YYUI.s(12)) {
                Text(core.cardStyleInstruction(style: style, chinese: card.lang != "en"))
                    .font(YYUI.font(11.5, .medium))
                    .foregroundStyle(YYColor.t2)
                    .padding(.top, YYUI.s(8))

                Text(scriptText)
                    .font(YYUI.serif(15))
                    .lineSpacing(8)
                    .foregroundStyle(YYColor.t1)
                    .padding(YYUI.s(14))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .yyCard()

                Button {
                    recorder.recording ? finishAndSave() : start()
                } label: {
                    HStack(spacing: YYUI.s(10)) {
                        Circle().fill(YYColor.danger).frame(width: YYUI.s(10), height: YYUI.s(10))
                        Text(CloneVoiceView.time(recorder.seconds)).font(YYUI.font(16, .bold)).monospacedDigit()
                        Spacer()
                        if checking || saving {
                            ProgressView().controlSize(.small)
                        } else {
                            Text(recorder.recording ? "停下并保存" : "点一下开始念")
                                .font(YYUI.font(12, .semibold))
                        }
                    }
                    .padding(.horizontal, YYUI.s(16))
                    .frame(height: YYUI.s(54))
                    .yyCard()
                }
                .buttonStyle(.plain)
                .disabled(checking || saving)

                if !faultMessages.isEmpty {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(faultMessages, id: \.self) { m in
                            Text(m).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.danger)
                        }
                    }
                    .padding(YYUI.s(12))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(YYColor.danger.opacity(0.08), in: .rect(cornerRadius: YYUI.s(10)))
                }
            }
        }
        .yySheetPage(String(localized: "录一句语气"), close: { recorder.stop(); onDone() })
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button(String(localized: "取消")) {
                    recorder.stop()
                    onDone()
                }
            }
        }
        #endif
        .onAppear {
            recorder.onSilenceStop = { finishAndSave() }
        }
        .onDisappear {
            recorder.stop()
        }
        #if os(macOS)
        .frame(width: 380, height: 320)
        #endif
    }

    private func start() {
        faultMessages = []
        Task {
            guard await SampleRecorder.allowed() else {
                faultMessages = [String(localized: "请先在系统设置里允许语阅使用麦克风")]
                return
            }
            recorder.start()
        }
    }

    private func finishAndSave() {
        guard let url = recorder.stop(), recorder.seconds >= 1.0 else { return }
        checking = true
        faultMessages = []
        let target = scriptText
        let lang = card.lang
        Task {
            guard let decoded = ClonedVoices.decodeAudioFile(url) else {
                checking = false
                faultMessages = [String(localized: "没录清楚，再试一次")]
                return
            }
            let secs = Double(decoded.samples.count) / Double(max(1, decoded.rate))
            var heard: String? = nil
            if Transcriber.available {
                let r = await Transcriber.words(url: url, spans: [TimeSpan(t0: 0, t1: min(secs, 20))], lang: lang, offset: 0)
                if case let .transcribe(words) = r, !words.isEmpty {
                    heard = words.map(\.text).joined(separator: lang == "en" ? " " : "")
                }
            }
            let check = core.cardCheck(samples: decoded.samples, rate: decoded.rate, text: target, heard: heard)
            checking = false
            guard check.ok else {
                faultMessages = check.messages.isEmpty ? [String(localized: "声音不太清楚，再念一次试试")] : check.messages
                recorder.discard()
                return
            }
            saving = true
            defer { saving = false }
            do {
                _ = try await ClonedVoices.saveStyleClip(
                    core: core,
                    dataDir: dataDir,
                    card: card,
                    style: style,
                    samples: decoded.samples,
                    rate: decoded.rate,
                    text: target
                )
                onDone()
            } catch {
                faultMessages = [error.localizedDescription]
            }
        }
    }
}

@MainActor private final class ClonePreviewPlayer {
    static let shared = ClonePreviewPlayer()
    private let player = ClipPlayer()
    func play(_ speech: Speech) { player.play(speech) }
    func stop() { player.stop() }
}

/// 麦克风录音器：0.1 秒电平采样，检测到开口说话后若连续静音满 1.2 秒自动停下。
@MainActor @Observable final class SampleRecorder {
    private(set) var recording = false
    private(set) var seconds: Double = 0
    private(set) var levels: [Float] = []
    @ObservationIgnored var onSilenceStop: (() -> Void)?

    @ObservationIgnored private var recorder: AVAudioRecorder?
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var hasSpoken = false
    @ObservationIgnored private var silentSeconds: Double = 0
    @ObservationIgnored private let url = FileManager.default.temporaryDirectory.appending(path: "yuyue-clone-\(UUID().uuidString).m4a")

    static func allowed() async -> Bool {
        await AVAudioApplication.requestRecordPermission()
    }

    func start() {
        discard()
        hasSpoken = false
        silentSeconds = 0
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.playAndRecord, mode: .default, options: [.defaultToSpeaker])
        try? AVAudioSession.sharedInstance().setActive(true)
        #endif
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: 44_100,
            AVNumberOfChannelsKey: 1,
            AVEncoderAudioQualityKey: AVAudioQuality.high.rawValue,
        ]
        guard let r = try? AVAudioRecorder(url: url, settings: settings) else { return }
        r.isMeteringEnabled = true
        guard r.record() else { return }
        recorder = r
        recording = true
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
    }

    private func tick() {
        guard let r = recorder, r.isRecording else { return }
        r.updateMeters()
        seconds = r.currentTime
        let db = r.averagePower(forChannel: 0)
        levels.append(max(0.08, min(1, (db + 50) / 50)))

        if seconds >= 0.8 && db > -36 {
            hasSpoken = true
            silentSeconds = 0
        } else if hasSpoken && seconds >= 2.8 {
            if db < -42 {
                silentSeconds += 0.1
                if silentSeconds >= 1.2 {
                    onSilenceStop?()
                    return
                }
            } else {
                silentSeconds = 0
            }
        }
        if seconds >= 45 {
            onSilenceStop?()
        }
    }

    @discardableResult func stop() -> URL? {
        timer?.invalidate()
        timer = nil
        guard let r = recorder else { return nil }
        seconds = r.currentTime > 0 ? r.currentTime : seconds
        r.stop()
        recorder = nil
        recording = false
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio, policy: .longFormAudio)
        #endif
        return url
    }

    func discard() {
        _ = stop()
        try? FileManager.default.removeItem(at: url)
        seconds = 0
        levels = []
        hasSpoken = false
        silentSeconds = 0
    }
}

/// Four thin bars that rise and fall: a voice is speaking. Paused, they hold where they were
/// and go on from there when it speaks again, never snapping to a fixed shape.
struct SpeakingBars: View {
    var color: Color = YYTint.standard.accent
    var paused = false
    /// Time spent paused, taken off the clock so the bars resume from where they stopped.
    @State private var shift: TimeInterval = 0
    @State private var stoppedAt: Date?

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 20, paused: paused || YYMotion.reduce)) { ctx in
            let t = (stoppedAt ?? ctx.date).timeIntervalSinceReferenceDate - shift
            HStack(spacing: 2) {
                ForEach(0 ..< 4, id: \.self) { i in
                    let phase = (t / 0.72 + Double(i) * 0.17).truncatingRemainder(dividingBy: 1)
                    let h = YYMotion.reduce ? 0.7 : 0.35 + 0.65 * abs(sin(phase * .pi))
                    Capsule().fill(color).frame(width: 2, height: 12 * h)
                }
            }
            .frame(height: 12)
        }
        .onAppear { if paused { stoppedAt = .now } }
        .onChange(of: paused) { _, now in
            if now {
                stoppedAt = .now
            } else if let at = stoppedAt {
                shift += Date.now.timeIntervalSince(at)
                stoppedAt = nil
            }
        }
    }
}

/// The clone pages' frame. On the Mac a card 348 wide and as tall as what it holds, so
/// nothing hangs empty under the last row; on a phone a page that scrolls.
struct CloneCardPage<Content: View>: View {
    @ViewBuilder let content: Content

    static var macWidth: CGFloat { 348 }

    var body: some View {
        #if os(macOS)
        VStack(alignment: .leading, spacing: 0) { content }
            .padding(.horizontal, 18)
            .padding(.top, 16)
            .padding(.bottom, 18)
            .frame(width: Self.macWidth, alignment: .topLeading)
            .fixedSize(horizontal: false, vertical: true)
            .background(YYColor.card)
        #else
        YYPage(background: YYColor.card) { content }
        #endif
    }
}
