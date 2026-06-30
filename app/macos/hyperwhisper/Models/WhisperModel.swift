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

    /// Model size in MB (approximate, based on API data)
    var sizeInMB: Int {
        switch self {
        case .tiny: return 69
        case .tinyEn: return 140
        case .base: return 132
        case .baseEn: return 133
        case .small: return 445
        case .smallEn: return 444
        case .medium: return 1441
        case .mediumEn: return 1441
        case .largeV2: return 2918
        case .largeV3: return 2918
        case .largeV3Turbo: return 3010
        }
    }

    /// Relative speed (1.0 = base speed)
    /// English-only models are typically faster than multilingual equivalents
    var relativeSpeed: Float {
        switch self {
        case .tiny, .tinyEn: return 4.0
        case .base, .baseEn: return 2.0
        case .small, .smallEn: return 1.0
        case .medium, .mediumEn: return 0.5
        case .largeV2, .largeV3: return 0.25
        case .largeV3Turbo: return 0.3  // Turbo variant is optimized for speed
        }
    }
    
    /// Check if this is an English-only model
    var isEnglishOnly: Bool {
        return rawValue.hasSuffix(".en")
    }
    
    /// Check if this is a large model variant
    var isLargeVariant: Bool {
        return rawValue.hasPrefix("large")
    }
}

