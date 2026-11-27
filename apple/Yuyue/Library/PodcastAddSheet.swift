import SwiftUI
import UniformTypeIdentifiers
import YuyueCore

/// 「添加节目」弹窗 — 与播客首页完全一致的文房纸墨语言（暖纸 `YYColor.bg`、宋体标题、瓷白圆 `×`、
/// 首页同款内嵌哑灰搜索栏、宣白 `PodcastStationeryCard` 结果/引导卡、`PodcastPaperPill` 瓷白按钮）。
struct PodcastAddSheet: View {
    let library: Library
    var initialQuery: String = ""
    let onClose: () -> Void

    @State private var query = ""
    @State private var hits: [PodcastSearchHit] = []
    @State private var searching = false
    @State private var subscribedURLs: Set<String> = []
    @State private var importingOPML = false
    @State private var importingAudio = false
    @State private var statusLine: String?
    @FocusState private var focused: Bool

    private var hub: PodcastHub { library.podcastHub }
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var looksLikeURL: Bool {
        let t = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return t.hasPrefix("http://") || t.hasPrefix("https://") || t.contains(".xml") || t.contains("/feed")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Songti Header + aligned porcelain × button
            HStack(alignment: .center, spacing: 10) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("添加节目")
                        .font(PodcastStyle.songti(18))
                        .foregroundStyle(YYColor.t1)
                    Text("搜播客目录、贴单集或导入本地音频")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(YYColor.t3)
                        .lineLimit(1)
                }
                Spacer(minLength: 8)
                PodcastCircleIconButton(
                    glyph: .xmark,
                    size: 28,
                    iconSize: 11,
                    accessibilityLabel: String(localized: "关闭"),
                    action: onClose
                )
                .focusable(false)
                .keyboardShortcut(.cancelAction)
            }
            .padding(.horizontal, 20)
            .padding(.top, 16)
            .padding(.bottom, 12)

            // Recessed matte command search bar (Homepage twin)
            HStack(spacing: 8) {
                Image(systemName: looksLikeURL ? "link" : "magnifyingglass")
                    .font(.system(size: 12.5, weight: .medium))
                    .foregroundStyle(YYColor.t3)

                TextField(
                    "",
                    text: $query,
                    prompt: Text(String(localized: "搜节目名，或贴 RSS / 单集网页 / 音频链接…"))
                        .foregroundStyle(YYColor.t3.opacity(0.8))
                )
                .textFieldStyle(.plain)
                .font(.yy(size: 13))
                .foregroundStyle(YYColor.t1)
                .focused($focused)
                .onSubmit { submitQuery() }

                if !query.isEmpty {
                    Button { query = ""; hits = [] } label: {
                        PodcastGlyphView(glyph: .xmark, size: 9, lineWidth: 1.7)
                            .foregroundStyle(YYColor.t3)
                            .frame(width: 18, height: 18)
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }

                if looksLikeURL {
                    Button(action: submitQuery) {
                        Text(hub.isAddingFeed ? String(localized: "解析中…") : String(localized: "放入收听台"))
                            .font(.yy(size: 11.5, weight: .medium))
                            .foregroundStyle(YYColor.card)
                            .padding(.horizontal, 11)
                            .frame(height: 24)
                            .background(YYColor.t1, in: Capsule())
                    }
                    .buttonStyle(YYPressStyle(scale: 0.96))
                    .disabled(hub.isAddingFeed)
                } else {
                    Text("⌘V")
                        .font(.yy(size: 10, weight: .medium))
                        .foregroundStyle(YYColor.t3)
                        .padding(.horizontal, 5)
                        .frame(height: 18)
                        .background(YYColor.card, in: RoundedRectangle(cornerRadius: 4.5, style: .continuous))
                        .overlay(
                            RoundedRectangle(cornerRadius: 4.5, style: .continuous)
                                .strokeBorder(PodcastStyle.hairline(scheme), lineWidth: 0.5)
                        )
                }
            }
            .padding(.horizontal, 12)
            .frame(height: 36)
            .background(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(scheme == .dark ? Color.white.opacity(0.06) : Color(yy: YYRGBA(236, 234, 227)))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .strokeBorder(focused ? PodcastStyle.accentRing(tint, scheme) : Color.clear, lineWidth: 0.75)
            )
            .padding(.horizontal, 20)

            if let err = hub.addError {
                Text(err)
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.danger)
                    .padding(.horizontal, 22)
                    .padding(.top, 8)
            } else if let statusLine {
                Text(statusLine)
                    .font(.yy(size: 11.5))
                    .foregroundStyle(tint.accent)
                    .padding(.horizontal, 22)
                    .padding(.top, 8)
            }

            // Results or Guide inside pure-white `PodcastStationeryCard`
            PodcastStationeryCard(cornerRadius: 13) {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        if searching {
                            HStack(spacing: 8) {
                                ProgressView().controlSize(.small)
                                Text("正在搜索播客目录…")
                                    .font(.yy(size: 12.5))
                                    .foregroundStyle(YYColor.t3)
                            }
                            .frame(maxWidth: .infinity, minHeight: 200, alignment: .center)
                        } else if hits.isEmpty {
                            emptyHint
                        } else {
                            HStack {
                                Text("搜索结果")
                                    .font(PodcastStyle.songti(13.5))
                                    .foregroundStyle(YYColor.t1)
                                Spacer()
                                Text("\(hits.count) 档节目")
                                    .font(.yy(size: 11))
                                    .foregroundStyle(YYColor.t3)
                            }
                            .padding(.horizontal, 14)
                            .padding(.top, 11)
                            .padding(.bottom, 6)

                            ForEach(Array(hits.enumerated()), id: \.element.id) { idx, hit in
                                if idx > 0 {
                                    Rectangle()
                                        .fill(PodcastStyle.softDivider(scheme))
                                        .frame(height: 0.5)
                                        .padding(.horizontal, 14)
                                }
                                resultRow(hit)
                            }
                        }
                    }
                }
                .scrollIndicators(.hidden)
            }
            .padding(.horizontal, 20)
            .padding(.top, 12)
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            // Quiet Homepage-matched footer pills
            HStack(spacing: 8) {
                PodcastPaperPill(title: String(localized: "导入 OPML"), height: 28, horizontalPadding: 11) {
                    importingOPML = true
                }
                PodcastPaperPill(title: String(localized: "本地音频"), height: 28, horizontalPadding: 11) {
                    importingAudio = true
                }
                Spacer()
                Text("粘贴链接回车即可收单集")
                    .font(.yy(size: 11))
                    .foregroundStyle(YYColor.t3)
            }
            .padding(.horizontal, 20)
            .padding(.top, 12)
            .padding(.bottom, 16)
        }
        #if os(macOS)
        .frame(width: 500, height: 420)
        #endif
        .background(YYColor.bg)
        .onAppear {
            subscribedURLs = Set(hub.channels.map(\.feedUrl))
            if !initialQuery.isEmpty {
                query = initialQuery
                submitQuery()
            }
            focused = true
        }
        .fileImporter(isPresented: $importingOPML, allowedContentTypes: [.xml, UTType(filenameExtension: "opml") ?? .xml]) { res in
            if case .success(let url) = res {
                Task {
                    let count = await hub.importOPML(from: url)
                    statusLine = String(localized: "已导入 \(count) 档播客到报刊栏")
                }
            }
        }
        .fileImporter(isPresented: $importingAudio, allowedContentTypes: [.audio]) { res in
            if case .success(let url) = res {
                Task {
                    if let issue = await hub.importLocalAudio(from: url) {
                        statusLine = String(localized: "已将《\(issue.title)》放上书桌")
                    }
                }
            }
        }
    }

    private var emptyHint: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("三种添加方式")
                .font(PodcastStyle.songti(14))
                .foregroundStyle(YYColor.t1)

            VStack(alignment: .leading, spacing: 10) {
                hintRow(glyph: .allGrid, lead: String(localized: "搜节目名"), detail: String(localized: "直连公开播客目录找到 RSS，回车即可搜索并一键订阅。"))
                hintRow(glyph: .link, lead: String(localized: "贴单集链接"), detail: String(localized: "支持小宇宙、Apple Podcasts、网页或音频直链，不强迫订阅整档节目。"))
                hintRow(glyph: .waves, lead: String(localized: "导入 OPML / 音频"), detail: String(localized: "一键迁入现有订阅或本地 .mp3 / .m4a 讲座录音，零未读红点焦虑。"))
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func hintRow(glyph: PodcastGlyph, lead: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            PodcastGlyphView(glyph: glyph, size: 13.5, lineWidth: 1.45)
                .foregroundStyle(tint.accent)
                .frame(width: 24, height: 24)
                .background(PodcastStyle.rowTintWash(tint, scheme), in: RoundedRectangle(cornerRadius: 7, style: .continuous))
            VStack(alignment: .leading, spacing: 2) {
                Text(lead)
                    .font(.yy(size: 12.5, weight: .medium))
                    .foregroundStyle(YYColor.t1)
                Text(detail)
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.t3)
                    .lineSpacing(3)
            }
        }
    }

    private func resultRow(_ hit: PodcastSearchHit) -> some View {
        let added = subscribedURLs.contains(hit.feedURL)
        return HStack(spacing: 11) {
            PodcastShowMark(title: hit.title, artworkURL: hit.artworkURL, size: 34)

            VStack(alignment: .leading, spacing: 2) {
                Text(hit.title)
                    .font(.yy(size: 13, weight: .medium))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                Text([hit.author, hit.genre].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            if added {
                Text("已订阅")
                    .font(.yy(size: 11, weight: .medium))
                    .foregroundStyle(YYColor.t3)
            } else {
                PodcastPaperPill(title: String(localized: "订阅"), accent: true, height: 25, horizontalPadding: 11) {
                    Task {
                        if await hub.subscribeRSS(urlString: hit.feedURL, putLatestOnDesk: true) {
                            subscribedURLs.insert(hit.feedURL)
                            statusLine = String(localized: "已订阅《\(hit.title)》，已拉取最近节目")
                        }
                    }
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
    }

    private func submitQuery() {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return }
        if looksLikeURL {
            Task {
                if await hub.subscribeRSS(urlString: q, putLatestOnDesk: true) {
                    statusLine = String(localized: "已放入收听台")
                    onClose()
                }
            }
        } else {
            searching = true
            Task {
                hits = await PodcastHub.searchITunes(query: q)
                searching = false
            }
        }
    }
}
