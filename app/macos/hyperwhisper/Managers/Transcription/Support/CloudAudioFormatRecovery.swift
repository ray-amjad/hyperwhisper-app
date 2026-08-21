//
//  CloudAudioFormatRecovery.swift
//  hyperwhisper
//
//  CLIENT HALF OF THE "RE-ENCODE AND RETRY" CONTRACT
//
//  Some upstreams behind HyperWhisper Cloud accept only uncompressed audio.
//  `azure-mai` (MAI-Transcribe 1.5) documents wav/mp3/flac only, so an .m4a
//  upload is rejected with a typed HTTP 415 — deliberately, so the CLIENT can
//  re-encode and retry instead of the request falling through a fallback chain
//  (see hyperwhisper-cloud/src/providers/types.ts `UnsupportedAudioFormatError`
//  and hyperwhisper-cloud/src/providers/azure-mai.ts).
//
//  The macOS recording pipeline stores finalized audio as .m4a by default
//  (`storeAsM4A`), and the file-import flow re-encodes video to .m4a, so any
//  mode on an azure-mai-backed accuracy tier hits that 415 on every upload.
//  This helper closes the loop: on a 415, decode the clip to a 16 kHz mono WAV
//  and send it again — ONCE.
//
//  DESIGN NOTES
//  - Scoped to the two HyperWhisper Cloud transcribe call sites
//    (`HyperWhisperCloudProvider` and `HyperWhisperRoutedTranscription`), NOT to
//    `RustRetry.perform`. Eleven BYOK providers share `RustRetry` but not this
//    endpoint, and they all accept m4a natively.
//  - No new `TranscriptionError` case. When recovery is impossible the ORIGINAL
//    server error is rethrown unchanged, so error classification, the Sentry
//    `errorCode` positional index, and every exhaustive switch stay untouched.
//    Diagnostics ride on a breadcrumb instead.
//  - The temp-file / size / delete / send boundaries are injected closures so
//    the retry-once contract and the cleanup-on-every-path guarantee are
//    testable with zero disk I/O, zero network and zero audio.
//

import Foundation

/// Namespace for the HTTP-415 "unsupported audio format" recovery used by the
/// HyperWhisper Cloud transcribe paths.
enum CloudAudioFormatRecovery {

    // MARK: - Constants

    /// Content-Type sent with the re-encoded upload.
    ///
    /// Passed EXPLICITLY rather than re-inferred: `AudioMimeTypeResolver.infer`
    /// consults `URLResourceKey.contentTypeKey` first, so a real .wav on disk can
    /// resolve to `audio/vnd.wave` depending on UTType behaviour. The server's
    /// format gate matches on the substring `wav` so both clear it, but pinning
    /// the value here keeps the retry independent of UTType.
    static let wavContentType = "audio/wav"

    /// Upper bound for the re-encoded upload.
    ///
    /// Mirrors `AZURE_MAI_MAX_BYTES` (hyperwhisper-cloud/src/lib/constants.ts) —
    /// 300 MB. DRIFT RISK: this is a hand-copied mirror of a server constant. If
    /// the server raises or lowers its cap, this value must follow, or an upload
    /// that the server would accept gets skipped here (or one it rejects gets
    /// sent). The server checks its byte cap BEFORE its format gate, so an
    /// oversized WAV would come back as a 413 rather than a 415 — which is why
    /// the size check has to happen before the retry, not after it.
    static let maxReencodedUploadBytes: Int64 = 300 * 1024 * 1024

    /// HTTP status the backend uses for "this upstream cannot read that format".
    private static let unsupportedMediaTypeStatus = 415

    // MARK: - Eligibility

    /// True when `error` is the backend's typed "unsupported audio format" 415
    /// AND the file we sent was not already a WAV.
    ///
    /// The already-WAV guard does double duty: it skips a pointless re-encode,
    /// and it makes a retry loop structurally impossible — the retry always
    /// uploads a `.wav`, so a second 415 can never be eligible again even if a
    /// caller nested this helper by mistake.
    static func shouldReencodeToWAV(after error: Error, sourceURL: URL) -> Bool {
        guard let transcriptionError = error as? TranscriptionError else {
            return false
        }
        guard case .serverError(let statusCode, _) = transcriptionError,
              statusCode == unsupportedMediaTypeStatus else {
            return false
        }
        return sourceURL.pathExtension.lowercased() != "wav"
    }

    // MARK: - Recovery

    /// Sends `sourceURL`, and on a 415 re-encodes it to WAV and sends it exactly
    /// one more time.
    ///
    /// - Parameters:
    ///   - sourceURL: The audio file the caller wants transcribed.
    ///   - reencode: `(source, destination)` — writes a WAV re-encode of `source`
    ///     to `destination`. Throws on failure.
    ///   - makeTempURL: Reserves the destination path for the re-encode.
    ///   - fileSize: Size of the re-encoded file in bytes, `nil` if unknown.
    ///   - removeItem: Deletes the temp file. Called on EVERY exit path.
    ///   - send: `(uploadURL, contentTypeOverride)` — performs the upload.
    ///     `contentTypeOverride` is `nil` for the original attempt (the caller
    ///     keeps whatever Content-Type it already resolved) and
    ///     `wavContentType` for the retry.
    ///
    /// - Returns: Whatever `send` returned, from whichever attempt succeeded.
    ///
    /// - Throws: On an ineligible failure, the original error UNCHANGED. When the
    ///   re-encode fails or the result is too large, the ORIGINAL 415 — never the
    ///   converter's `AudioError.exportFailed`, and never a synthesized 413. When
    ///   the retry itself fails, the retry's own error (freshest verdict, same as
    ///   `performTranscribeRequestWithLicenseRecovery`). Cancellation outranks all
    ///   of that: a task cancelled on the way to any "recovery impossible" exit
    ///   throws `CancellationError`, never the 415 — the converter reports a
    ///   cancelled decode as its own failure rather than as `CancellationError`,
    ///   so a silent stop must not surface as an unsupported-format toast.
    static func withUnsupportedFormatRecovery<T>(
        sourceURL: URL,
        reencode: (URL, URL) async throws -> Void,
        makeTempURL: () -> URL = { CloudAudioFormatRecovery.makeTemporaryWAVURL() },
        fileSize: (URL) -> Int64? = { CloudAudioFormatRecovery.fileSizeInBytes(of: $0) },
        removeItem: (URL) -> Void = { CloudAudioFormatRecovery.deleteFile(at: $0) },
        send: (URL, String?) async throws -> T
    ) async throws -> T {
        let originalError: Error
        do {
            return try await send(sourceURL, nil)
        } catch {
            originalError = error
        }

        guard shouldReencodeToWAV(after: originalError, sourceURL: sourceURL) else {
            throw originalError
        }

        let sourceExtension = displayExtension(of: sourceURL)
        AppLogger.network.warning(
            "Cloud transcribe rejected the audio format · status=415 · sourceExtension=\(sourceExtension, privacy: .public) · re-encoding to WAV and retrying once"
        )

        // Reserve the temp path and register cleanup BEFORE the re-encode, so a
        // mid-write throw (or a cancellation) can never leak a partial file.
        // Mirrors the temp-envelope convention in RustHTTPExecutor.
        let tempURL = makeTempURL()
        defer { removeItem(tempURL) }

        // Cancellation must stay cancellation — never be reported as a 415.
        try Task.checkCancellation()

        do {
            try await reencode(sourceURL, tempURL)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            // A cancelled re-encode usually surfaces as the converter's own
            // failure (AVFoundation tears the reader down, it does not throw
            // `CancellationError`), so the typed catch above misses it. Re-check
            // the task before falling back to the 415 — otherwise stopping a
            // recording shows the user an "unsupported audio format" toast.
            try Task.checkCancellation()
            // Swallow the converter's error on purpose: the user-visible failure
            // is the server's rejection, and surfacing `AudioError.exportFailed`
            // here would show an unrelated export message for a network verdict.
            AppLogger.network.warning(
                "Cloud audio WAV re-encode failed · sourceExtension=\(sourceExtension, privacy: .public) · preserving the original 415"
            )
            recordOutcome(
                sourceExtension: sourceExtension,
                reencodeSucceeded: false,
                reencodedBytes: nil,
                retryStatus: "not_attempted_reencode_failed"
            )
            throw originalError
        }

        guard let reencodedBytes = fileSize(tempURL) else {
            // A cancellation mid-encode leaves a truncated or absent temp file,
            // which lands here — report it as the cancellation it is.
            try Task.checkCancellation()
            AppLogger.network.warning(
                "Cloud audio WAV re-encode produced an unreadable file · sourceExtension=\(sourceExtension, privacy: .public) · preserving the original 415"
            )
            recordOutcome(
                sourceExtension: sourceExtension,
                reencodeSucceeded: true,
                reencodedBytes: nil,
                retryStatus: "not_attempted_size_unknown"
            )
            throw originalError
        }

        guard reencodedBytes <= maxReencodedUploadBytes else {
            // Cancellation outranks the size verdict: a user who stopped the
            // transcription must not be told the audio was too large.
            try Task.checkCancellation()
            // Uploading this would earn a 413 instead of the 415, which is a
            // worse error for the same underlying problem. Keep the 415.
            AppLogger.network.warning(
                "Cloud audio WAV re-encode exceeds the upload cap · bytes=\(reencodedBytes, privacy: .public) · capBytes=\(Self.maxReencodedUploadBytes, privacy: .public) · preserving the original 415"
            )
            recordOutcome(
                sourceExtension: sourceExtension,
                reencodeSucceeded: true,
                reencodedBytes: reencodedBytes,
                retryStatus: "not_attempted_over_cap"
            )
            throw originalError
        }

        try Task.checkCancellation()

        do {
            let result = try await send(tempURL, wavContentType)
            recordOutcome(
                sourceExtension: sourceExtension,
                reencodeSucceeded: true,
                reencodedBytes: reencodedBytes,
                retryStatus: "success"
            )
            return result
        } catch {
            recordOutcome(
                sourceExtension: sourceExtension,
                reencodeSucceeded: true,
                reencodedBytes: reencodedBytes,
                retryStatus: "failed"
            )
            throw error
        }
    }

    // MARK: - Default boundaries

    /// Decodes any supported container to a 16 kHz / mono / 16-bit LPCM WAV.
    /// The single place this recovery path depends on `AudioFileConverter`.
    static func reencodeToWAV(source: URL, destination: URL) async throws {
        _ = try await AudioFileConverter().convertAudioToWAV(from: source, to: destination)
    }

    /// Temp destination for the re-encode. Follows the `hw-…-<uuid>` convention
    /// used by `RustHTTPExecutor` for its multipart envelope.
    static func makeTemporaryWAVURL() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("hw-reencode-\(UUID().uuidString).wav")
    }

    /// Size in bytes, or `nil` when the file is missing/unreadable.
    static func fileSizeInBytes(of url: URL) -> Int64? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? Int64 else {
            return nil
        }
        return size
    }

    /// Best-effort delete — a leftover temp file is not worth failing an upload.
    static func deleteFile(at url: URL) {
        try? FileManager.default.removeItem(at: url)
    }

    // MARK: - Diagnostics

    /// Lowercased file extension, or `"none"`. EXTENSION ONLY — never a path or
    /// a file name: this code path has already leaked a real user home directory
    /// into an error report once.
    private static func displayExtension(of url: URL) -> String {
        let ext = url.pathExtension.lowercased()
        return ext.isEmpty ? "none" : ext
    }

    /// One breadcrumb per recovery attempt, so a retry that still fails is
    /// distinguishable from the original failure in the issue's event data
    /// without adding a `TranscriptionError` case.
    private static func recordOutcome(
        sourceExtension: String,
        reencodeSucceeded: Bool,
        reencodedBytes: Int64?,
        retryStatus: String
    ) {
        // Privacy-aware interpolation can't nest a `??`/ternary — resolve first.
        let bytesText = reencodedBytes.map { String($0) } ?? "unknown"
        AppLogger.network.warning(
            "Cloud audio format recovery outcome · sourceExtension=\(sourceExtension, privacy: .public) · reencodeSucceeded=\(reencodeSucceeded, privacy: .public) · reencodedBytes=\(bytesText, privacy: .public) · retryStatus=\(retryStatus, privacy: .public)"
        )
        SentryService.addBreadcrumb(
            message: "cloud_reencode_wav_retry",
            category: "transcription.format_recovery",
            data: [
                "sourceExtension": sourceExtension,
                "reencodeSucceeded": reencodeSucceeded,
                "reencodedBytes": reencodedBytes ?? -1,
                "retryStatus": retryStatus
            ]
        )
    }
}
