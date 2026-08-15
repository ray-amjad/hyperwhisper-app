//
//  HyperWhisperCloudEntitlement.swift
//  hyperwhisper
//
//  Client-side pre-check for the HyperWhisper Cloud transcribe path.
//

import Foundation

/// Fail-fast pre-check for provider paths that terminate at the HyperWhisper
/// Cloud transcribe backend.
///
/// The backend dropped guest / device-credit auth: its auth middleware reads
/// only `account_key` / `license_key` and rejects everything else with a 401
/// before it reads the request body. So an unlicensed upload is guaranteed to
/// fail — but only *after* the client has pushed the audio over the network.
/// The Rust core then maps that 401 to a terminal
/// `TranscriptionError.unauthorized`, which is reported to Sentry as a
/// production error and tells the user to "Open Settings → API Keys" for a
/// provider that has no user-supplied API key. (Sentry HYPERWHISPER-T2.)
///
/// This check refuses that request locally, up front, with a `TranscriptionError`
/// that names the real remedy.
///
/// **What it actually saves.** The network round-trip and the upload — not the
/// audio preprocessing. The guard sits inside the provider, and by the time a
/// provider is called the audio has already been prepared: the recording flow
/// runs VAD trimming before `transcribeWithDetails`
/// (`RecordingTranscriptionFlow+StopRecording.swift`), and the file-import path
/// re-encodes first. Moving the check ahead of that work would mean a preflight
/// in the recording flow itself, which is a deliberate follow-up, not something
/// this type does today.
///
/// **Fail-closed only.** This type can only *refuse* a request; it can never
/// grant one. The server remains the sole authority on entitlement — a `true`
/// here just means "the client has something worth sending", and the backend
/// still validates the key. `.unauthorized` deliberately stays a reported Sentry
/// error, because a 401 on a request we believed *was* licensed is a genuine
/// signal (e.g. a stale server-side licence cache), not user error.
///
/// Mirrors the Windows implementation in
/// `app/windows/HyperWhisper/Services/HyperWhisperCloudService.cs` and
/// `HyperWhisperRoutedTranscriptionClient.cs`.
enum HyperWhisperCloudEntitlement {

    /// Throws `TranscriptionError.cloudAccountRequired` when the caller holds no
    /// account key.
    ///
    /// Takes a plain `Bool` rather than the `LicenseManager` itself: the manager
    /// is `@MainActor`, and the call sites have already awaited
    /// `getTranscriptionIdentifier()` off the main actor by the time they get
    /// here. Keeping this synchronous and dependency-free also makes it directly
    /// unit-testable.
    ///
    /// - Parameters:
    ///   - isLicensed: Second element of `LicenseManager.getTranscriptionIdentifier()`.
    ///     `false` means the identifier is a device ID, which the backend no
    ///     longer accepts.
    ///   - provider: Display name used in the thrown error, e.g. "HyperWhisper Cloud".
    static func requireLicense(isLicensed: Bool, provider: String) throws {
        guard isLicensed else {
            throw TranscriptionError.cloudAccountRequired(provider: provider)
        }
    }
}
