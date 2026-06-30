
//
//  TranscriptionState.swift
//  hyperwhisper
//
//  Created by Gemini on 31/08/2025.
//

import Foundation

/// Represents the state of the TranscriptionPipeline.
/// WARM-UP REMOVED: libwhisper.cpp loads models instantly!
enum TranscriptionState: Equatable, CustomStringConvertible {
    case idle
    // warmingUp case REMOVED - no longer needed with libwhisper.cpp
    case transcribing(provider: String, progress: Float)
    case postProcessing
    case error(message: String)

    // CustomStringConvertible conformance for string interpolation and debugging
    var description: String {
        switch self {
        case .idle:
            return "idle"
        // warmingUp case removed - models load instantly now
        case let .transcribing(provider, progress):
            return "transcribing with \(provider) (\(Int(progress * 100))%)"
        case .postProcessing:
            return "post-processing"
        case let .error(message):
            return "error: \(message)"
        }
    }
    
    // Equatable conformance to allow state comparisons
    static func == (lhs: TranscriptionState, rhs: TranscriptionState) -> Bool {
        switch (lhs, rhs) {
        case (.idle, .idle):
            return true
        // warmingUp case removed - no comparison needed
        case let (.transcribing(lProv, lProg), .transcribing(rProv, rProg)):
            return lProv == rProv && lProg == rProg
        case (.postProcessing, .postProcessing):
            return true
        case let (.error(lMsg), .error(rMsg)):
            return lMsg == rMsg
        default:
            return false
        }
    }
}
