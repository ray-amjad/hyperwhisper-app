//
//  LatencyOptOut.swift
//  hyperwhisper
//
//  ANONYMOUS SPEED DATA — THE OPT-OUT HEADER
//
//  HyperWhisper Cloud times each provider call and publishes the aggregate at
//  hyperwhisper.com/en/latency, so people can see which provider is actually
//  fastest from their part of the world before they pick one.
//
//  A user who does not want to be part of that turns off "Share anonymous speed
//  data" in Settings → General. This type turns that setting into the header the
//  backend reads (`hyperwhisper-cloud/src/routes/transcribe.ts` → isLatencyOptOut).
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

    /// Header name agreed with the backend. Sent only when the user opted out —
    /// there is no "yes please" header, because sharing is the default.
    static let headerName = "X-Latency-Opt-Out"

    /// True when the user turned sharing off.
    ///
    /// Absent from UserDefaults means the default (sharing on), which is what
    /// `registerHyperWhisperDefaults()` registers.
    static var isEnabled: Bool {
        guard UserDefaults.standard.object(forKey: settingKey) != nil else { return false }
        return !UserDefaults.standard.bool(forKey: settingKey)
    }

    /// Appends the opt-out header to a core-built request when, and only when,
    /// the user asked to be left out.
    static func apply(to request: inout HttpRequest) {
        guard isEnabled else { return }
        request.headers.append(Header(name: headerName, value: "1"))
    }
}
