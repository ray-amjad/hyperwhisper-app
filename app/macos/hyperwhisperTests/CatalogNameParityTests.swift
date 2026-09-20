//
//  CatalogNameParityTests.swift
//  hyperwhisperTests
//
//  A model has exactly 1 name, and `shared-models/models-catalog.json` is where
//  it is written (#837). `CloudTranscriptionModels.swift` used to carry its own
//  33 literals and they had drifted from Windows: `Whisper Large v3` here
//  against `Whisper Large V3` there, `Scribe v2` against `Scribe V2`, and 4 rows
//  appended `(Preview)` beside the `previewStatus` field that already said so.
//
//  `CloudTranscriptionModel.displayName` is now a catalog lookup with an
//  `?? id` fallback. This file is what keeps that fallback unreachable: a
//  registry row with no catalog entry fails here, so a raw model id can never
//  reach a user.
//

import Foundation
import Testing
@testable import HyperWhisper

struct CatalogNameParityTests {

    @Test("Every registry row resolves a catalog name")
    func everyRegistryRowResolvesACatalogName() {
        for model in CloudTranscriptionModels.availableModels {
            let key = SharedModelsCatalog.providerKey(model.provider)
            let name = SharedModelsCatalog.entry(
                provider: key, kind: .voice, id: model.id)?.displayName
            #expect(
                name?.isEmpty == false,
                "\(key)/\(model.id) has no displayName in shared-models/models-catalog.json")
            #expect(
                model.displayName == name,
                "\(key)/\(model.id) shows '\(model.displayName)', the catalog says '\(name ?? "nil")'")
        }
    }

    /// The style rule lives in `hw-catalog::style_violation` and is checked
    /// there on every catalog row. This is the user-facing half of it: whatever
    /// the picker actually draws must obey the same rule, which catches a name
    /// a view builds by hand rather than reading.
    @Test("No shown name carries a status, a bracketed domain or a padded version")
    func noShownNameBreaksTheStyleRule() {
        for model in CloudTranscriptionModels.availableModels {
            let name = model.displayName
            #expect(!name.lowercased().contains("(preview)"),
                    "\(model.id): status belongs in previewStatus, and the UI draws the badge")
            #expect(!name.lowercased().contains("(medical)"),
                    "\(model.id): write the domain as a word - `Universal-2 Medical`")
            #expect(!name.hasSuffix(".0"),
                    "\(model.id): drop the padding - `Grok Voice Transcribe 1`, not `... 1.0`")
        }
    }
}
