//
//  ProviderType.swift
//  HyperWhisper
//
//  Local or cloud provider type.
//

import SwiftUI

// MARK: - Provider Type

enum ProviderType: String, CaseIterable, Identifiable {
    case local
    case cloud
    var id: String { rawValue }
    var label: String {
        switch self {
        case .local:
            return "modes.provider.local".localized
        case .cloud:
            return "modes.provider.cloud".localized
        }
    }
}
