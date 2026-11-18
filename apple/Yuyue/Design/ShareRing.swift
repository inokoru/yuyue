import SwiftUI

/// 边下边听's download circle, as Podcasts' download button: an arrow before; a thin ink ring
/// filling while it comes, a small square in it to pause; nothing once it's all here.
struct ShareRing: View {
    enum State: Equatable {
        case get
        case coming(Double)
        case paused(Double)
    }

    let state: State
    var size: CGFloat = 18
    var action: () -> Void = {}

    var body: some View {
        Button(action: action) {
            ZStack {
                Circle().stroke(YYColor.line, lineWidth: 1.6)
                switch state {
                case .get:
                    Image(systemName: "arrow.down").font(.system(size: size * 0.45, weight: .semibold)).foregroundStyle(YYColor.t2)
                case .coming(let f):
                    Circle().trim(from: 0, to: max(0.03, f)).stroke(YYColor.t1, style: StrokeStyle(lineWidth: 1.6, lineCap: .round)).rotationEffect(.degrees(-90))
                    RoundedRectangle(cornerRadius: 1).fill(YYColor.t1).frame(width: size * 0.27, height: size * 0.27)
                case .paused(let f):
                    Circle().trim(from: 0, to: max(0.03, f)).stroke(YYColor.t3, style: StrokeStyle(lineWidth: 1.6, lineCap: .round)).rotationEffect(.degrees(-90))
                    Image(systemName: "arrow.down").font(.system(size: size * 0.45, weight: .semibold)).foregroundStyle(YYColor.t2)
                }
            }
            .frame(width: size, height: size)
            .padding(6)
            .contentShape(.rect)
            .animation(.linear(duration: 0.25), value: state)
        }
        .buttonStyle(.plain)
    }
}
