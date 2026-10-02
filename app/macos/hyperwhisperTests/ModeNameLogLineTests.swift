//
//  ModeNameLogLineTests.swift
//  hyperwhisperTests
//
//  Issue #804: no log line writes the user-typed Mode name.
//
//  A Mode's `name` is free text the user typed (issue #795). `SentryService`
//  attaches recent unified-log lines as the `recent_logs` extra, and
//  `AppLogger.sanitizeLogLine` has no rule for a mode name. Today only
//  `.warning` and above reach that extra, but that rests on one missing
//  `--info` flag in `getRecentLogs`, so the guard is on the log lines
//  themselves: report the Mode's UUID and `PresetType.reportingValue(for:)`.
//
//  This reads production source because a log line's arguments cannot be
//  observed from a test. What it sees:
//
//  - Every logger call, not only `AppLogger.<category>.<level>(`: a file-local
//    `Logger(subsystem: "com.hyperwhisper.app", …)` writes to the same
//    subsystem `getRecentLogs` reads, so `logger.info(`, `self.logger.debug(`,
//    `homeViewLogger.info(`, `os_log(` and `NSLog(` all count.
//  - A call that spans lines, up to 12 lines, until its parentheses balance.
//  - Any `<receiver>.name` / `<receiver>?.name`, whatever the receiver is
//    called (`byId.name`, `fallback.name`), unless the receiver is on the
//    allow-list below of receivers that are known not to be a Mode.
//  - Closure shorthand `$0.name` / `$1?.name` (`modes.map { $0.name }`),
//    unless the collection the closure runs over is on the per-file
//    allow-list of collections known not to hold Modes.
//  - `modeName`, `selectedModeName`, `currentMode` (the persisted name string),
//    and a bare `\(name` interpolation (a local such as `let name = mode.name`).
//
//  What it cannot see: a Mode name passed through a local with another name
//  (`let title = mode.name`), or a receiver on the allow-list that is later
//  rebound to a Mode. A new non-Mode `.name` on a log line fails this test on
//  purpose: check its type, then add it to the allow-list with a comment.
//

import Foundation
import Testing

struct ModeNameLogLineTests {

    /// Receivers whose `.name` is NOT a Mode name, as of issue #804.
    /// Each one was checked by type. Keep this list short and commented.
    private static let nonModeNameReceivers: Set<String> = [
        "savedDevice",   // AudioDevice: AudioRecordingManager, MainAppView
        "device",        // AudioDevice: AudioDeviceManager.selectDevice
        "selected",      // AudioDevice: AudioDeviceManager system-default switch
        "endpoint",      // custom post-processing endpoint: CustomPostProcessingManager, AIPostProcessor
        "original",      // custom post-processing endpoint: CustomPostProcessingManager.duplicate
        "provider",      // TranscriptionProvider: TranscriptionProviderRouter
        "model",         // WhisperModel: WhisperModelManager
        "uploadedFile",  // Gemini Files API upload: GeminiTranscriptionProvider
        "currentModel",  // WhisperCppModel: LibWhisperProvider
    ]

    /// `<collection>.<method> { … $0.name … }` pairs, by file, whose elements are
    /// NOT Modes, as of issue #804. Keyed by file so a Mode collection with the
    /// same name elsewhere still fails. Each one was checked by type.
    private static let nonModeShorthandCollections: Set<String> = [
        "WhisperModelManager.swift:downloaded",        // [WhisperCppModel]: the model-list-changed line
        "LibWhisperProvider.swift:downloadedModels",   // WhisperModelManager.downloadedModels: [WhisperCppModel], model-not-found line
    ]

    /// Files where a bare `\(name` interpolation is known not to be a Mode name.
    private static let bareNameFiles: Set<String> = [
        "CustomPostProcessingManager.swift",  // `name` is the deleted endpoint's name
    ]

    /// How many extra lines a multi-line logger call may span before the scan gives up.
    private static let maxContinuationLines = 12

    @Test func noLogLineInterpolatesAModeName() throws {
        let directory = ProductionSource.url("app/macos/hyperwhisper")
        let files = try ProductionSource.swiftFiles(under: directory)
        #expect(files.count >= 50, "the macOS app source tree was not found where this test expects it")

        // Any `<ident>.<level>(` call: `AppLogger.ui.info(`, `logger.info(`,
        // `self.logger.debug(`, `log.notice(`. Plus `os_log(` and `NSLog(`.
        let logCall = try NSRegularExpression(
            pattern: #"\b[A-Za-z_][A-Za-z0-9_]*\.(?:info|debug|notice|warning|error|fault|trace|critical|log)\(|\b(?:os_log|NSLog)\("#
        )
        // `mode.name`, `modeOverride?.name`, `byId.name`, `fallback.name`, and closure
        // shorthand `$0.name` / `$1?.name`; group 1 is the receiver.
        let dotName = try NSRegularExpression(
            pattern: #"(\$[0-9]+|\b[A-Za-z_][A-Za-z0-9_]*)\??\.name\b"#
        )
        // The collection a shorthand closure runs over: `downloaded.map { $0.name }`,
        // `items.sorted(by: { $0.name < $1.name })`. Group 1 is the collection,
        // group 2 the first `$N` in that closure whose `.name` is read.
        let shorthandOwner = try NSRegularExpression(
            pattern: #"\b([A-Za-z_][A-Za-z0-9_]*)\??\.[A-Za-z_][A-Za-z0-9_]*\s*(?:\([^{}()]*)?\{[^{}]*?(\$[0-9]+)\??\.name\b"#
        )
        // `modeName`, `selectedModeName`, and `currentMode` when it is not a
        // receiver itself (`currentMode?.id` is fine).
        let nameIdentifier = try NSRegularExpression(
            pattern: #"\b[A-Za-z]*[Mm]odeName\b|\bcurrentMode\b(?!\??\.)"#
        )
        // `\(name, privacy: .public)`, but not `\(name.rawValue)`.
        let bareName = try NSRegularExpression(
            pattern: #"\\\(\s*name\b(?!\??\.)"#
        )

        var offenders: [String] = []
        for file in files {
            let source = try ProductionSource.text(of: file)
            let lines = source.components(separatedBy: .newlines)
            for (index, line) in lines.enumerated() {
                guard !line.trimmingCharacters(in: .whitespaces).hasPrefix("//") else { continue }
                let lineRange = NSRange(line.startIndex..., in: line)
                guard let call = logCall.firstMatch(in: line, range: lineRange),
                      let callStart = Range(call.range, in: line)?.lowerBound else { continue }

                // The call and its arguments, until the parentheses balance.
                var statement = String(line[callStart...])
                var depth = Self.parenDepth(of: statement)
                var next = index + 1
                while depth > 0, next < lines.count, next - index <= Self.maxContinuationLines {
                    statement += "\n" + lines[next]
                    depth += Self.parenDepth(of: lines[next])
                    next += 1
                }

                let range = NSRange(statement.startIndex..., in: statement)
                // Shorthand `$N` positions whose closure runs over an allow-listed collection.
                let exemptShorthand = Set(shorthandOwner.matches(in: statement, range: range).compactMap { match -> Int? in
                    guard let owner = Range(match.range(at: 1), in: statement) else { return nil }
                    let key = "\(file.lastPathComponent):\(statement[owner])"
                    return Self.nonModeShorthandCollections.contains(key) ? match.range(at: 2).location : nil
                })
                let modeReceivers = dotName.matches(in: statement, range: range).compactMap { match -> String? in
                    guard let receiver = Range(match.range(at: 1), in: statement) else { return nil }
                    let name = String(statement[receiver])
                    if name.hasPrefix("$") {
                        return exemptShorthand.contains(match.range(at: 1).location) ? nil : name
                    }
                    return Self.nonModeNameReceivers.contains(name) ? nil : name
                }
                let hasNameIdentifier = nameIdentifier.firstMatch(in: statement, range: range) != nil
                let hasBareName = !Self.bareNameFiles.contains(file.lastPathComponent)
                    && bareName.firstMatch(in: statement, range: range) != nil

                guard !modeReceivers.isEmpty || hasNameIdentifier || hasBareName else { continue }
                offenders.append("\(file.lastPathComponent):\(index + 1)")
            }
        }

        #expect(offenders.isEmpty, """
            A log line writes a Mode name at \(offenders.joined(separator: ", ")). \
            Log the Mode's id and PresetType.reportingValue(for:) instead (issue #804). \
            If the `.name` there is not a Mode, add its receiver to nonModeNameReceivers \
            (or a `$0.name` closure's collection to nonModeShorthandCollections) with a comment.
            """)
    }

    private static func parenDepth(of text: String) -> Int {
        text.reduce(0) { depth, character in
            switch character {
            case "(": return depth + 1
            case ")": return depth - 1
            default: return depth
            }
        }
    }
}
