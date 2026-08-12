//
//  WhisperModel.swift
//  hyperwhisper
//
//  Extracted from TranscriptionPipeline to reduce coupling.

import Foundation

/// Available Whisper models
enum WhisperModel: String, CaseIterable {
    // Models paired by size (multilingual + English-only)
    case tiny = "tiny"
    case tinyEn = "tiny.en"
    case base = "base"
    case baseEn = "base.en"
    case small = "small"
    case smallEn = "small.en"
    case medium = "medium"
    case mediumEn = "medium.en"
    case largeV2 = "large-v2"
    case largeV3 = "large-v3"
    case largeV3Turbo = "large-v3_turbo"

    var name: String { 
        // Use cached display names if available, otherwise format the raw value
        if let displayName = UserDefaults.standard.dictionary(forKey: "modelDisplayNames")?[rawValue] as? String {
            return displayName
        }
        
        // Fallback formatting
        switch self {
        case .tiny: return "Tiny (Multilingual)"
        case .tinyEn: return "Tiny (English-only)"
        case .base: return "Base (Multilingual)"
        case .baseEn: return "Base (English-only)"
        case .small: return "Small (Multilingual)"
        case .smallEn: return "Small (English-only)"
        case .medium: return "Medium (Multilingual)"
        case .mediumEn: return "Medium (English-only)"
        case .largeV2: return "Large v2"
        case .largeV3: return "Large v3"
        case .largeV3Turbo: return "Large v3 Turbo"
        }
    }

    /// Check if this is an English-only model
    var isEnglishOnly: Bool {
        return rawValue.hasSuffix(".en")
    }
    
}

