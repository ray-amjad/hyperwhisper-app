//
//  ModeNameLogLineTests.swift
//  hyperwhisperTests
//
//  Issue #804: no `AppLogger` line writes the user-typed Mode name.
//
//  A Mode's `name` is free text the user typed (issue #795). `SentryService`
//  attaches recent unified-log lines as the `recent_logs` extra, and
//  `AppLogger.sanitizeLogLine` has no rule for a mode name. Today only
//  `.warning` and above reach that extra, but that rests on one missing
//  `--info` flag in `getRecentLogs`, so the guard is on the log lines
//  themselves: report the Mode's UUID and `PresetType.reportingValue(for:)`.
//
//  This reads production source because a log line's arguments cannot be
//  observed from a test. It is line-based: a multi-line `AppLogger` call, or a
//  name bound to a local with no `mode`/`Mode` in it, is not seen.
//

import Foundation
import Testing

struct ModeNameLogLineTests {

    @Test func noAppLoggerLineInterpolatesAModeName() throws {
        let directory = ProductionSource.url("app/macos/hyperwhisper")
        let files = try ProductionSource.swiftFiles(under: directory)
        #expect(files.count >= 50, "the macOS app source tree was not found where this test expects it")

        let logCall = try NSRegularExpression(
            pattern: #"AppLogger\.[A-Za-z]+\.(info|debug|notice|warning|error|fault)\("#
        )
        // `mode.name`, `modeOverride?.name`, `firstMode.name`, `selectedModeName`, `modeName`.
        let modeName = try NSRegularExpression(
            pattern: #"[Mm]ode[A-Za-z]*\??\.name\b|[Mm]odeName\b"#
        )

        var offenders: [String] = []
        for file in files {
            let source = try ProductionSource.text(of: file)
            for (offset, line) in source.components(separatedBy: .newlines).enumerated() {
                guard !line.trimmingCharacters(in: .whitespaces).hasPrefix("//") else { continue }
                let range = NSRange(line.startIndex..., in: line)
                guard logCall.firstMatch(in: line, range: range) != nil,
                      modeName.firstMatch(in: line, range: range) != nil else { continue }
                offenders.append("\(file.lastPathComponent):\(offset + 1)")
            }
        }

        #expect(offenders.isEmpty, """
            AppLogger line writes a Mode name at \(offenders.joined(separator: ", ")). \
            Log the Mode's id and PresetType.reportingValue(for:) instead (issue #804).
            """)
    }
}
