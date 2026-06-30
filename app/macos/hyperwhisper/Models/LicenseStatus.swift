//
//  LicenseStatus.swift
//  hyperwhisper
//
//  LICENSE STATUS MODEL
//  Defines the various license states and their UI representations.
//
//  This enum encapsulates the HyperWhisper Cloud license states. Local, on-device
//  transcription is always free and unlimited (open source); these states only
//  describe the Cloud "wallet":
//  - trial: User hasn't activated a Cloud license (uses device_id for Cloud)
//  - active: Valid Cloud license (uses license_key for Cloud)
//  - expired: License was valid but expired
//  - invalid: License key is malformed or revoked
//
//  Each status includes:
//  - Localized display text for UI
//  - User-friendly descriptions
//  - Color coding for badges
//

import Foundation
import SwiftUI

/// Notification posted when license status changes (activation, deactivation, validation)
/// This allows other managers to react to license changes (e.g., invalidate credit cache)
extension Notification.Name {
    static let licenseStatusChanged = Notification.Name("com.hyperwhisper.licenseStatusChanged")
}

/// Represents the current license status of the application
///
/// This enum is used throughout the app to:
/// 1. Display Cloud license state in the UI (badge colors, text)
/// 2. Determine which Cloud transcription identifier to use (license_key vs device_id)
/// 3. Surface the Cloud credits CTA
///
/// Note: local transcription/model downloads are unlimited and NOT gated by status.
enum LicenseStatus: String, CaseIterable {
    case trial = "Trial"
    case active = "Active"
    case expired = "Expired"
    case invalid = "Invalid"

    /// Localized display name for the status badge
    /// Used in the UI to show the current license state
    var localizedTitle: String {
        switch self {
        case .trial:
            return "license.status.trial.title".localized
        case .active:
            return "license.status.active.title".localized
        case .expired:
            return "license.status.expired.title".localized
        case .invalid:
            return "license.status.invalid.title".localized
        }
    }

    /// User-friendly description of the Cloud license status.
    var description: String {
        switch self {
        case .trial:
            return "license.status.trial.description".localized
        case .active:
            return "license.status.active.description".localized
        case .expired:
            return "license.status.expired.description".localized
        case .invalid:
            return "license.status.invalid.description".localized
        }
    }

    /// Color representation for UI badges and indicators
    /// Visual feedback for license state:
    /// - Orange: Trial (neutral, informational)
    /// - Green: Active (positive, good to go)
    /// - Red: Expired/Invalid (negative, action required)
    var color: Color {
        switch self {
        case .trial:
            return .orange
        case .active:
            return .green
        case .expired:
            return .red
        case .invalid:
            return .red
        }
    }
}
