import Foundation
import Synchronization
import YuyueCore

/// 本机模型与字体的统一多源自动下载与断点续传。
///
/// 1. 自动选路：中国大陆地区 / 中国时区 / 简体中文环境优先走魔搭（ModelScope）国内 CDN，
///    超时或报错时自动无感回退 GitHub Releases；海外环境优先 GitHub Releases，失败回退魔搭。
/// 2. 断点续传：保留 `.part` 临时文件，重连或切源时自动带 `Range` 头续传；最终按 BLAKE3 强校验。
nonisolated enum ModelDownload {
    static let githubReleaseBase = "https://github.com/inokoru/yuyue/releases/download/models-1/"
    static let modelScopeRepo = "nowtin/yuyue-models"
    static let modelScopeBase = "https://modelscope.cn/models/\(modelScopeRepo)/resolve/master/"

    /// 判断当前设备是否大概率处于中国大陆网络环境。
    static var prefersChinaMirror: Bool {
        if Locale.current.region?.identifier.uppercased() == "CN" { return true }
        let tz = TimeZone.current.identifier
        if tz == "Asia/Shanghai" || tz == "Asia/Chongqing" || tz == "Asia/Harbin" || tz == "Asia/Urumqi" {
            return true
        }
        if let first = Locale.preferredLanguages.first,
           first.hasPrefix("zh-Hans") || first == "zh-CN" {
            return true
        }
        return false
    }

    /// 构造按优先级排列的候选下载地址列表（自动去重）。
    /// - Parameters:
    ///   - asset: `models-1` 里的文件名（如 `live-zipvoice-1.zip`），或字体的文件名。
    ///   - officialURL: 官方源完整地址；未传时默认拼 `githubReleaseBase + asset`。
    ///   - mirrorPath: 在镜像仓里的相对路径；未传时同 `asset`（字体传 `fonts/<file>`）。
    static func candidateURLs(
        for asset: String,
        officialURL: URL? = nil,
        mirrorPath: String? = nil
    ) -> [URL] {
        let rel = (mirrorPath ?? asset).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let official = officialURL ?? URL(string: githubReleaseBase + asset)
        let mirror = URL(string: modelScopeBase + rel)
        let raw = prefersChinaMirror ? [mirror, official] : [official, mirror]

        var result: [URL] = []
        for url in raw.compactMap({ $0 }) where !result.contains(url) {
            result.append(url)
        }
        return result
    }

    /// 支持多源回退、HTTP Range 断点续传与 BLAKE3 校验的文件下载。
    /// 下载中断或暂停时保留 `partFile`，下次重试直接从断点继续；仅在校验不匹配时清理重下。
    @concurrent
    static func downloadWithResume(
        urls: [URL],
        to partFile: URL,
        expectedBytes: Int64? = nil,
        expectedBlake3: String? = nil,
        progress: @escaping @Sendable (Int64, Int64) -> Void
    ) async throws {
        guard !urls.isEmpty else { throw URLError(.badURL) }
        try FileManager.default.createDirectory(
            at: partFile.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )

        // 若本地已有完整且校验通过的 .part，直接复用（避免上次校验前被暂停白下）。
        if let existingSize = fileSize(partFile), existingSize > 0, let expected = expectedBytes, expected > 0 {
            let hasDigest = expectedBlake3?.isEmpty == false
            if existingSize >= expected {
                let valid = hasDigest
                    ? (try verifyDigest(of: partFile, blake3: expectedBlake3))
                    : (existingSize == expected)
                if valid {
                    progress(existingSize, existingSize)
                    return
                } else {
                    try? FileManager.default.removeItem(at: partFile)
                }
            }
        }

        var lastError: Error = URLError(.cannotLoadFromNetwork)
        for url in urls {
            try Task.checkCancellation()
            if url.isFileURL {
                try? FileManager.default.removeItem(at: partFile)
                try FileManager.default.copyItem(at: url, to: partFile)
                let size = fileSize(partFile) ?? expectedBytes ?? 1
                progress(size, size)
                return
            }

            do {
                try await streamOneSource(
                    url: url,
                    to: partFile,
                    expectedBytes: expectedBytes,
                    expectedBlake3: expectedBlake3,
                    progress: progress
                )
                if try verifyDigest(of: partFile, blake3: expectedBlake3) {
                    return
                }
                // 校验失败：说明文件损坏或被劫持，删掉 .part 换下一个源从头重下。
                try? FileManager.default.removeItem(at: partFile)
                lastError = URLError(.cannotDecodeContentData)
            } catch is CancellationError {
                // 暂停或取消时保留 .part，方便下次断点续传。
                throw CancellationError()
            } catch {
                // 网络或 HTTP 错误：保留已下载的 .part，换下一个源继续 Range 续传。
                lastError = error
            }
        }
        throw lastError
    }

    private static func streamOneSource(
        url: URL,
        to partFile: URL,
        expectedBytes: Int64?,
        expectedBlake3: String?,
        progress: @escaping @Sendable (Int64, Int64) -> Void
    ) async throws {
        let offset = fileSize(partFile) ?? 0
        let outcome = try await runStreamingRequest(
            url: url,
            partFile: partFile,
            offset: offset,
            expectedBytes: expectedBytes,
            progress: progress
        )
        if outcome == .rangeNotSatisfiable {
            let hasDigest = expectedBlake3?.isEmpty == false
            let looksComplete = hasDigest
                ? (try verifyDigest(of: partFile, blake3: expectedBlake3))
                : (expectedBytes != nil && expectedBytes! > 0 && offset == expectedBytes!)
            if offset > 0 && looksComplete {
                progress(offset, offset)
                return
            }
            try? FileManager.default.removeItem(at: partFile)
            let retry = try await runStreamingRequest(
                url: url,
                partFile: partFile,
                offset: 0,
                expectedBytes: expectedBytes,
                progress: progress
            )
            if retry != .ok {
                throw URLError(.badServerResponse)
            }
        }
    }

    private enum StreamOutcome: Sendable {
        case ok
        case rangeNotSatisfiable
    }

    private static func runStreamingRequest(
        url: URL,
        partFile: URL,
        offset: Int64,
        expectedBytes: Int64?,
        progress: @escaping @Sendable (Int64, Int64) -> Void
    ) async throws -> StreamOutcome {
        var request = URLRequest(url: url)
        request.timeoutInterval = 30
        if offset > 0 {
            request.setValue("bytes=\(offset)-", forHTTPHeaderField: "Range")
        }

        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 60 * 60
        config.requestCachePolicy = .reloadIgnoringLocalCacheData

        let state = Mutex<StreamTaskBox>(StreamTaskBox())

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                let delegate = StreamingDelegate(
                    partFile: partFile,
                    initialOffset: offset,
                    expectedBytes: expectedBytes,
                    progress: progress,
                    continuation: continuation
                )
                let session = URLSession(configuration: config, delegate: delegate, delegateQueue: nil)
                let task = session.dataTask(with: request)
                let shouldCancel = state.withLock { box -> Bool in
                    box.session = session
                    box.task = task
                    return box.cancelled
                }
                if shouldCancel {
                    delegate.cancelImmediately()
                    task.cancel()
                    session.invalidateAndCancel()
                } else {
                    task.resume()
                    session.finishTasksAndInvalidate()
                }
            }
        } onCancel: {
            let pair = state.withLock { box -> (URLSessionDataTask?, URLSession?) in
                box.cancelled = true
                return (box.task, box.session)
            }
            pair.0?.cancel()
            pair.1?.invalidateAndCancel()
        }
    }

    private struct StreamTaskBox: @unchecked Sendable {
        var task: URLSessionDataTask?
        var session: URLSession?
        var cancelled = false
    }

    private final class StreamingDelegate: NSObject, URLSessionDataDelegate, @unchecked Sendable {
        private let partFile: URL
        private let initialOffset: Int64
        private let expectedBytes: Int64?
        private let progress: @Sendable (Int64, Int64) -> Void

        private let lock = Mutex<DelegateState>(DelegateState())

        private struct DelegateState: @unchecked Sendable {
            var continuation: CheckedContinuation<StreamOutcome, Error>?
            var handle: FileHandle?
            var written: Int64 = 0
            var total: Int64 = 0
            var lastReported: Int64 = 0
            var lastReportTime: ContinuousClock.Instant?
            var outcome: StreamOutcome = .ok
            var failedError: Error?
        }

        init(
            partFile: URL,
            initialOffset: Int64,
            expectedBytes: Int64?,
            progress: @escaping @Sendable (Int64, Int64) -> Void,
            continuation: CheckedContinuation<StreamOutcome, Error>
        ) {
            self.partFile = partFile
            self.initialOffset = initialOffset
            self.expectedBytes = expectedBytes
            self.progress = progress
            self.lock.withLock { $0.continuation = continuation }
        }

        func urlSession(
            _ session: URLSession,
            dataTask: URLSessionDataTask,
            didReceive response: URLResponse,
            completionHandler: @escaping (URLSession.ResponseDisposition) -> Void
        ) {
            guard let http = response as? HTTPURLResponse else {
                fail(URLError(.badServerResponse))
                completionHandler(.cancel)
                return
            }

            if http.statusCode == 416 {
                lock.withLock { $0.outcome = .rangeNotSatisfiable }
                completionHandler(.cancel)
                return
            }

            guard http.statusCode == 200 || http.statusCode == 206 else {
                fail(URLError(.badServerResponse))
                completionHandler(.cancel)
                return
            }

            let isPartial = http.statusCode == 206 && initialOffset > 0
            let contentLength = http.expectedContentLength > 0 ? http.expectedContentLength : 0
            let totalBytes: Int64
            if isPartial {
                if let rangeHeader = http.value(forHTTPHeaderField: "Content-Range"),
                   let slash = rangeHeader.lastIndex(of: "/"),
                   let parsedTotal = Int64(rangeHeader[rangeHeader.index(after: slash)...]),
                   parsedTotal > 0 {
                    totalBytes = parsedTotal
                } else if contentLength > 0 {
                    totalBytes = initialOffset + contentLength
                } else {
                    totalBytes = expectedBytes ?? initialOffset
                }
            } else {
                totalBytes = contentLength > 0 ? contentLength : (expectedBytes ?? 0)
            }

            do {
                let path = partFile.path(percentEncoded: false)
                if !isPartial || !FileManager.default.fileExists(atPath: path) {
                    FileManager.default.createFile(atPath: path, contents: nil)
                }
                let handle = try FileHandle(forWritingTo: partFile)
                let startPos: Int64
                if isPartial {
                    try handle.seekToEnd()
                    startPos = initialOffset
                } else {
                    try handle.truncate(atOffset: 0)
                    startPos = 0
                }
                let now = ContinuousClock.now
                lock.withLock { st in
                    st.handle = handle
                    st.written = startPos
                    st.total = max(totalBytes, startPos)
                    st.lastReported = startPos
                    st.lastReportTime = startPos > 0 ? now : nil
                }
                if startPos > 0 {
                    progress(startPos, max(totalBytes, startPos))
                }
                completionHandler(.allow)
            } catch {
                fail(error)
                completionHandler(.cancel)
            }
        }

        func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
            let report: (Int64, Int64)? = lock.withLock { st in
                guard st.failedError == nil, let handle = st.handle else { return nil }
                do {
                    try handle.write(contentsOf: data)
                    st.written += Int64(data.count)
                    let total = max(st.total, st.written)
                    st.total = total
                    let now = ContinuousClock.now
                    let dueByTime = st.lastReportTime.map { now - $0 >= .milliseconds(100) } ?? true
                    let isDone = total > 0 && st.written >= total
                    if (dueByTime && st.written - st.lastReported >= 65_536) || isDone {
                        st.lastReported = st.written
                        st.lastReportTime = now
                        return (st.written, total)
                    }
                    return nil
                } catch {
                    st.failedError = error
                    return nil
                }
            }
            if let report {
                progress(report.0, report.1)
            }
            let hasError = lock.withLock { $0.failedError != nil }
            if hasError {
                dataTask.cancel()
            }
        }

        func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
            let snapshot = lock.withLock { st -> (CheckedContinuation<StreamOutcome, Error>?, StreamOutcome, Error?, Int64, Int64) in
                try? st.handle?.close()
                st.handle = nil
                let cont = st.continuation
                st.continuation = nil
                return (cont, st.outcome, st.failedError, st.written, st.total)
            }
            guard let continuation = snapshot.0 else { return }
            if snapshot.1 == .rangeNotSatisfiable {
                continuation.resume(returning: .rangeNotSatisfiable)
                return
            }
            if let writeErr = snapshot.2 {
                continuation.resume(throwing: writeErr)
                return
            }
            if let error {
                if (error as? URLError)?.code == .cancelled {
                    continuation.resume(throwing: CancellationError())
                } else {
                    continuation.resume(throwing: error)
                }
                return
            }
            if snapshot.3 > 0 {
                progress(snapshot.3, max(snapshot.4, snapshot.3))
            }
            continuation.resume(returning: .ok)
        }

        func cancelImmediately() {
            let continuation = lock.withLock { st -> CheckedContinuation<StreamOutcome, Error>? in
                try? st.handle?.close()
                st.handle = nil
                let cont = st.continuation
                st.continuation = nil
                return cont
            }
            continuation?.resume(throwing: CancellationError())
        }

        private func fail(_ error: Error) {
            lock.withLock { st in
                if st.failedError == nil { st.failedError = error }
            }
        }
    }

    static func fileSize(_ url: URL) -> Int64? {
        guard let vals = try? url.resourceValues(forKeys: [.fileSizeKey]),
              let size = vals.fileSize else { return nil }
        return Int64(size)
    }

    static func verifyDigest(of file: URL, blake3: String?) throws -> Bool {
        if let blake3, !blake3.isEmpty {
            let actual = try fileDigest(path: file.path(percentEncoded: false))
            if actual.caseInsensitiveCompare(blake3) != .orderedSame {
                return false
            }
        }
        return true
    }

    /// 合并高频后台下载进度回调，确保同一时刻最多只有一个 `@MainActor` 更新排队，
    /// 避免高速宽带下密集刷新把主线程事件循环堵死。
    final class ProgressRelay: @unchecked Sendable {
        private struct State {
            var latest: Double = -1
            var delivered: Double = -1
            var scheduled = false
        }

        private let state = Mutex(State())
        private let minStep: Double
        private let deliver: @MainActor @Sendable (Double) -> Void

        init(minStep: Double = 0.005, deliver: @escaping @MainActor @Sendable (Double) -> Void) {
            self.minStep = minStep
            self.deliver = deliver
        }

        func send(_ raw: Double) {
            let value = min(1, max(0, raw))
            let shouldSchedule = state.withLock { st -> Bool in
                st.latest = value
                guard !st.scheduled else { return false }
                if st.delivered >= 0, value < 1, abs(value - st.delivered) < minStep {
                    return false
                }
                st.scheduled = true
                return true
            }
            guard shouldSchedule else { return }
            Task { @MainActor in
                while true {
                    let next = self.state.withLock { st -> Double? in
                        let v = st.latest
                        if st.delivered >= 0, v < 1, abs(v - st.delivered) < self.minStep {
                            st.scheduled = false
                            return nil
                        }
                        st.delivered = v
                        return v
                    }
                    guard let next else { break }
                    self.deliver(next)
                    let more = self.state.withLock { st -> Bool in
                        if st.latest != st.delivered,
                           st.latest >= 1 || abs(st.latest - st.delivered) >= self.minStep {
                            return true
                        }
                        st.scheduled = false
                        return false
                    }
                    if !more { break }
                    await Task.yield()
                }
            }
        }
    }
}
