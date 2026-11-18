import SwiftUI
import YuyueCore

/// Shared column metrics for 笔记 and 生词本 in the Mac shelf window, so switching between
/// them keeps the group rail and NoteCard on the exact same vertical line.
enum ShelfNotesLayout {
    static let sideWidth: CGFloat = 92
    static let columnSpacing: CGFloat = 16
    static let sideTopPadding: CGFloat = 14
}

/// The pill label of a header menu (导出), matching YYPillChrome's height, type and hover lift.
struct ShelfMenuPill: View {
    let title: LocalizedStringKey
    let systemImage: String
    @Environment(\.isEnabled) private var enabled
    @Environment(\.colorScheme) private var scheme
    @State private var hover = false

    var body: some View {
        HStack(spacing: 5) {
            Image(systemName: systemImage).font(.yy(size: 11, weight: .semibold))
            Text(title)
        }
        .font(.yy(size: 12))
        .foregroundStyle(YYColor.t1)
        .padding(.horizontal, 13)
        .frame(height: 28)
        .background { YYCapsuleLift(lift: .secondary(scheme: scheme, onPaper: false, hover: hover)) }
        .contentShape(.capsule)
        .opacity(enabled ? 1 : 0.4)
        .animation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.2), value: hover)
        #if os(macOS)
        .onHover { hover = $0 && enabled }
        #endif
    }
}

/// A white card of notes, lines between them.
struct NoteCard<Content: View>: View {
    @ViewBuilder let content: Content
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Group(subviews: content) { rows in
                ForEach(Array(rows.enumerated()), id: \.offset) { i, row in
                    if i > 0 { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                    row
                }
            }
        }
        .padding(.horizontal, 20)
        .background(YYColor.card, in: .rect(cornerRadius: YYRadius.card))
        .yyShadow(scheme == .dark ? YYShadow.ringDark : YYShadow.ringLight)
    }
}

extension MarkStyle {
    /// The solid dot standing for the colour in 笔记's filters.
    var dot: Color {
        switch self {
        case .yellow: YYColor.hlY
        case .blue: YYColor.hlB
        case .pink: Color(red: 0.94, green: 0.62, blue: 0.58)
        default: YYColor.hlR
        }
    }
}

/// Dates and counts as 笔记 writes them.
/// When a note was made, a small headphone before it when it was marked by 双击耳机.
struct NoteStamp: View {
    let mark: Mark
    let text: String
    var size: CGFloat = 11

    var body: some View {
        HStack(spacing: 3) {
            if mark.heard { Image(systemName: "headphones").font(.yy(size: size - 1.5)).accessibilityLabel("听书时划的") }
            Text(text).font(.yy(size: size)).monospacedDigit()
        }
        .foregroundStyle(YYColor.t3)
    }
}

enum Notes {
    static func date(_ ms: Int64) -> Date { Date(timeIntervalSince1970: Double(ms) / 1000) }

    /// A local day as the core keeps time by it: "2026-10-16".
    static func day(_ date: Date) -> String {
        let c = Calendar.current.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    static var today: String { day(Date()) }

    static func dayName(_ day: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(day) { return String(localized: "今天") }
        if cal.isDateInYesterday(day) { return String(localized: "昨天") }
        return day.formatted(.dateTime.weekday(.wide))
    }

    static func shortDay(_ date: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(date) { return String(localized: "今天") }
        if cal.isDateInYesterday(date) { return String(localized: "昨天") }
        return monthDay(date)
    }

    /// 10 月 13 日, spaced like the rest of the text.
    static func monthDay(_ date: Date) -> String {
        let c = Calendar.current.dateComponents([.month, .day], from: date)
        return String(localized: "\(c.month ?? 0) 月 \(c.day ?? 0) 日")
    }

    /// 8:09, as the notes list it under the day.
    static func time(_ ms: Int64) -> String {
        let c = Calendar.current.dateComponents([.hour, .minute], from: date(ms))
        return String(format: "%d:%02d", c.hour ?? 0, c.minute ?? 0)
    }

    static func counts(_ marks: [Mark]) -> String {
        let thoughts = marks.count { $0.note != nil }
        return String(localized: "划线 \(marks.count)") + (thoughts > 0 ? String(localized: " · 想法 \(thoughts)") : "")
    }
}
