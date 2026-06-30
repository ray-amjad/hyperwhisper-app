//
//  BackupDefaultModelByModeTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

struct BackupDefaultModelByModeTests {
    @Test func mergePreservesLocalOnlyAssignmentsAndLetsBackupWinOnOverlap() {
        let localOnlyModeId = UUID().uuidString
        let sharedModeId = UUID().uuidString
        let backupOnlyModeId = UUID().uuidString

        let merged = BackupManager.mergeDefaultModelByMode(
            current: [
                localOnlyModeId: "local-model",
                sharedModeId: "old-shared-model"
            ],
            imported: [
                sharedModeId: "backup-shared-model",
                backupOnlyModeId: "backup-only-model"
            ]
        )

        #expect(merged[localOnlyModeId] == "local-model")
        #expect(merged[sharedModeId] == "backup-shared-model")
        #expect(merged[backupOnlyModeId] == "backup-only-model")
    }

    @Test func keepBothRemapMovesImportedAssignmentToNewModeIdBeforeMerge() {
        let existingModeId = UUID()
        let importedModeId = UUID()

        let remappedImported = BackupManager.remapDefaultModelByMode(
            [existingModeId.uuidString: "backup-model"],
            using: [existingModeId: importedModeId]
        )
        let merged = BackupManager.mergeDefaultModelByMode(
            current: [existingModeId.uuidString: "existing-local-model"],
            imported: remappedImported
        )

        #expect(remappedImported[existingModeId.uuidString] == nil)
        #expect(remappedImported[importedModeId.uuidString] == "backup-model")
        #expect(merged[existingModeId.uuidString] == "existing-local-model")
        #expect(merged[importedModeId.uuidString] == "backup-model")
    }
}
