//
//  ModeEditorDefaults.swift
//  HyperWhisper
//
//  Pure seeds for the CREATE branch of ModeEditorView (issue #873).
//

import Foundation

enum ModeEditorDefaults {
    /// Transcription provider a brand-new mode opens on. HyperWhisper Cloud needs
    /// an active licence, so an unlicensed user with an installed local model
    /// opens on On-device; everyone else keeps the Cloud default.
    static func initialProvider(licenseActive: Bool, availableModelIds: [String]) -> ProviderType {
        (licenseActive || availableModelIds.isEmpty) ? .cloud : .local
    }
}
