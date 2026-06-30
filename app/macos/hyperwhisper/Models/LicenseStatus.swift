//
//  LicenseStatus.swift
//  hyperwhisper
//
//  LICENSE STATUS MODEL
//  Defines the various license states and their UI representations.
//
//  This enum encapsulates all possible license states in HyperWhisper:
//  - trial: User hasn't purchased a license (5 min/day limit, 3 model limit)
//  - active: Valid paid license (unlimited usage)
//  - expired: License was valid but expired
//  - invalid: License key is malformed or revoked
//
//  Each status includes:
//  - Localized display text for UI
//  - User-friendly descriptions (trial description uses format string with model count)
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
/// 1. Display license state in the UI (badge colors, text)
/// 2. Enforce usage limits (daily transcription time, model downloads)
/// 3. Determine which transcription identifier to use (license_key vs device_id)
/// 4. Show appropriate upgrade prompts and messages
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

    /// User-friendly description of the status
    /// Provides context about what this status means for the user
    ///
    /// NOTE: For trial status, this returns a format string that requires
    /// the model limit parameter. Use LicenseManager.licenseStatusDescription
    /// instead, which properly formats the trial description with the current
    /// model limit from the usage tracker.
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
