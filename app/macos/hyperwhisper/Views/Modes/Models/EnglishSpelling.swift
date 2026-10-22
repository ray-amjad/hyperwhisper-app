//
//  EnglishSpelling.swift
//  HyperWhisper
//
//  English spelling variant for post-processing.
//

import SwiftUI

// MARK: - English Spelling

/// English spelling variant for post-processing
/// Only applies when post-processing is enabled and language is English
enum EnglishSpelling: String, CaseIterable, Identifiable {
    case american = "american"
    case british = "british"
    case australian = "australian"
    case canadian = "canadian"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .american:
            return "modes.englishSpelling.american".localized
        case .british:
            return "modes.englishSpelling.british".localized
        case .australian:
            return "modes.englishSpelling.australian".localized
        case .canadian:
            return "modes.englishSpelling.canadian".localized
        }
    }

    var description: String {
        switch self {
        case .american:
            return "modes.englishSpelling.american.description".localized
        case .british:
            return "modes.englishSpelling.british.description".localized
        case .australian:
            return "modes.englishSpelling.australian.description".localized
        case .canadian:
            return "modes.englishSpelling.canadian.description".localized
        }
    }
}
