//
//  HistoryPlaybackStartTests.swift
//  hyperwhisperTests
//
//  #1456: with a History row open, moving its audio file away made Play do
//  nothing, with no message. The detail pane trusted an "exists" flag cached
//  when the row loaded. Play now re-checks the disk at press time, and a gone
//  file re-runs the row's audio check so the pane shows "Audio file missing".
//
//  The pane itself needs a real window, so these tests pin the decision Play
//  makes: a file that is on disk plays, and a file that has gone since the
//  row loaded (or was never there) does not.
//

import Foundation
import Testing

@testable import HyperWhisper

struct HistoryPlaybackStartTests {

    private func makeTempAudioFile() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("history-play-\(UUID().uuidString).wav")
        try Data([0x52, 0x49, 0x46, 0x46]).write(to: url)
        return url
    }

    @Test func aFileOnDiskPlays() throws {
        let url = try makeTempAudioFile()
        defer { try? FileManager.default.removeItem(at: url) }

        let start = HistoryPlaybackStart.resolve(path: url.path, cachedExists: true)
        #expect(start == .play(URL(fileURLWithPath: url.path)))
    }

    /// The #1456 case: the row loaded while the file existed, then it moved.
    @Test func aFileMovedAwayAfterTheRowLoadedIsGoneNotPlayed() throws {
        let url = try makeTempAudioFile()
        let cachedExists = FileManager.default.fileExists(atPath: url.path)
        #expect(cachedExists)

        try FileManager.default.removeItem(at: url)

        let start = HistoryPlaybackStart.resolve(path: url.path, cachedExists: cachedExists)
        #expect(start == .audioGone)
    }

    @Test func noPathIsGone() {
        #expect(HistoryPlaybackStart.resolve(path: nil, cachedExists: true) == .audioGone)
        #expect(HistoryPlaybackStart.resolve(path: "", cachedExists: true) == .audioGone)
    }

    @Test func aPathTheRowAlreadyFoundMissingIsGone() {
        let start = HistoryPlaybackStart.resolve(
            path: "/tmp/anything.wav",
            cachedExists: false,
            fileExists: { _ in true }
        )
        #expect(start == .audioGone)
    }

    @Test func theDiskIsCheckedAtPressTime() {
        var checked: [String] = []
        let start = HistoryPlaybackStart.resolve(
            path: "/recordings/imported_1_short.wav",
            cachedExists: true,
            fileExists: { checked.append($0); return false }
        )
        #expect(start == .audioGone)
        #expect(checked == ["/recordings/imported_1_short.wav"])
    }
}
