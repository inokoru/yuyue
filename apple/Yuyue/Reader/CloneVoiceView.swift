import AVFoundation
import SwiftUI
import UniformTypeIdentifiers
import YuyueCore

/// §6.1 & §7.6-3：克隆一个声音。
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
    var onSaved: ((VoiceCard) -> Void)? = nil
    var onClose: (() -> Void)? = nil

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
    }

    init(
        core: YuyueCore,
        dataDir: URL,
        language: String,
        existingID: String? = nil,
        initialName: String = "",
        previewSentence: String? = nil,
        onSaved: ((VoiceCard) -> Void)? = nil,
        onClose: (() -> Void)? = nil
    ) {
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
        YYPage {
            VStack(alignment: .leading, spacing: YYUI.s(12)) {
                consentCard
                    .padding(.top, YYUI.s(8))
                promptCard
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
                YYNote(String(localized: "录音留在这台设备上，不会上传到任何服务器。"))
            }
            // 录 → 检查 → 不行 / 能用: each step takes the last one's place, not in a cut.
            .animation(YYMotion.standard, value: decodedPCM == nil)
            .animation(YYMotion.standard, value: checking)
            .animation(YYMotion.standard, value: checkResult?.ok)
        }
        .navigationTitle(existingID != nil ? String(localized: "重新录一段") : String(localized: "克隆一个声音"))
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            if pathBinding == nil, let onClose {
                ToolbarItem(placement: .cancellationAction) {
                    Button(String(localized: "取消"), action: onClose)
                }
            }
        }
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
        .confirmationDialog(
            String(localized: "用在哪"),
            isPresented: Binding(get: { savedCard != nil && castContext != nil }, set: { if !$0 { finishCastFlow() } }),
            titleVisibility: .visible
        ) {
            if let forName, let card = savedCard, let ctx = castContext {
                Button(String(localized: "给\(forName)")) {
                    var v = ctx.role(forName) ?? RoleVoice(voice: nil, rate: 1, pitch: 0, right: false)
                    v.voice = card.id
                    ctx.set(forName, v)
                    finishCastFlow()
                }
            }
            if let card = savedCard, let ctx = castContext {
                Button(String(localized: "全书（旁白）")) {
                    let voice = ClonedVoices.resolveVoice(card.id, bookID: ctx.bookID) ?? CardVoice(card: card, bookID: ctx.bookID)
                    ctx.player?.choose(voice, for: ctx.language)
                    finishCastFlow()
                }
            }
            Button(String(localized: "先放着"), role: .cancel) { finishCastFlow() }
        } message: {
            Text("已经存在这台设备上。")
        }
        .yyNotice(
            String(localized: "没存下"),
            message: failure ?? "",
            isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })
        )
        .onAppear {
            if !initialName.isEmpty, name.isEmpty {
                name = initialName
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

    private var consentCard: some View {
        YYGroup {
            Button { consent.toggle() } label: {
                HStack(spacing: YYUI.s(10)) {
                    Image(systemName: consent ? "checkmark.circle.fill" : "circle")
                        .font(YYUI.font(17))
                        .foregroundStyle(consent ? YYTint.standard.fill : YYColor.t3)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("这是我的声音，或者我得到了本人同意")
                            .font(YYUI.font(11.5, .semibold))
                            .foregroundStyle(YYColor.t1)
                        Text("先勾上这一项才能开始录音或导入音频")
                            .font(YYUI.font(10))
                            .foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 0)
                }
                .padding(.vertical, YYUI.s(9))
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
        }
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
                YYLabel(String(localized: "照着平常读书的样子念这一句"))
                Text(promptScript)
                    .font(YYUI.serif(15))
                    .lineSpacing(8)
                    .foregroundStyle(YYColor.t1)
                    .padding(.horizontal, YYUI.s(16))
                    .padding(.vertical, YYUI.s(14))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .yyCard()
            }
        }
    }

    private var recordingControls: some View {
        VStack(alignment: .leading, spacing: YYUI.s(10)) {
            Button {
                recorder.recording ? stopRecording() : startRecording()
            } label: {
                HStack(spacing: YYUI.s(10)) {
                    Circle()
                        .fill(consent ? YYColor.danger : YYColor.t3.opacity(0.4))
                        .frame(width: YYUI.s(10), height: YYUI.s(10))
                        .overlay(
                            Circle()
                                .stroke(YYColor.danger.opacity(0.18), lineWidth: recorder.recording ? 4 : 0)
                                .padding(-4)
                        )
                    Text(Self.time(recorder.seconds))
                        .font(YYUI.font(17, .bold))
                        .monospacedDigit()
                        .foregroundStyle(consent ? YYColor.t1 : YYColor.t3)
                    HStack(spacing: 2) {
                        ForEach(Array(recorder.levels.suffix(28).enumerated()), id: \.offset) { _, l in
                            Capsule()
                                .fill(YYColor.t2)
                                .frame(width: 2, height: max(3, CGFloat(l) * YYUI.s(24)))
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: YYUI.s(24), alignment: .leading)
                    HStack(spacing: YYUI.s(5)) {
                        if recorder.recording {
                            RoundedRectangle(cornerRadius: 2.5)
                                .fill(YYColor.danger)
                                .frame(width: YYUI.s(10), height: YYUI.s(10))
                            Text("停下")
                        } else {
                            Text("点一下开始念")
                        }
                    }
                    .font(YYUI.font(11.5, .semibold))
                    .foregroundStyle(consent ? YYColor.t1 : YYColor.t3)
                }
                .padding(.horizontal, YYUI.s(16))
                .frame(height: YYUI.s(58))
                .yyCard()
                .opacity(consent ? 1 : 0.55)
                // 停下 ⇄ 点一下开始念 and the ring round the dot; the level bars stay live.
                .animation(YYMotion.quick, value: recorder.recording)
                .animation(YYMotion.standard, value: consent)
                .contentShape(.rect)
            }
            .buttonStyle(YYPressStyle())
            .disabled(!consent)

            Text(recorder.recording ? "念完停顿一下就会自动结束，也可以点一下停下。" : "大约 8 秒就够；请在安静屋子里自然念出上面这句话。")
                .font(YYUI.font(10.5))
                .foregroundStyle(YYColor.t3)
                .padding(.horizontal, YYUI.s(2))

            HStack(spacing: YYUI.s(10)) {
                actionChip(String(localized: "导入一段录音…"), icon: "square.and.arrow.down", wide: true) {
                    importing = true
                }
                .disabled(!consent)
                .opacity(consent ? 1 : 0.55)
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
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(YYUI.font(13))
                    .foregroundStyle(YYColor.danger)
                    .padding(.top, 1)
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(check.messages.isEmpty ? [String(localized: "这段录音不太清楚，再录一次试试")] : check.messages, id: \.self) { msg in
                        Text(msg)
                            .font(YYUI.font(12, .semibold))
                            .foregroundStyle(YYColor.t1)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(YYUI.s(14))
            .background(YYColor.danger.opacity(0.08), in: .rect(cornerRadius: YYUI.s(12)))
            .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.danger.opacity(0.24), lineWidth: 0.5))

            HStack(spacing: YYUI.s(10)) {
                if imported {
                    actionChip(String(localized: "改好文字再检一次"), icon: "checkmark.circle") {
                        recheckImportedTranscript()
                    }
                }
                actionChip(String(localized: "再录一次"), icon: "arrow.uturn.backward", wide: true, action: redo)
            }
        }
    }

    private var passReviewSection: some View {
        VStack(alignment: .leading, spacing: YYUI.s(12)) {
            VStack(alignment: .leading, spacing: YYUI.s(8)) {
                HStack {
                    Label("录好了 · 试听这本书的下一句", systemImage: "checkmark.circle.fill")
                        .font(YYUI.font(11.5, .semibold))
                        .foregroundStyle(YYColor.ok)
                    Spacer()
                    Button(action: playSamplePreview) {
                        HStack(spacing: 5) {
                            if previewingSample {
                                ProgressView().controlSize(.mini)
                            } else {
                                Image(systemName: "play.fill").font(YYUI.font(9))
                            }
                            Text(previewingSample ? "正在合成…" : "再听一遍")
                        }
                        .font(YYUI.font(11, .semibold))
                        .foregroundStyle(YYColor.t1)
                        .padding(.horizontal, YYUI.s(10))
                        .frame(height: YYUI.s(26))
                        .background(YYColor.inset, in: .capsule)
                    }
                    .buttonStyle(.plain)
                    .disabled(previewingSample)
                }
                Text(nextLineToPreview)
                    .font(YYUI.serif(13.5))
                    .lineSpacing(6)
                    .foregroundStyle(YYColor.t2)
                    .lineLimit(3)
            }
            .padding(YYUI.s(14))
            .yyCard()

            YYGroup {
                YYField(label: String(localized: "名字")) {
                    TextField(String(localized: "我的声音"), text: $name)
                }
            }

            HStack(spacing: YYUI.s(10)) {
                actionChip(String(localized: "再录一次"), icon: "arrow.uturn.backward", action: redo)
                Button(action: saveCard) {
                    HStack(spacing: YYUI.s(6)) {
                        if saving {
                            ProgressView().controlSize(.mini)
                        }
                        Text("用这个声音")
                            .font(YYUI.font(12.5, .semibold))
                    }
                    .foregroundStyle(YYTint.standard.onBlack)
                    .frame(maxWidth: .infinity)
                    .frame(height: YYUI.s(38))
                    .background(YYTint.standard.black, in: .rect(cornerRadius: YYUI.s(11)))
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .disabled(saving || name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
    }

    private func actionChip(_ title: String, icon: String, wide: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: YYUI.s(7)) {
                Image(systemName: icon).font(YYUI.font(12)).foregroundStyle(YYColor.t2)
                Text(title).font(YYUI.font(12, .semibold)).foregroundStyle(YYColor.t1)
            }
            .padding(.horizontal, YYUI.s(14))
            .frame(maxWidth: wide ? .infinity : nil)
            .frame(height: YYUI.s(38))
            .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(11)))
            .overlay(RoundedRectangle(cornerRadius: YYUI.s(11)).strokeBorder(YYColor.line, lineWidth: 0.5))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
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
                ClonePreviewPlayer.shared.play(speech)
            } else if let fallback = SystemVoice.best(for: lang),
                      let speech = try? await fallback.render(line, language: lang) {
                ClonePreviewPlayer.shared.play(speech)
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

// MARK: - §6.2 & §7.6-3 克隆声音详情（补录六档语气、同步、ElevenLabs 备份、重命名、删除）

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
        YYPage {
            if let entry {
                headerCard(entry)
                    .padding(.top, YYUI.s(10))

                if let card = entry.card {
                    YYLabel(String(localized: "加上语气"), String(localized: "广播剧演到激烈处更自然"))
                    YYGroup {
                        ForEach(Self.extraStyles, id: \.style) { item in
                            styleRow(card: card, style: item.style, label: String(localized: String.LocalizationValue(item.title)))
                        }
                    }

                    YYLabel(String(localized: "设置"))
                    YYGroup {
                        syncRow(card: card)
                        if CloneEngine.available {
                            elevenLabsRow(entry: entry)
                        }
                    }
                    if CloneEngine.available {
                        YYNote(String(localized: "“也用 ElevenLabs 念”默认关闭；打开时才会把参考录音传到你自己的 ElevenLabs 账号。"))
                    }
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

                YYGroup {
                    Button {
                        newName = entry.name
                        renaming = true
                    } label: {
                        HStack {
                            Label("重命名", systemImage: "pencil")
                                .font(YYUI.font(12))
                                .foregroundStyle(YYColor.t1)
                            Spacer()
                        }
                        .padding(.vertical, YYUI.s(10))
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)

                    Button(role: .destructive) {
                        confirmingDelete = YYConfirm(
                            title: String(localized: "删除“\(entry.name)”？"),
                            message: String(localized: "这台设备上的参考录音会一并删掉。"),
                            button: String(localized: "删除")
                        ) {
                            ClonedVoices.deleteCard(id: entry.id, core: core)
                            onDeleted?()
                            dismiss()
                        }
                    } label: {
                        HStack {
                            Label("删除这个声音", systemImage: "trash")
                                .font(YYUI.font(12))
                                .foregroundStyle(YYColor.danger)
                            Spacer()
                        }
                        .padding(.vertical, YYUI.s(10))
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.top, YYUI.s(10))
            }
        }
        .navigationTitle(entry?.name ?? String(localized: "克隆声音"))
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
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
        entry = ClonedVoices.entries().first { $0.id == entryID }
    }

    private func headerCard(_ entry: ClonedEntry) -> some View {
        HStack(spacing: YYUI.s(12)) {
            if let resolved = ClonedVoices.resolveVoice(entry.id) {
                VoiceAvatar(voice: resolved, size: YYUI.s(38))
            }
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(entry.name).font(YYUI.font(14, .semibold)).foregroundStyle(YYColor.t1)
                    Text("克隆")
                        .font(YYUI.font(9.5, .semibold))
                        .foregroundStyle(YYColor.t2)
                        .padding(.horizontal, 6).padding(.vertical, 2)
                        .background(YYColor.inset, in: .capsule)
                }
                Text(entry.subtitle)
                    .font(YYUI.font(10.5))
                    .foregroundStyle(YYColor.t3)
            }
            Spacer()
            if let voice = ClonedVoices.resolveVoice(entry.id), entry.isPlayable {
                Button {
                    Voices.preview(voice, text: Voices.sampleText(language))
                } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "play.fill").font(YYUI.font(8.5))
                        Text("试听")
                    }
                    .font(YYUI.font(11, .semibold))
                    .foregroundStyle(YYColor.t1)
                    .padding(.horizontal, YYUI.s(10))
                    .frame(height: YYUI.s(28))
                    .background(YYColor.inset, in: .capsule)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(YYUI.s(14))
        .yyCard()
    }

    private func styleRow(card: VoiceCard, style: CardStyle, label: String) -> some View {
        let clip = card.clips.first { $0.style == style }
        return HStack(spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: 2) {
                Text(label).font(YYUI.font(12, .medium)).foregroundStyle(YYColor.t1)
                Text(clip.map { String(format: String(localized: "%.1f 秒"), $0.seconds) } ?? String(localized: "未录 · 先用平常语气代替"))
                    .font(YYUI.font(10))
                    .foregroundStyle(YYColor.t3)
            }
            Spacer()
            if clip != nil {
                Button {
                    Task {
                        let cv = CardVoice(card: card, bookID: nil)
                        if let sp = try? await cv.render(core.cardScript(lang: card.lang, style: style), language: card.lang, style: style) {
                            ClonePreviewPlayer.shared.play(sp)
                        }
                    }
                } label: {
                    Image(systemName: "play.fill")
                        .font(YYUI.font(9))
                        .foregroundStyle(YYColor.t1)
                        .frame(width: YYUI.s(26), height: YYUI.s(26))
                        .background(YYColor.inset, in: .circle)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("试听\(label)"))
            }
            Button {
                recordingStyle = style
            } label: {
                Text(clip != nil ? "重录" : "录一句")
                    .font(YYUI.font(11, .semibold))
                    .foregroundStyle(YYColor.t1)
                    .padding(.horizontal, YYUI.s(10))
                    .frame(height: YYUI.s(26))
                    .background(YYColor.inset, in: .capsule)
            }
            .buttonStyle(.plain)
        }
        .padding(.vertical, YYUI.s(8))
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
                Text("随你的同步盘传到自己的其他设备").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
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
                Text("走你自己的 ElevenLabs 账号合成").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
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
        .navigationTitle(String(localized: "录一句语气"))
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button(String(localized: "取消")) {
                    recorder.stop()
                    onDone()
                }
            }
        }
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

/// 麦克风录音器：0.1 秒电平采样，检测到开口说话后若连续静音满 1.2 秒自动停下（§6.1）。
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
