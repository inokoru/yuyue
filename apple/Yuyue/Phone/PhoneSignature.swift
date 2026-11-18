import SwiftUI

/// The foot of 关于: the pencil signature from the corner of a drawing — a curve running into the
/// long stroke under it, then two T's. It writes itself out when the page opens; touched, again.
struct PencilSignature: View {
    /// Its three strokes, in the order they were written, on a 60 × 56 sheet.
    private static let strokes: [Path] = [
        path { p in
            p.move(to: pt(24, 9))
            p.addCurve(to: pt(11.5, 33), control1: pt(19, 15), control2: pt(13.5, 25))
            p.addCurve(to: pt(20, 41.5), control1: pt(10, 39), control2: pt(13, 41.5))
            p.addCurve(to: pt(44, 39.5), control1: pt(28, 41.5), control2: pt(36, 40.5))
            p.addCurve(to: pt(64, 37), control1: pt(51, 38.5), control2: pt(58, 37.5))
        },
        path { p in
            p.move(to: pt(21.5, 17))
            p.addCurve(to: pt(35, 18.2), control1: pt(27, 18.5), control2: pt(31.5, 18.8))
            p.addCurve(to: pt(26.3, 33), control1: pt(31, 21), control2: pt(27.5, 26))
            p.addCurve(to: pt(25.2, 53), control1: pt(25.3, 40), control2: pt(24.6, 47))
        },
        path { p in
            p.move(to: pt(43, 17.5))
            p.addCurve(to: pt(56.5, 15.5), control1: pt(48, 18.8), control2: pt(52.5, 17.8))
            p.addCurve(to: pt(43.3, 31), control1: pt(51, 19), control2: pt(45.5, 24))
            p.addCurve(to: pt(41, 59.5), control1: pt(41.5, 39), control2: pt(40.3, 49))
        },
    ]
    /// Where the pencil pressed hardest: the right half of the long stroke.
    private static let pressed = path { p in
        p.move(to: pt(40, 40.2))
        p.addCurve(to: pt(63.5, 37.2), control1: pt(48, 39), control2: pt(56, 37.8))
    }

    private static func pt(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x - 8, y: y - 6) }
    private static func path(_ build: (inout Path) -> Void) -> Path { var p = Path(); build(&p); return p }

    static let lead = Color.yyDynamic(light: YYRGBA(47, 46, 45), dark: YYRGBA(217, 213, 205))
    /// Paper grain the graphite didn't reach.
    private static let grain: CGImage? = {
        let n = 96
        var px = [UInt8](repeating: 0, count: n * n)
        var g = SystemRandomNumberGenerator()
        for i in px.indices {
            let r = Double(UInt8.random(in: 0...255, using: &g)) / 255
            px[i] = UInt8(max(0, min(1, 1.3 - 0.9 * r)) * 255)
        }
        guard let provider = CGDataProvider(data: Data(px) as CFData) else { return nil }
        return CGImage(width: n, height: n, bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: n,
                       space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }()

    var size: CGFloat = 84
    @State private var drawn: [CGFloat] = [0, 0, 0]
    @State private var writing: Task<Void, Never>?

    var body: some View {
        let k = size / 60
        ZStack(alignment: .topLeading) {
            ForEach(0..<3, id: \.self) { i in
                let s = Self.strokes[i].applying(CGAffineTransform(scaleX: k, y: k))
                s.trim(from: 0, to: drawn[i])
                    .stroke(Self.lead.opacity(0.9), style: StrokeStyle(lineWidth: 1.9 * k, lineCap: .round, lineJoin: .round))
                s.trim(from: 0, to: drawn[i])
                    .stroke(Self.lead.opacity(0.6), style: StrokeStyle(lineWidth: 1 * k, lineCap: .round, lineJoin: .round))
                    .offset(x: 0.28 * k, y: 0.22 * k)
                if i == 0 {
                    Self.pressed.applying(CGAffineTransform(scaleX: k, y: k))
                        .trim(from: 0, to: max(0, drawn[0] * 2.1 - 1.1))
                        .stroke(Self.lead.opacity(0.5), style: StrokeStyle(lineWidth: 2.6 * k, lineCap: .round))
                }
            }
        }
        .frame(width: size, height: size * 56 / 60, alignment: .topLeading)
        .blur(radius: 0.32 * k)
        .mask {
            if let grain = Self.grain {
                Image(decorative: grain, scale: 3).resizable(resizingMode: .tile).luminanceToAlpha()
            } else {
                Color.black
            }
        }
        .contentShape(.rect)
        .onTapGesture { write() }
        .onAppear { write() }
        .onDisappear { writing?.cancel() }
        .accessibilityLabel(Text(verbatim: "CTT"))
    }

    /// One stroke after another, each as long as it is.
    private func write() {
        writing?.cancel()
        var none = Transaction()
        none.disablesAnimations = true
        if YYMotion.reduce {
            withTransaction(none) { drawn = [1, 1, 1] }
            return
        }
        withTransaction(none) { drawn = [0, 0, 0] }
        writing = Task {
            try? await Task.sleep(for: .milliseconds(400))
            let durations: [Double] = [0.62, 0.5, 0.56]
            for i in 0..<3 {
                guard !Task.isCancelled else { return }
                let d = durations[i]
                withAnimation(.timingCurve(0.45, 0.05, 0.3, 1, duration: d)) { drawn[i] = 1 }
                if i < 2 {
                    try? await Task.sleep(for: .seconds(d * 0.82))
                }
            }
        }
    }
}
