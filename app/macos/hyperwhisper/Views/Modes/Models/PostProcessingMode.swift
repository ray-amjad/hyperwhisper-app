//
//  PostProcessingMode.swift
//  HyperWhisper
//
//  Post-processing mode for transcriptions.
//

import SwiftUI

// MARK: - Post-Processing Mode

/// Post-processing mode for transcriptions
enum PostProcessingMode: Int16, CaseIterable {
    case off = 0      // No post-processing
    case cloud = 1    // Cloud-based AI post-processing (OpenAI)
    case local = 2    // Local LLM post-processing (Qwen)

    var displayName: String {
        switch self {
        case .off:
            return "Off"
        case .cloud:
            return "Cloud"
        case .local:
            return "Local"
        }
    }

    var requiresInternet: Bool {
        switch self {
        case .off:
            return false
        case .cloud:
            return true
        case .local:
            return false
        }
    }

    /// Default provider for this post-processing mode
    var defaultProvider: PostProcessingProvider? {
        switch self {
        case .off:
            return nil
        case .cloud:
            return .hyperwhisper  // HyperWhisper Cloud is the default for cloud post-processing
        case .local:
            return .localLLM
        }
    }

    /// Whether the user can pick a language model for this mode
    var allowsModelSelection: Bool {
        switch self {
        case .off:
            return false
        case .cloud, .local:
            return true
        }
    }

    /// Whether the app should run provider health checks before using this mode
    var requiresHealthCheck: Bool {
        switch self {
        case .cloud:
            return true
        case .off, .local:
            return false
        }
    }
}
