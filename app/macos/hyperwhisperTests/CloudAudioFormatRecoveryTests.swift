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
                    _ = recorder.recordUpload(url: uploadURL, contentType: contentType)
                    throw Self.unsupportedFormat
                }
            )
            Issue.record("Expected the 415 to surface")
        } catch let error as TranscriptionError {
            guard case .serverError(let status, _) = error, status == 415 else {
                Issue.record("Expected serverError(415), got \(error)")
                return
            }
        } catch {
            Issue.record("Expected TranscriptionError.serverError, got \(error)")
        }

        // EXACTLY two uploads and one re-encode: the recovery never loops.
        #expect(recorder.uploads.count == 2)
        #expect(recorder.reencodes.count == 1)
        #expect(recorder.tempReservations == 1)
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
