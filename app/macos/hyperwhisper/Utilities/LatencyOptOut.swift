//
//  LatencyOptOut.swift
//  hyperwhisper
//
//  ANONYMOUS SPEED DATA — READING THE SETTING
//
//  HyperWhisper Cloud times each provider call and publishes the aggregate at
//  hyperwhisper.com/en/latency, so people can see which provider is actually
//  fastest from their part of the world before they pick one.
//
//  A user who does not want to be part of that turns off "Share anonymous speed
//  data" in Settings → General. This type is only the *read* of that setting.
//  The header itself (`X-Latency-Opt-Out`, see `LATENCY_OPT_OUT_HEADER` in
//  hw-net's `hyperwhisper_cloud.rs`) is built by the Rust core from
//  `TranscribeParams.shareAnonymousSpeedData`, so every routed provider gets it
//  and no direct-vendor request can carry it. Pass `!LatencyOptOut.isEnabled`
//  into `RustCoreMapping.transcribeParams(...)`; do not append a header here.
//
//  Reads UserDefaults directly rather than SettingsManager: SettingsManager is
//  @MainActor and the transcription path builds requests off the main actor.
//  UserDefaults is thread-safe and holds the same value the @AppStorage property
//  writes.
//

import Foundation

enum LatencyOptOut {
    /// The UserDefaults key behind `GeneralSettingsManager.shareAnonymousSpeedData`.
    static let settingKey = "shareAnonymousSpeedData"

    /// True when the user turned sharing off.
    ///
    /// Absent from UserDefaults means the default (sharing on), which is what
    /// `registerHyperWhisperDefaults()` registers.
    ///
    /// Note the inversion at the call sites: this is the *opt-out*, so the core
    /// wants `shareAnonymousSpeedData: !LatencyOptOut.isEnabled`.
    static var isEnabled: Bool {
        guard UserDefaults.standard.object(forKey: settingKey) != nil else { return false }
        return !UserDefaults.standard.bool(forKey: settingKey)
    }
}
