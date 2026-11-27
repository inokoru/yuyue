import CoreGraphics
import Foundation

let args = Array(CommandLine.arguments.dropFirst())
guard let cmd = args.first else {
    fputs("usage: mac-input move|click|right|drag|hold|scroll|type|key|windows\n", stderr)
    exit(2)
}

func num(_ s: String) -> CGFloat { CGFloat(Double(s) ?? 0) }

func post(_ type: CGEventType, _ p: CGPoint, _ button: CGMouseButton = .left) {
    guard let e = CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: p, mouseButton: button) else { return }
    e.post(tap: .cghidEventTap)
}

func at(_ i: Int) -> CGPoint { CGPoint(x: num(args[i]), y: num(args[i + 1])) }

switch cmd {
case "move":
    post(.mouseMoved, at(1))
case "click":
    let p = at(1)
    post(.mouseMoved, p)
    usleep(40_000)
    post(.leftMouseDown, p)
    usleep(50_000)
    post(.leftMouseUp, p)
case "right":
    let p = at(1)
    post(.mouseMoved, p)
    usleep(40_000)
    post(.rightMouseDown, p, .right)
    usleep(50_000)
    post(.rightMouseUp, p, .right)
case "hold":
    let p = at(1)
    let ms = UInt32(args.count > 3 ? (Double(args[3]) ?? 0.6) * 1_000_000 : 600_000)
    post(.mouseMoved, p)
    usleep(30_000)
    post(.leftMouseDown, p)
    usleep(ms)
    post(.leftMouseUp, p)
case "drag":
    let a = at(1)
    let b = at(3)
    post(.mouseMoved, a)
    usleep(30_000)
    post(.leftMouseDown, a)
    for step in 1...12 {
        let t = CGFloat(step) / 12
        let p = CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t)
        post(.leftMouseDragged, p)
        usleep(16_000)
    }
    post(.leftMouseUp, b)
case "scroll":
    let p = at(1)
    let dy = Int32(args.count > 3 ? (Int(args[3]) ?? -4) : -4)
    post(.mouseMoved, p)
    if let e = CGEvent(scrollWheelEvent2Source: nil, units: .line, wheelCount: 1, wheel1: dy, wheel2: 0, wheel3: 0) {
        e.location = p
        e.post(tap: .cghidEventTap)
    }
case "type":
    let text = args.dropFirst().joined(separator: " ")
    let units = Array(text.utf16)
    if let down = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: true),
       let up = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: false) {
        down.keyboardSetUnicodeString(stringLength: units.count, unicodeString: units)
        up.keyboardSetUnicodeString(stringLength: units.count, unicodeString: units)
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
    }
case "key":
    let name = args.count > 1 ? args[1] : ""
    let code: CGKeyCode
    switch name {
    case "1": code = CGKeyCode(0x12)
    case "return", "enter": code = CGKeyCode(0x24)
    case "space": code = CGKeyCode(0x31)
    case "escape": code = CGKeyCode(0x35)
    default:
        fputs("unknown key \(name)\n", stderr)
        exit(2)
    }
    CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: true)?.post(tap: .cghidEventTap)
    CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: false)?.post(tap: .cghidEventTap)
case "windows":
    let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
    let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] ?? []
    for item in list {
        let layer = item[kCGWindowLayer as String] as? Int ?? 0
        if layer != 0 { continue }
        let owner = item[kCGWindowOwnerName as String] as? String ?? ""
        guard owner.contains("Yuyue") || owner.contains("语阅") || owner.contains("Simulator") else { continue }
        guard let bounds = item[kCGWindowBounds as String] as? [String: Any] else { continue }
        let x = (bounds["X"] as? NSNumber)?.doubleValue ?? 0
        let y = (bounds["Y"] as? NSNumber)?.doubleValue ?? 0
        let w = (bounds["Width"] as? NSNumber)?.doubleValue ?? 0
        let h = (bounds["Height"] as? NSNumber)?.doubleValue ?? 0
        if w < 200 || h < 200 { continue }
        print("\(owner)\t\(Int(x))\t\(Int(y))\t\(Int(w))\t\(Int(h))")
    }
default:
    fputs("unknown command \(cmd)\n", stderr)
    exit(2)
}
