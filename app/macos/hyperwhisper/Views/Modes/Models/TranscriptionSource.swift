//
//  TranscriptionSource.swift
//  HyperWhisper
//
//  The transcription picker's source axis.
//

import SwiftUI

// MARK: - Transcription Source

/// UI-only 3-way source axis for the transcription picker. Replaces the old
/// implicit Local/Cloud toggle + provider dropdown with one explicit choice.
/// Derived from (and applied back onto) the persisted `provider` + `cloudProvider`
/// fields — no new storage:
///   .onDevice          ⇄ provider == .local
///   .hyperwhisperCloud ⇄ provider == .cloud && cloudProvider == "hyperwhisper"
///   .yourProvider      ⇄ provider == .cloud && cloudProvider != "hyperwhisper" (BYOK)
enum TranscriptionSource: String, CaseIterable, Identifiable {
    case onDevice
    case hyperwhisperCloud
    case yourProvider

    var id: String { rawValue }

    var label: String {
        switch self {
        case .onDevice:
            return "modes.source.onDevice".localized
        case .hyperwhisperCloud:
            return "modes.source.hyperwhisperCloud".localized
        case .yourProvider:
            return "modes.source.yourProvider".localized
        }
    }
}
