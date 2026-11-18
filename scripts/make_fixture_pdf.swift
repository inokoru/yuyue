// 生成图册用的中文 PDF 样张:swift scripts/make_fixture_pdf.swift <输出.pdf> [文本源.txt]
// 用背影全文重复三遍排成六页上下,给 window-catalog.sh 的 PDF 组当书。
import AppKit
import CoreText

let args = CommandLine.arguments
let out = args.count > 1 ? args[1] : "sample.pdf"
let src = args.count > 2 ? args[2] : "fixtures/books/beiying.txt"
let body = (try? String(contentsOfFile: src, encoding: .utf8)).flatMap { $0.isEmpty ? nil : $0 } ?? "背影"
let paras = body.split(whereSeparator: \.isNewline).filter { !$0.isEmpty }

let title = NSAttributedString(string: "背影(样张)\n\n", attributes: [
    NSAttributedString.Key(rawValue: kCTFontAttributeName as String): CTFontCreateWithName("PingFang SC" as CFString, 28, nil),
])
let style = NSMutableParagraphStyle()
style.lineSpacing = 6
style.paragraphSpacing = 10
let text = NSAttributedString(
    string: Array(repeating: paras.joined(separator: "\n"), count: 3).joined(separator: "\n\n\n"),
    attributes: [
        NSAttributedString.Key(rawValue: kCTFontAttributeName as String): CTFontCreateWithName("PingFang SC" as CFString, 13, nil),
        NSAttributedString.Key(rawValue: kCTParagraphStyleAttributeName as String): style,
    ])
let doc = NSMutableAttributedString(attributedString: title)
doc.append(text)

let url = URL(fileURLWithPath: out) as CFURL
var box = CGRect(x: 0, y: 0, width: 595, height: 842)
guard let ctx = CGContext(url, mediaBox: &box, nil) else { fatalError("开不了 PDF:\(out)") }
let fs = CTFramesetterCreateWithAttributedString(doc)
let page = CGPath(rect: CGRect(x: 56, y: 56, width: 483, height: 730), transform: nil)
var pos = 0
var pages = 0
while pos < doc.length, pages < 40 {
    ctx.beginPDFPage(nil)
    let frame = CTFramesetterCreateFrame(fs, CFRange(location: pos, length: 0), page, nil)
    CTFrameDraw(frame, ctx)
    let vis = CTFrameGetVisibleStringRange(frame)
    guard vis.length > 0 else { break }
    pos = vis.location + vis.length
    ctx.endPDFPage()
    pages += 1
}
ctx.closePDF()
print("样张 PDF:\(out),\(pages) 页")
