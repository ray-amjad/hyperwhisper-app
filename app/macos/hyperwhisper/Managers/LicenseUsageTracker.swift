//
//  LicenseUsageTracker.swift
//  hyperwhisper
//
//  LICENSE USAGE TRACKING
//  Tracks daily transcription time and model downloads for trial users.
//
//  TRIAL LIMITS:
//  - Daily transcription: 300 seconds (5 minutes) in production, 1800 seconds (30 minutes) in dev
//  - Model downloads: 3 models maximum
//
//  LICENSED USERS:
//  - No limits enforced (Int.max for all checks)
//
//  STORAGE (M3-C):
//  - All usage/limit LOGIC now lives in the Rust shared core (`hw-license`).
//  - State is persisted in UserDefaults via a shared `RustLicenseStore`
//    (`com.hyperwhisper.usage.*`), seeded once from the legacy Core Data
//    `UsageTracking` entity (see RustLicenseStore.swift).
//  - The day-boundary reset is READ-TIME in the core (no midnight timer needed);
//    `now` is passed offset to the local day so the UTC bucket lands on local
//    midnight, preserving the old `isDateInToday` behavior.
//
//  INTEGRATION:
//  - Called by AudioRecordingManager after each transcription
//  - Called by WhisperModelManager after model downloads
//  - Queried by UI to show remaining time/downloads
//

import Foundation
import SwiftUI
import AppKit

/// Tracks usage limits for trial users.
///
/// This class is a thin macOS shim over the Rust license core: it owns the
/// `@Published` UI state and forwards every decision (record/check/can-start) to
/// the core over the shared `RustLicenseStore`.
///
/// Licensed users bypass all limits and get unlimited access.
@MainActor
class LicenseUsageTracker: ObservableObject {

    // MARK: - Published Properties (for UI binding)

    /// Daily transcription usage in seconds (driven by the core's UsageSnapshot).
    @Published var dailyUsageSeconds: Int = 0

    /// Number of models downloaded by the user. Lifetime count, never resets.
    @Published var modelsDownloaded: Int = 0

    /// Whether the daily limit has been reached. true = cannot start recordings.
    @Published var isDailyLimitReached: Bool = false

    /// Whether the model download limit has been reached. true = cannot download.
    @Published var isModelLimitReached: Bool = false

    // MARK: - Limits (effective trial limits in force)

    /// Effective daily trial limit (defaults overlaid with a fresh remote
    /// override). Exposed for UI display. Computed from the core, not hardcoded.
    private(set) var trialDailyLimitSeconds: Int

    /// Effective trial model-download limit. Computed from the core.
    private(set) var trialModelLimit: Int

    // MARK: - Properties

    /// Shared key-value store backing the Rust license core.
    private let store: RustLicenseStore

    /// Current license status (determines if limits apply).
    private var licenseStatus: LicenseStatus = .trial

    /// `true` for DEBUG builds — drives the core's `licenseLimitsDefaults`.
    /// Passed in (not read with `#if DEBUG` here) so the default limit values
    /// come from the core, never hardcoded.
    private let debugBuild: Bool

    /// Token for the system wake observer, removed on deinit.
    /// Kept ONLY as a UI refresh trigger (re-reads the core snapshot). The
    /// midnight reset is now read-time in the core, so no reset timer is needed.
    private var wakeObserver: NSObjectProtocol?

    // MARK: - Public API (Read-only limits)

    /// Exposes the trial transcription limit for UI display.
    var trialDailyTranscriptionLimit: Int { trialDailyLimitSeconds }

    /// Exposes the trial model download limit for UI display.
    var trialModelDownloadLimit: Int { trialModelLimit }

    // MARK: - Initialization

    init(store: RustLicenseStore) {
        self.store = store
        #if DEBUG
        self.debugBuild = true
        #else
        self.debugBuild = false
        #endif

        // Seed effective limits from the core's defaults for this build flavor.
        let defaults = licenseLimitsDefaults(debugBuild: debugBuild)
        self.trialDailyLimitSeconds = Int(defaults.dailySeconds)
        self.trialModelLimit = Int(defaults.modelDownloads)

        // Refresh UI state when the Mac wakes from sleep. The core resets the
        // daily bucket at read time, so this is purely a UI re-read — no reset
        // timer is scheduled anymore.
        setupWakeObserver()
    }

    deinit {
        if let wakeObserver {
            NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver)
        }
    }

    // MARK: - Effective limits

    /// The effective `Limits` in force right now: build-flavor defaults overlaid
    /// with a fresh remote override (if present and within the 24h TTL). This is
    /// the single source of truth passed to every core usage call.
    private func effectiveLimits() -> Limits {
        let defaults = licenseLimitsDefaults(debugBuild: debugBuild)
        if let override = licenseRemoteOverrideIfFresh(
            store: store,
            nowUnixSecs: RustLicenseTime.nowUTC()
        ) {
            return Limits(
                dailySeconds: override.dailySeconds,
                modelDownloads: override.modelDownloads
            )
        }
        return defaults
    }

    // MARK: - Remote Config Updates

    /// Applies freshly-fetched remote trial limits: persists them to the core's
    /// store (so they survive restarts and feed `effectiveLimits()`), updates the
    /// displayed limits, and recomputes the published flags.
    /// Called by LicenseManager after fetching remote config.
    func updateTrialLimits(dailySeconds: Int, modelLimit: Int, maxAgeSecs: Int? = nil, isLiveFetch: Bool = false) {
        licenseStoreRemoteOverride(
            store: store,
            limits: TrialLimits(
                dailySeconds: Int64(dailySeconds),
                modelDownloads: Int64(modelLimit)
            ),
            nowUnixSecs: RustLicenseTime.nowUTC()
        )
        // B4/E5: persist the server Cache-Control max-age so the core's freshness
        // check (`licenseRemoteOverrideIfFresh`) honors it instead of the fixed
        // default. Key must match hw_license::cache's K_OVERRIDE_MAX_AGE.
        //
        // Only a LIVE fetch is authoritative for the TTL. On a live fetch with a
        // positive max-age we store it; with no/≤0 max-age we CLEAR the key (the
        // core treats empty as "use the 6h default") so a previously-stored
        // max-age can't persist after the server stops sending one. The
        // cached-replay path (isLiveFetch == false) leaves the stored value
        // untouched — it isn't a fresh signal from the server.
        if isLiveFetch {
            if let maxAgeSecs, maxAgeSecs > 0 {
                store.set(key: "com.hyperwhisper.config.maxAgeSecs", value: String(maxAgeSecs))
            } else {
                store.set(key: "com.hyperwhisper.config.maxAgeSecs", value: "")
            }
        }
        trialDailyLimitSeconds = dailySeconds
        trialModelLimit = modelLimit
        refreshFromSnapshot()
    }

    // MARK: - License Status Updates

    /// Updates the license status and recomputes the published flags.
    func updateLicenseStatus(_ status: LicenseStatus) {
        licenseStatus = status
        refreshFromSnapshot()
    }

    // MARK: - Recording Limits

    /// Checks if the user can start recording based on the daily limit.
    /// Delegates to the core (which day-resets at read time).
    func canStartRecording() -> Bool {
        let allowed = licenseCanStartRecording(
            store: store,
            status: LicenseNetworkService.toCore(licenseStatus),
            limits: effectiveLimits(),
            nowUnixSecs: RustLicenseTime.nowLocal()
        )
        // Keep the published flags consistent with what we just decided.
        refreshFromSnapshot()
        return allowed
    }

    /// Records transcription time against today's bucket, then refreshes the UI.
    func recordTranscriptionTime(_ seconds: Int) async {
        licenseRecordUsage(
            store: store,
            seconds: Int64(seconds),
            nowUnixSecs: RustLicenseTime.nowLocal()
        )
        refreshFromSnapshot()
    }

    /// Remaining daily transcription time in seconds (Int.max for licensed users).
    func getRemainingDailyTime() -> Int {
        let snap = currentSnapshot()
        return clampRemaining(snap.remainingDailySeconds)
    }

    // MARK: - Model Download Limits

    /// Checks if the user can download another model. Delegates to the core.
    func canDownloadModel() -> Bool {
        licenseCanDownloadModel(
            store: store,
            status: LicenseNetworkService.toCore(licenseStatus),
            limits: effectiveLimits()
        )
    }

    /// Increments the lifetime model download count, then refreshes the UI.
    func incrementModelDownloadCount() async {
        licenseRecordModelDownload(store: store)
        refreshFromSnapshot()
    }

    /// Remaining model downloads (Int.max for licensed users).
    func getRemainingModelDownloads() -> Int {
        let snap = currentSnapshot()
        return clampRemaining(snap.remainingModelDownloads)
    }

    // MARK: - Snapshot refresh

    /// Reads the authoritative usage snapshot from the core. The core performs
    /// the read-time day-boundary reset, so this always reflects today.
    private func currentSnapshot() -> UsageSnapshot {
        licenseCheckLimits(
            store: store,
            status: LicenseNetworkService.toCore(licenseStatus),
            limits: effectiveLimits(),
            nowUnixSecs: RustLicenseTime.nowLocal()
        )
    }

    /// Drives every `@Published` property from a fresh core snapshot.
    private func refreshFromSnapshot() {
        let snap = currentSnapshot()
        dailyUsageSeconds = Int(snap.dailySecondsUsed)
        modelsDownloaded = Int(snap.modelsDownloaded)
        isDailyLimitReached = snap.dailyLimitReached
        isModelLimitReached = snap.modelLimitReached
    }

    /// Refreshes usage statistics from the core (called on app launch / wake).
    /// Replaces the old Core Data reload; the core is now the source of truth.
    func refreshUsageStats() async {
        refreshFromSnapshot()
    }

    /// Maps the core's `i64::MAX` "unlimited" sentinel to Swift `Int.max`.
    private func clampRemaining(_ value: Int64) -> Int {
        value == Int64.max ? Int.max : Int(value)
    }

    // MARK: - Wake observer (UI refresh only)

    /// Observes system wake to refresh the UI snapshot after sleep.
    ///
    /// The core resets the daily bucket at read time, so a Mac that sleeps across
    /// midnight surfaces a fresh counter on the next read automatically. This
    /// observer just nudges the published state so the UI updates without a user
    /// action. No reset timer is scheduled.
    private func setupWakeObserver() {
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.refreshFromSnapshot()
            }
        }
    }
}
