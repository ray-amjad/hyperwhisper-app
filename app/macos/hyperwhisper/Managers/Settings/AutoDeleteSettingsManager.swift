//
//  AutoDeleteSettingsManager.swift
//  hyperwhisper
//
//  AUTO-DELETE SETTINGS MANAGER
//  Manages settings for automatic deletion of old recordings and transcripts.
//
//  RESPONSIBILITIES:
//  - Store auto-delete enabled/disabled state
//  - Store time unit (minutes, hours, days)
//  - Store duration value (how many units before deletion)
//  - Persist settings via @AppStorage (UserDefaults)
//
//  ARCHITECTURE:
//  - Uses @AppStorage for automatic persistence
//  - Time unit is stored as raw string value for stability
//  - Default: OFF (recordings are kept indefinitely)
//
//  DELETION BEHAVIOR:
//  When enabled, transcripts older than the configured duration will be deleted
//  along with their associated audio files (both original and trimmed).
//

import Foundation
import SwiftUI
import os

// MARK: - Auto-Delete Time Unit

/// Represents the time unit for auto-delete duration
///
/// Cases:
/// - minutes: Delete recordings after X minutes
/// - hours: Delete recordings after X hours
/// - days: Delete recordings after X days
enum AutoDeleteTimeUnit: String, CaseIterable, Codable {
    case minutes
    case hours
    case days

    /// Localized display name for the time unit
    var localizedName: String {
        switch self {
        case .minutes:
            return NSLocalizedString("history.autoDelete.unit.minutes", value: "Minutes", comment: "")
        case .hours:
            return NSLocalizedString("history.autoDelete.unit.hours", value: "Hours", comment: "")
        case .days:
            return NSLocalizedString("history.autoDelete.unit.days", value: "Days", comment: "")
        }
    }

    /// Convert the value in this unit to seconds for date calculations
    /// - Parameter value: The number of units
    /// - Returns: The equivalent duration in seconds
    func toSeconds(_ value: Int) -> TimeInterval {
        switch self {
        case .minutes:
            return TimeInterval(value * 60)
        case .hours:
            return TimeInterval(value * 60 * 60)
        case .days:
            return TimeInterval(value * 60 * 60 * 24)
        }
    }
}

// MARK: - UserDefaults Keys

/// The `UserDefaults.standard` keys behind the auto-delete settings.
///
/// Shared by the `@AppStorage` properties below and by the plain `nonisolated`
/// readers the cleanup timer uses, so the two can never read different keys.
/// Deliberately not main-actor isolated.
enum AutoDeleteDefaultsKey {
    static let enabled = "autoDeleteEnabled"
    static let timeUnit = "autoDeleteTimeUnit"
    static let value = "autoDeleteValue"
}

// MARK: - Auto-Delete Settings Manager

/// Manages automatic deletion settings for recordings and transcripts
///
/// SETTINGS STORED:
/// - autoDeleteEnabled: Whether auto-delete is active (default: false)
/// - autoDeleteTimeUnit: The unit of time (minutes, hours, days) - default: days
/// - autoDeleteValue: How many units before deletion (default: 30)
///
/// EXAMPLE CONFIGURATIONS:
/// - Delete after 30 days: enabled=true, unit=days, value=30
/// - Delete after 24 hours: enabled=true, unit=hours, value=24
/// - Delete after 60 minutes: enabled=true, unit=minutes, value=60
@MainActor
class AutoDeleteSettingsManager: ObservableObject {

    // MARK: - Logger

    /// Logger for auto-delete settings operations
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "AutoDeleteSettings")

    // MARK: - Cleanup Service Reference

    /// Reference to the cleanup service for accessing next run time
    /// Set by hyperwhisperApp after initializing the cleanup service
    /// Weak to avoid retain cycle (cleanup service holds reference to this manager)
    weak var cleanupService: AutoDeleteCleanupService?

    // MARK: - Stored Settings

    // NOTE ON @AppStorage AND ObservableObject:
    // @AppStorage does NOT automatically trigger objectWillChange.send() like @Published does.
    // To ensure SwiftUI views properly update when these values change, we wrap @AppStorage
    // properties in computed properties that explicitly call objectWillChange.send().
    // Without this, toggling autoDeleteEnabled or changing autoDeleteValue would NOT
    // trigger UI updates in observers (e.g., the modal's conditional sections or HistoryView's icon).

    /// Private storage for auto-delete enabled state
    /// Exposed via computed property to ensure SwiftUI observer updates
    @AppStorage(AutoDeleteDefaultsKey.enabled) private var autoDeleteEnabledStorage: Bool = false

    /// Whether automatic deletion of old recordings is enabled
    /// Default: false (recordings are kept indefinitely)
    ///
    /// IMPORTANT: This computed property wraps @AppStorage to ensure objectWillChange is called,
    /// which triggers SwiftUI view updates. Direct @AppStorage doesn't integrate with ObservableObject.
    var autoDeleteEnabled: Bool {
        get { autoDeleteEnabledStorage }
        set {
            if autoDeleteEnabledStorage != newValue {
                objectWillChange.send()
                autoDeleteEnabledStorage = newValue
            }
        }
    }

    /// The time unit for auto-delete duration
    /// Stored as raw string value for stability across app versions
    /// Default: days
    @AppStorage(AutoDeleteDefaultsKey.timeUnit) private var autoDeleteTimeUnitRaw: String = AutoDeleteTimeUnit.days.rawValue

    /// Private storage for auto-delete value
    /// Exposed via computed property to ensure SwiftUI observer updates
    @AppStorage(AutoDeleteDefaultsKey.value) private var autoDeleteValueStorage: Int = 30

    /// The number of time units before a recording is deleted
    /// Must be a positive integer (minimum 1)
    /// Default: 30 (when combined with days unit = 30 days)
    ///
    /// IMPORTANT: This computed property wraps @AppStorage to ensure objectWillChange is called,
    /// which triggers SwiftUI view updates. Direct @AppStorage doesn't integrate with ObservableObject.
    var autoDeleteValue: Int {
        get { autoDeleteValueStorage }
        set {
            let clamped = max(1, newValue)
            if autoDeleteValueStorage != clamped {
                objectWillChange.send()
                autoDeleteValueStorage = clamped
            }
        }
    }

    // MARK: - Computed Properties

    /// The time unit for auto-delete duration
    /// Provides type-safe access to the stored raw value
    ///
    /// IMPORTANT: Calls objectWillChange.send() to ensure SwiftUI observers update.
    var autoDeleteTimeUnit: AutoDeleteTimeUnit {
        get {
            AutoDeleteTimeUnit(rawValue: autoDeleteTimeUnitRaw) ?? .days
        }
        set {
            if autoDeleteTimeUnitRaw != newValue.rawValue {
                objectWillChange.send()
                autoDeleteTimeUnitRaw = newValue.rawValue
            }
        }
    }

    /// Calculates the cutoff date for deletion based on current settings
    /// Recordings older than this date should be deleted
    ///
    /// - Returns: The cutoff Date, or nil if auto-delete is disabled
    var deletionCutoffDate: Date? {
        guard autoDeleteEnabled else { return nil }
        guard autoDeleteValue > 0 else { return nil }

        let secondsAgo = autoDeleteTimeUnit.toSeconds(autoDeleteValue)
        return Date().addingTimeInterval(-secondsAgo)
    }

    // MARK: - Off-Main-Actor Reads (HYPERWHISPER-Y0, #880)

    // The cleanup timer must not read these settings through @AppStorage on the
    // main actor: that getter enters SwiftUI's UserDefaultLocation and then a
    // synchronous cfprefsd round trip, and a slow cfprefsd froze the app there
    // once a minute. These readers go straight to UserDefaults, hold no
    // main-actor state, and are called from a background queue. They read the
    // live value on every call (nothing is cached), so a toggle in Settings is
    // seen by the very next tick. The @AppStorage properties above stay for the
    // views, which need their objectWillChange.

    /// Reads the auto-delete flag exactly as `@AppStorage(AutoDeleteDefaultsKey.enabled)`
    /// with a default of `false` does: a stored Bool, else the default.
    nonisolated static func isAutoDeleteEnabled(in defaults: UserDefaults) -> Bool {
        (defaults.object(forKey: AutoDeleteDefaultsKey.enabled) as? Bool) ?? false
    }

    /// Reads the time unit exactly as `autoDeleteTimeUnit`'s getter does: the
    /// stored raw String (default `days`), and `.days` for an unknown raw value.
    nonisolated static func autoDeleteTimeUnit(in defaults: UserDefaults) -> AutoDeleteTimeUnit {
        let raw = (defaults.object(forKey: AutoDeleteDefaultsKey.timeUnit) as? String)
            ?? AutoDeleteTimeUnit.days.rawValue
        return AutoDeleteTimeUnit(rawValue: raw) ?? .days
    }

    /// The live auto-delete flag from `UserDefaults.standard`, the store the
    /// `@AppStorage` property uses. Safe to call off the main actor.
    nonisolated func autoDeleteEnabledFromDefaults() -> Bool {
        Self.isAutoDeleteEnabled(in: .standard)
    }

    /// The live time unit from `UserDefaults.standard`. Safe to call off the
    /// main actor.
    nonisolated func autoDeleteTimeUnitFromDefaults() -> AutoDeleteTimeUnit {
        Self.autoDeleteTimeUnit(in: .standard)
    }

    // MARK: - Initialization

    init() {
        // Validate stored value is positive
        if autoDeleteValue < 1 {
            autoDeleteValue = 30
            logger.warning("Auto-delete value was invalid, reset to 30")
        }
    }

    // MARK: - Public Methods

    /// Validates and sets the auto-delete value
    /// Ensures the value is at least 1
    ///
    /// - Parameter value: The new value to set
    func setAutoDeleteValue(_ value: Int) {
        autoDeleteValue = max(1, value)
        logger.info("Auto-delete value set to \(self.autoDeleteValue, privacy: .public) \(self.autoDeleteTimeUnit.rawValue, privacy: .public)")
    }

}
