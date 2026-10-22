//
//  BackupImportRequestTests.swift
//  hyperwhisperTests
//
//  Issue #1398: the backup import options sheet is driven by `.sheet(item:)` with a
//  `BackupImportRequest`. SwiftUI re-presents an item sheet only when the item's identity
//  changes, so each pick of a file must get a new `id`, even when the user picks the same file
//  twice. The request must also carry the picked file and its contents to the sheet unchanged.
//

import Foundation
import Testing
@testable import HyperWhisper

struct BackupImportRequestTests {

    private let url = URL(fileURLWithPath: "/tmp/hyperwhisper-backup.json")

    private let contents = BackupContents(
        format: .legacyV1,
        hasSettings: true,
        hasModes: false,
        hasVocabulary: true,
        vocabularyCount: 3,
        hasAPIKeys: false,
        hasLicense: false,
        appVersion: "1.0"
    )

    @Test func carriesThePickedFileAndContents() {
        let request = BackupImportRequest(url: url, contents: contents)

        #expect(request.url == url)
        #expect(request.contents.format == .legacyV1)
        #expect(request.contents.hasSettings)
        #expect(!request.contents.hasModes)
        #expect(request.contents.vocabularyCount == 3)
    }

    @Test func pickingTheSameFileTwiceGivesANewIdentity() {
        let first = BackupImportRequest(url: url, contents: contents)
        let second = BackupImportRequest(url: url, contents: contents)

        #expect(first.id != second.id)
    }
}
