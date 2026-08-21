//
//  CloudAudioFormatRecoveryTests.swift
//  hyperwhisperTests
//
//  Covers the HTTP-415 "re-encode to WAV and retry once" recovery shared by the
//  two HyperWhisper Cloud transcribe paths.
//
//  Every boundary the recovery touches (temp-file reservation, size lookup,
//  deletion, the upload itself, the re-encode) is injected, so these tests need
//  no disk, no network, no audio hardware and no licence — entitlement stays
//  enforced server-side and is never simulated here.
//

import Foundation
import Testing
@testable import HyperWhisper

struct CloudAudioFormatRecoveryTests {

    // Synthetic paths — nothing is ever read or written, since every filesystem
    // boundary is injected. Never a real user path.
    private static let m4aSource = URL(fileURLWithPath: "/tmp/hw-tests/recording.m4a")
    private static let wavSource = URL(fileURLWithPath: "/tmp/hw-tests/recording.wav")
    private static let tempWAV = URL(fileURLWithPath: "/tmp/hw-tests/hw-reencode-test.wav")

    private static let unsupportedFormat = TranscriptionError.serverError(
        statusCode: 415,
        message: "Unsupported audio format"
    )

    // MARK: - Eligibility

    @Test func eligibilityRequiresA415OnANonWavFile() {
        #expect(CloudAudioFormatRecovery.shouldReencodeToWAV(
            after: Self.unsupportedFormat,
            sourceURL: Self.m4aSource
        ))
        // Already a WAV — re-encoding would be pointless, and refusing here is
        // what makes a retry loop structurally impossible.
        #expect(!CloudAudioFormatRecovery.shouldReencodeToWAV(
            after: Self.unsupportedFormat,
            sourceURL: Self.wavSource
        ))
        #expect(!CloudAudioFormatRecovery.shouldReencodeToWAV(
            after: Self.unsupportedFormat,
            sourceURL: URL(fileURLWithPath: "/tmp/hw-tests/RECORDING.WAV")
        ))
        #expect(!CloudAudioFormatRecovery.shouldReencodeToWAV(
            after: TranscriptionError.serverError(statusCode: 500, message: "boom"),
            sourceURL: Self.m4aSource
        ))
        #expect(!CloudAudioFormatRecovery.shouldReencodeToWAV(
            after: TranscriptionError.audioFileTooLarge(fileSize: 1, limit: 1, providerName: "HyperWhisper Cloud"),
            sourceURL: Self.m4aSource
        ))
        #expect(!CloudAudioFormatRecovery.shouldReencodeToWAV(
            after: CancellationError(),
            sourceURL: Self.m4aSource
        ))
    }

    // MARK: - Happy path

    @Test func reencodesToWAVAndRetriesOnceAfter415() async throws {
        let recorder = FormatRecoveryRecorder()

        let transcript = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
            sourceURL: Self.m4aSource,
            reencode: { source, destination in
                recorder.recordReencode(source: source, destination: destination)
            },
            makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
            fileSize: { _ in 64 * 1024 },
            removeItem: { recorder.recordRemoval($0) },
            send: { (uploadURL: URL, contentType: String?) async throws -> String in
                let attempt = recorder.recordUpload(url: uploadURL, contentType: contentType)
                if attempt == 1 {
                    throw Self.unsupportedFormat
                }
                return "transcript"
            }
        )

        #expect(transcript == "transcript")
        // The retry uploads the temp WAV with an explicitly pinned Content-Type,
        // never a re-inferred one.
        #expect(recorder.uploads == [
            .init(fileName: "recording.m4a", contentType: nil),
            .init(fileName: "hw-reencode-test.wav", contentType: "audio/wav")
        ])
        #expect(recorder.reencodes == [
            .init(source: "recording.m4a", destination: "hw-reencode-test.wav")
        ])
        #expect(recorder.tempReservations == 1)
        #expect(recorder.removals == ["hw-reencode-test.wav"])
    }

    @Test func successOnTheFirstAttemptReservesNoTempFile() async throws {
        let recorder = FormatRecoveryRecorder()

        let transcript = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
            sourceURL: Self.m4aSource,
            reencode: { source, destination in
                recorder.recordReencode(source: source, destination: destination)
            },
            makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
            fileSize: { _ in 64 * 1024 },
            removeItem: { recorder.recordRemoval($0) },
            send: { (uploadURL: URL, contentType: String?) async throws -> String in
                _ = recorder.recordUpload(url: uploadURL, contentType: contentType)
                return "transcript"
            }
        )

        #expect(transcript == "transcript")
        #expect(recorder.uploads == [.init(fileName: "recording.m4a", contentType: nil)])
        #expect(recorder.reencodes.isEmpty)
        #expect(recorder.tempReservations == 0)
        #expect(recorder.removals.isEmpty)
    }

    // MARK: - Retry-once guarantee

    @Test func aSecond415DoesNotStartAnotherRecovery() async {
        let recorder = FormatRecoveryRecorder()

        do {
            _ = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
                sourceURL: Self.m4aSource,
                reencode: { source, destination in
                    recorder.recordReencode(source: source, destination: destination)
                },
                makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
                fileSize: { _ in 64 * 1024 },
                removeItem: { recorder.recordRemoval($0) },
                send: { (uploadURL: URL, contentType: String?) async throws -> String in
                    let attempt = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    // Distinct messages so the assertion below can tell WHICH 415
                    // came out. Throwing the identical error from both attempts
                    // would pass whether the original was preserved or the retry's
                    // verdict won, leaving the documented contract untested.
                    throw TranscriptionError.serverError(
                        statusCode: 415,
                        message: attempt == 1 ? "original-415" : "reencoded-415"
                    )
                }
            )
            Issue.record("Expected the 415 to surface")
        } catch let error as TranscriptionError {
            guard case .serverError(let status, let message) = error, status == 415 else {
                Issue.record("Expected serverError(415), got \(error)")
                return
            }
            // The retry ran and its own verdict is the freshest, so it wins.
            #expect(message == "reencoded-415")
        } catch {
            Issue.record("Expected TranscriptionError.serverError, got \(error)")
        }

        // EXACTLY two uploads and one re-encode: the recovery never loops.
        #expect(recorder.uploads.count == 2)
        #expect(recorder.reencodes.count == 1)
        #expect(recorder.tempReservations == 1)
        #expect(recorder.removals == ["hw-reencode-test.wav"])
    }

    @Test func aFailedRetrySurfacesTheRetrysOwnErrorNotTheOriginal415() async {
        let recorder = FormatRecoveryRecorder()
        let retryFailure = TranscriptionError.serverError(statusCode: 503, message: "upstream unavailable")

        do {
            _ = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
                sourceURL: Self.m4aSource,
                reencode: { source, destination in
                    recorder.recordReencode(source: source, destination: destination)
                },
                makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
                fileSize: { _ in 64 * 1024 },
                removeItem: { recorder.recordRemoval($0) },
                send: { (uploadURL: URL, contentType: String?) async throws -> String in
                    let attempt = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    throw attempt == 1 ? Self.unsupportedFormat : retryFailure
                }
            )
            Issue.record("Expected the retry's own error")
        } catch let error as TranscriptionError {
            // Once the retry actually reached the server, its verdict is the
            // freshest fact about the request — the stale 415 must not mask it.
            guard case .serverError(let status, _) = error, status == 503 else {
                Issue.record("Expected serverError(503), got \(error)")
                return
            }
        } catch {
            Issue.record("Expected TranscriptionError.serverError(503), got \(error)")
        }

        #expect(recorder.uploads.count == 2)
        #expect(recorder.reencodes.count == 1)
        #expect(recorder.removals == ["hw-reencode-test.wav"])
    }

    @Test func anAlreadyWavSourceIsRethrownWithoutReencoding() async {
        let recorder = FormatRecoveryRecorder()

        do {
            _ = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
                sourceURL: Self.wavSource,
                reencode: { source, destination in
                    recorder.recordReencode(source: source, destination: destination)
                },
                makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
                fileSize: { _ in 64 * 1024 },
                removeItem: { recorder.recordRemoval($0) },
                send: { (uploadURL: URL, contentType: String?) async throws -> String in
                    _ = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    throw Self.unsupportedFormat
                }
            )
            Issue.record("Expected the original 415")
        } catch let error as TranscriptionError {
            guard case .serverError(let status, _) = error, status == 415 else {
                Issue.record("Expected serverError(415), got \(error)")
                return
            }
        } catch {
            Issue.record("Expected TranscriptionError.serverError, got \(error)")
        }

        #expect(recorder.uploads == [.init(fileName: "recording.wav", contentType: nil)])
        #expect(recorder.reencodes.isEmpty)
        #expect(recorder.tempReservations == 0)
        #expect(recorder.removals.isEmpty)
    }

    // MARK: - Non-415 failures pass through untouched

    @Test func nonFormatFailuresAreNotRecovered() async {
        await expectNoRecovery(
            for: TranscriptionError.serverError(statusCode: 500, message: "boom"),
            label: "serverError(500)"
        )
        await expectNoRecovery(
            for: TranscriptionError.unauthorized(provider: "HyperWhisper Cloud"),
            label: "unauthorized"
        )
        await expectNoRecovery(
            for: TranscriptionError.rateLimited(retryAfter: 3),
            label: "rateLimited"
        )
    }

    // MARK: - Recovery impossible: the ORIGINAL 415 survives

    @Test func aFailedReencodePreservesTheOriginal415() async {
        let recorder = FormatRecoveryRecorder()

        do {
            _ = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
                sourceURL: Self.m4aSource,
                reencode: { source, destination in
                    recorder.recordReencode(source: source, destination: destination)
                    throw AudioError.exportFailed
                },
                makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
                fileSize: { _ in 64 * 1024 },
                removeItem: { recorder.recordRemoval($0) },
                send: { (uploadURL: URL, contentType: String?) async throws -> String in
                    _ = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    throw Self.unsupportedFormat
                }
            )
            Issue.record("Expected the original 415")
        } catch let error as TranscriptionError {
            // Must NOT be AudioError.exportFailed: the user-facing failure is the
            // server's rejection, and that error's string is M4A-flavoured.
            guard case .serverError(let status, _) = error, status == 415 else {
                Issue.record("Expected serverError(415), got \(error)")
                return
            }
        } catch {
            Issue.record("Expected TranscriptionError.serverError(415), got \(error)")
        }

        #expect(recorder.uploads.count == 1)
        #expect(recorder.reencodes.count == 1)
        #expect(recorder.removals == ["hw-reencode-test.wav"])
    }

    @Test func anOversizedReencodeIsNotUploaded() async {
        let recorder = FormatRecoveryRecorder()
        let overCap = CloudAudioFormatRecovery.maxReencodedUploadBytes + 1

        do {
            _ = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
                sourceURL: Self.m4aSource,
                reencode: { source, destination in
                    recorder.recordReencode(source: source, destination: destination)
                },
                makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
                fileSize: { _ in overCap },
                removeItem: { recorder.recordRemoval($0) },
                send: { (uploadURL: URL, contentType: String?) async throws -> String in
                    _ = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    throw Self.unsupportedFormat
                }
            )
            Issue.record("Expected the original 415")
        } catch let error as TranscriptionError {
            // The server checks its byte cap BEFORE its format gate, so uploading
            // this would swap the 415 for a 413. Keep the 415.
            guard case .serverError(let status, _) = error, status == 415 else {
                Issue.record("Expected serverError(415), got \(error)")
                return
            }
        } catch {
            Issue.record("Expected TranscriptionError.serverError(415), got \(error)")
        }

        #expect(recorder.uploads.count == 1)
        #expect(recorder.reencodes.count == 1)
        #expect(recorder.removals == ["hw-reencode-test.wav"])
    }

    @Test func anUnreadableReencodePreservesTheOriginal415() async {
        let recorder = FormatRecoveryRecorder()

        do {
            _ = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
                sourceURL: Self.m4aSource,
                reencode: { source, destination in
                    recorder.recordReencode(source: source, destination: destination)
                },
                makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
                fileSize: { _ in nil },
                removeItem: { recorder.recordRemoval($0) },
                send: { (uploadURL: URL, contentType: String?) async throws -> String in
                    _ = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    throw Self.unsupportedFormat
                }
            )
            Issue.record("Expected the original 415")
        } catch let error as TranscriptionError {
            guard case .serverError(let status, _) = error, status == 415 else {
                Issue.record("Expected serverError(415), got \(error)")
                return
            }
        } catch {
            Issue.record("Expected TranscriptionError.serverError(415), got \(error)")
        }

        #expect(recorder.uploads.count == 1)
        #expect(recorder.removals == ["hw-reencode-test.wav"])
    }

    // MARK: - Cancellation

    @Test func cancellationDuringRecoveryStaysCancellationAndCleansUp() async {
        let recorder = FormatRecoveryRecorder()

        let task = Task { () async throws -> String in
            try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
                sourceURL: Self.m4aSource,
                reencode: { source, destination in
                    recorder.recordReencode(source: source, destination: destination)
                },
                makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
                fileSize: { _ in 64 * 1024 },
                removeItem: { recorder.recordRemoval($0) },
                send: { (uploadURL: URL, contentType: String?) async throws -> String in
                    _ = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    // Cancel from inside the attempt so the recovery path observes
                    // an already-cancelled task — deterministic, no sleeps.
                    withUnsafeCurrentTask { $0?.cancel() }
                    throw Self.unsupportedFormat
                }
            )
        }

        do {
            _ = try await task.value
            Issue.record("Expected cancellation")
        } catch is CancellationError {
            // Expected: a cancelled recovery must not be reported as a 415.
        } catch {
            Issue.record("Expected CancellationError, got \(error)")
        }

        #expect(recorder.uploads.count == 1)
        #expect(recorder.reencodes.isEmpty)
        // The temp path is reserved before the cancellation check precisely so
        // this path still cleans up.
        #expect(recorder.removals == ["hw-reencode-test.wav"])
    }

    /// The three "recovery impossible" exits rethrow the ORIGINAL 415. That is
    /// right for a real re-encode failure and wrong for a cancelled one: stopping
    /// a transcription would show an "unsupported audio format" toast for audio
    /// the server never got a second look at. A cancellation observed on the way
    /// to any of those exits must stay a cancellation.
    ///
    /// Cancellation is signalled from inside the re-encode WITHOUT throwing
    /// `CancellationError`, because that is how it really arrives: AVFoundation
    /// tears its reader down and reports its own failure (or writes a truncated
    /// file), so the typed `catch is CancellationError` never sees it.

    @Test func aCancelledReencodeIsNotReportedAsAFormatError() async {
        await expectCancellationInsteadOfFormatError(
            label: "re-encode threw",
            reencodeThrows: true,
            reencodedSize: 64 * 1024
        )
    }

    @Test func aCancelledReencodeWithAnUnreadableFileIsNotReportedAsAFormatError() async {
        await expectCancellationInsteadOfFormatError(
            label: "size unreadable",
            reencodeThrows: false,
            reencodedSize: nil
        )
    }

    @Test func aCancelledReencodeOverTheCapIsNotReportedAsAFormatError() async {
        await expectCancellationInsteadOfFormatError(
            label: "over cap",
            reencodeThrows: false,
            reencodedSize: CloudAudioFormatRecovery.maxReencodedUploadBytes + 1
        )
    }

    // MARK: - Credential carry-over between the two format attempts

    /// The provider nests licence recovery INSIDE format recovery, so the two
    /// format attempts are two separate licence recoveries. This reproduces the
    /// full azure-mai sequence that exposed the bug:
    ///
    ///   .m4a with a stale server licence cache
    ///     -> 401 -> revalidate -> refresh -> resend -> 415
    ///     -> re-encode to WAV -> resend
    ///
    /// The first licence recovery THROWS the 415, so its repaired identity never
    /// comes back as a `TranscribeRequestResult`. If the WAV attempt restarts from
    /// the outer identifier it uploads the whole clip under the known-bad key, and
    /// the second `/license/validate` — rate-limited here, as a real one can be —
    /// sinks the transcription. Entitlement stays server-enforced throughout: the
    /// fake backend below accepts exactly one key, and the client never invents it.
    @Test func theWavRetryUsesTheIdentityTheFirstAttemptRepaired() async throws {
        let recorder = FormatRecoveryRecorder()
        let auth = CloudCredentialRecorder()
        let success = HttpResponse(status: 200, headers: [], body: Data())

        var attemptIdentifier = "stale-license"
        var attemptIsLicensed = true

        let result = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
            sourceURL: Self.m4aSource,
            reencode: { source, destination in
                recorder.recordReencode(source: source, destination: destination)
            },
            makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
            fileSize: { _ in 64 * 1024 },
            removeItem: { recorder.recordRemoval($0) },
            send: { (uploadURL: URL, _: String?) async throws
                -> HyperWhisperCloudProvider.TranscribeRequestResult in
                try await HyperWhisperCloudProvider.performTranscribeRequestWithLicenseRecovery(
                    identifier: attemptIdentifier,
                    isLicensed: attemptIsLicensed,
                    send: { identifier, isLicensed in
                        // `auth` logs the upload here rather than `recorder`: this
                        // closure runs once per HTTP send, which is not the same as
                        // once per format attempt.
                        auth.recordSend(
                            identifier: identifier,
                            isLicensed: isLicensed,
                            fileName: uploadURL.lastPathComponent
                        )
                        // Fake backend: the server's licence cache only knows the
                        // refreshed key, and this tier reads WAV only.
                        guard identifier == "refreshed-license" else {
                            throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
                        }
                        guard uploadURL.pathExtension.lowercased() == "wav" else {
                            throw Self.unsupportedFormat
                        }
                        return success
                    },
                    revalidate: { identifier in
                        // Only the FIRST validate succeeds; a second one is
                        // rate-limited, exactly the case that turned the wasted
                        // round trip into a failed transcription.
                        let call = auth.recordRevalidation(identifier)
                        return Self.validationResult(isValid: call == 1)
                    },
                    currentIdentifier: {
                        auth.recordIdentityResolution()
                        return ("refreshed-license", true)
                    },
                    refreshServerAuthCache: { identifier in
                        auth.recordServerCacheRefresh(identifier)
                    },
                    onLicenseRepaired: { repairedIdentifier, repairedIsLicensed in
                        attemptIdentifier = repairedIdentifier
                        attemptIsLicensed = repairedIsLicensed
                    }
                )
            }
        )

        #expect(result.response == success)
        #expect(result.identifier == "refreshed-license")
        #expect(result.isLicensed)

        // Three sends, and the WAV goes out under the REPAIRED key — not the
        // stale one the outer scope still holds.
        #expect(auth.sends == [
            .init(identifier: "stale-license", isLicensed: true, fileName: "recording.m4a"),
            .init(identifier: "refreshed-license", isLicensed: true, fileName: "recording.m4a"),
            .init(identifier: "refreshed-license", isLicensed: true, fileName: "hw-reencode-test.wav")
        ])
        // Exactly ONE licence repair: the WAV never re-earns a 401, so the
        // second `/license/validate` never happens.
        #expect(auth.revalidations == ["stale-license"])
        #expect(auth.serverCacheRefreshes == ["refreshed-license"])
        #expect(recorder.reencodes.count == 1)
        #expect(recorder.removals == ["hw-reencode-test.wav"])
    }

    // MARK: - Default boundaries

    @Test func defaultTemporaryURLIsUniqueAndWavSuffixed() {
        let first = CloudAudioFormatRecovery.makeTemporaryWAVURL()
        let second = CloudAudioFormatRecovery.makeTemporaryWAVURL()

        #expect(first.pathExtension == "wav")
        #expect(first.lastPathComponent.hasPrefix("hw-reencode-"))
        #expect(first != second)
    }

    @Test func defaultFileSizeIsNilForAMissingFile() {
        let missing = FileManager.default.temporaryDirectory
            .appendingPathComponent("hw-reencode-missing-\(UUID().uuidString).wav")

        #expect(CloudAudioFormatRecovery.fileSizeInBytes(of: missing) == nil)
    }

    @Test func retryContentTypeIsPinnedToAudioWav() {
        #expect(CloudAudioFormatRecovery.wavContentType == "audio/wav")
        // Mirrors AZURE_MAI_MAX_BYTES (300 MB) on the server.
        #expect(CloudAudioFormatRecovery.maxReencodedUploadBytes == 300 * 1024 * 1024)
    }

    // MARK: - Helpers

    private static func validationResult(isValid: Bool) -> LicenseValidationResult {
        LicenseValidationResult(
            isValid: isValid,
            status: isValid ? .active : .invalid,
            customerId: nil,
            customerEmail: nil,
            customerName: nil,
            errorMessage: isValid ? nil : "Validation unavailable"
        )
    }

    /// Cancels the task from inside the re-encode, then drives the recovery to
    /// one of the three "recovery impossible" exits and asserts the cancellation
    /// survives instead of being reported as the server's 415.
    ///
    /// - Parameters:
    ///   - reencodeThrows: `true` reaches the failed-re-encode exit; `false` lets
    ///     the re-encode "succeed" so `reencodedSize` picks the exit.
    ///   - reencodedSize: `nil` reaches the size-unreadable exit, a value above
    ///     the cap reaches the over-cap exit.
    private func expectCancellationInsteadOfFormatError(
        label: String,
        reencodeThrows: Bool,
        reencodedSize: Int64?
    ) async {
        let recorder = FormatRecoveryRecorder()

        let task = Task { () async throws -> String in
            try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
                sourceURL: Self.m4aSource,
                reencode: { source, destination in
                    recorder.recordReencode(source: source, destination: destination)
                    // The user stopped the transcription mid-encode. Note this
                    // does NOT throw CancellationError — the real converter
                    // doesn't either.
                    withUnsafeCurrentTask { $0?.cancel() }
                    if reencodeThrows {
                        throw AudioError.exportFailed
                    }
                },
                makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
                fileSize: { _ in reencodedSize },
                removeItem: { recorder.recordRemoval($0) },
                send: { (uploadURL: URL, contentType: String?) async throws -> String in
                    _ = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    throw Self.unsupportedFormat
                }
            )
        }

        do {
            _ = try await task.value
            Issue.record("Expected cancellation for \(label)")
        } catch is CancellationError {
            // Expected: a cancelled recovery is never an "unsupported audio
            // format" verdict.
        } catch {
            Issue.record("Expected CancellationError for \(label), got \(error)")
        }

        // The original upload happened; the retry never did.
        #expect(recorder.uploads.count == 1)
        #expect(recorder.reencodes.count == 1)
        // Cleanup still runs on the cancellation path.
        #expect(recorder.removals == ["hw-reencode-test.wav"])
    }

    /// Asserts that `error` propagates unchanged with a single upload and no
    /// re-encode, temp reservation or deletion.
    private func expectNoRecovery(for error: TranscriptionError, label: String) async {
        let recorder = FormatRecoveryRecorder()

        do {
            _ = try await CloudAudioFormatRecovery.withUnsupportedFormatRecovery(
                sourceURL: Self.m4aSource,
                reencode: { source, destination in
                    recorder.recordReencode(source: source, destination: destination)
                },
                makeTempURL: { recorder.recordTempReservation(Self.tempWAV) },
                fileSize: { _ in 64 * 1024 },
                removeItem: { recorder.recordRemoval($0) },
                send: { (uploadURL: URL, contentType: String?) async throws -> String in
                    _ = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    throw error
                }
            )
            Issue.record("Expected \(label) to propagate")
        } catch let caught as TranscriptionError {
            #expect(String(describing: caught) == String(describing: error))
        } catch {
            Issue.record("Expected \(label), got \(error)")
        }

        #expect(recorder.uploads.count == 1)
        #expect(recorder.reencodes.isEmpty)
        #expect(recorder.tempReservations == 0)
        #expect(recorder.removals.isEmpty)
    }
}

/// Records every boundary call the recovery makes.
///
/// A lock-guarded class rather than an actor on purpose: `removeItem` is invoked
/// from a `defer`, which cannot `await`, so the deletion boundary has to be
/// synchronous — and therefore so does anything that records it.
private final class FormatRecoveryRecorder: @unchecked Sendable {
    struct Upload: Equatable {
        let fileName: String
        let contentType: String?
    }

    struct Reencode: Equatable {
        let source: String
        let destination: String
    }

    private let lock = NSLock()
    private var storedUploads: [Upload] = []
    private var storedReencodes: [Reencode] = []
    private var storedRemovals: [String] = []
    private var storedTempReservations = 0

    var uploads: [Upload] {
        lock.lock()
        defer { lock.unlock() }
        return storedUploads
    }

    var reencodes: [Reencode] {
        lock.lock()
        defer { lock.unlock() }
        return storedReencodes
    }

    var removals: [String] {
        lock.lock()
        defer { lock.unlock() }
        return storedRemovals
    }

    var tempReservations: Int {
        lock.lock()
        defer { lock.unlock() }
        return storedTempReservations
    }

    /// Returns the 1-based attempt number so a test can fail only the first send.
    func recordUpload(url: URL, contentType: String?) -> Int {
        lock.lock()
        defer { lock.unlock() }
        storedUploads.append(Upload(fileName: url.lastPathComponent, contentType: contentType))
        return storedUploads.count
    }

    func recordReencode(source: URL, destination: URL) {
        lock.lock()
        defer { lock.unlock() }
        storedReencodes.append(
            Reencode(source: source.lastPathComponent, destination: destination.lastPathComponent)
        )
    }

    func recordRemoval(_ url: URL) {
        lock.lock()
        defer { lock.unlock() }
        storedRemovals.append(url.lastPathComponent)
    }

    func recordTempReservation(_ url: URL) -> URL {
        lock.lock()
        defer { lock.unlock() }
        storedTempReservations += 1
        return url
    }
}

/// Records the licence-recovery boundaries for the credential carry-over test:
/// which identity each upload went out under, and how often the licence had to
/// be repaired. Lock-guarded for the same reason as `FormatRecoveryRecorder`.
///
/// No licence key here is real, and none is ever accepted by the client on its
/// own: the fake backend in the test decides what is valid, exactly as the
/// server does in production.
private final class CloudCredentialRecorder: @unchecked Sendable {
    struct Send: Equatable {
        let identifier: String
        let isLicensed: Bool
        let fileName: String
    }

    private let lock = NSLock()
    private var storedSends: [Send] = []
    private var storedRevalidations: [String] = []
    private var storedIdentityResolutions = 0
    private var storedServerCacheRefreshes: [String] = []

    var sends: [Send] {
        lock.lock()
        defer { lock.unlock() }
        return storedSends
    }

    var revalidations: [String] {
        lock.lock()
        defer { lock.unlock() }
        return storedRevalidations
    }

    var identityResolutions: Int {
        lock.lock()
        defer { lock.unlock() }
        return storedIdentityResolutions
    }

    var serverCacheRefreshes: [String] {
        lock.lock()
        defer { lock.unlock() }
        return storedServerCacheRefreshes
    }

    func recordSend(identifier: String, isLicensed: Bool, fileName: String) {
        lock.lock()
        defer { lock.unlock() }
        storedSends.append(Send(identifier: identifier, isLicensed: isLicensed, fileName: fileName))
    }

    /// Returns the 1-based call number so a test can make only the first
    /// revalidation succeed.
    func recordRevalidation(_ identifier: String) -> Int {
        lock.lock()
        defer { lock.unlock() }
        storedRevalidations.append(identifier)
        return storedRevalidations.count
    }

    func recordIdentityResolution() {
        lock.lock()
        defer { lock.unlock() }
        storedIdentityResolutions += 1
    }

    func recordServerCacheRefresh(_ identifier: String) {
        lock.lock()
        defer { lock.unlock() }
        storedServerCacheRefreshes.append(identifier)
    }
}
