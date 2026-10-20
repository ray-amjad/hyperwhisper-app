//
//  OnboardingTypes.swift
//  hyperwhisper
//
//  The onboarding step machine and the value types that cross the seams of
//  `OnboardingFlowModel`. Moved out of OnboardingFlowModel.swift unchanged (#911).
//

import Foundation

// MARK: - Step machine

/// The eight onboarding steps, in order. Raw values are stable so a step can be
/// compared, persisted, or reported for analytics without a lookup table.
enum OnboardingStep: Int, CaseIterable, Identifiable, Comparable {
    case welcome
    case permissions
    case source
    case configure
    case setup
    case microphone
    case tryIt
    case done

    var id: Int { rawValue }

    static func < (lhs: OnboardingStep, rhs: OnboardingStep) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

// MARK: - Value types crossing the seams

/// Microphone authorization, reduced to the three cases the flow reacts to.
/// `.denied` covers restricted as well: in both cases the OS will not re-prompt,
/// so the flow deep links System Settings instead of asking again.
enum OnboardingMicrophoneAuthorization: Equatable {
    case undetermined
    case authorized
    case denied
}

/// One selectable input device. `id` is empty for the synthetic "System Default"
/// row, which is always offered first (see `deviceOptions`).
struct OnboardingInputDevice: Identifiable, Equatable {
    let id: String
    let name: String

    /// The synthetic first option. An empty id means "follow the system default",
    /// which is exactly how `settingsManager.selectedMicrophoneId` encodes it.
    static func systemDefault(name: String) -> OnboardingInputDevice {
        OnboardingInputDevice(id: "", name: name)
    }

    var isSystemDefault: Bool { id.isEmpty }
}

/// The last known download error for each local engine. Both are carried so the
/// flow can pick the one that matches the selected model rather than defaulting
/// to Whisper, which is how Parakeet failures used to disappear.
struct OnboardingDownloadErrors: Equatable {
    var whisper: String?
    var parakeet: String?

    static let none = OnboardingDownloadErrors(whisper: nil, parakeet: nil)

    func message(for kind: OnboardingModelSelection.Kind) -> String? {
        switch kind {
        case .whisper: return whisper
        case .parakeet: return parakeet
        }
    }
}

/// Result of a license probe or activation, reduced to what the flow needs.
struct OnboardingLicenseOutcome: Equatable {
    let isValid: Bool
    let errorMessage: String?

    static func failure(_ message: String?) -> OnboardingLicenseOutcome {
        OnboardingLicenseOutcome(isValid: false, errorMessage: message)
    }
}

/// The fully staged source configuration. Nothing in here has touched Core Data,
/// SettingsManager, the Keychain, or LicenseManager. It is handed to the
/// committer once, at an explicit commit boundary.
struct OnboardingStagedSource: Equatable {
    let source: TranscriptionSource
    /// Written verbatim to `Mode.model`. A Whisper catalog name ("base") or a
    /// Parakeet id ("parakeet-tdt-0.6b-v2"), never a prettified display name.
    let model: String
    let cloudProvider: String?
    let postProcessingMode: Int16
    let cloudAccuracyTier: String?
}

/// Opaque marker for whatever the committer needs in order to put production
/// state back. The flow only ever stores and returns it, so the Core Data detail
/// stays out of the presentation layer and out of the tests.
protocol OnboardingRestorePoint {}
