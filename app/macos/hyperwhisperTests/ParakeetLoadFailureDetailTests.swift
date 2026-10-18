//
//  ParakeetLoadFailureDetailTests.swift
//  hyperwhisperTests
//
//  Issue #1017: a failed Parakeet / Nemotron load reached Sentry as a domain
//  and a code only, so HYPERWHISPER-Z7 / Z8 / W2 could not say WHY it failed.
//  `LocalModelLoadFailureDetail` reads the error chain into fixed identifiers.
//  These tests pin every FluidAudio 0.15.2 `HuggingFaceDownloadError` case,
//  the underlying-error paths, the phase box, and that no returned value ever
//  carries the error's path, message or snippet.
//
//  #1980 adds the Nemotron runtime-load Sentry extras/tags, the public
//  one-line error summary, and a guard on the live streaming log lines.
//

import Foundation
import Testing
import FluidAudio
@testable import HyperWhisper

@Suite("Parakeet load failure detail")
struct ParakeetLoadFailureDetailTests {

    private typealias Detail = LocalModelLoadFailureDetail
    private typealias HFError = DownloadUtils.HuggingFaceDownloadError

    /// Text that must never reach a report. The path holds a user name, like a
    /// real cache directory does.
    private static let secretPath = "/Users/example-user/Library/Application Support/FluidAudio/Models/parakeet-tdt-0.6b-v3-coreml/Encoder.mlmodelc"
    private static let secretMessage = "Rate limited while downloading secret-message-text"
    private static let secretSnippet = "<!doctype html><title>secret-snippet-text</title>"
    private static let secrets = [secretPath, secretMessage, secretSnippet, "example-user"]

    /// Every value the helper can hand to Sentry or os_log for this error.
    private func everyReportedValue(for error: Error) -> [String] {
        let fields = Detail.fields(for: error)
        let extras = Detail.extras(for: fields, keyPrefix: "parakeet")
        return extras.values.map { String(describing: $0) } + [Detail.logSummary(for: fields)]
    }

    private func expectNoSecrets(in error: Error) {
        for value in everyReportedValue(for: error) {
            for secret in Self.secrets {
                #expect(!value.contains(secret), "leaked \(secret) in \(value)")
            }
        }
    }

    // MARK: - HuggingFaceDownloadError cases

    @Test func invalidResponse() {
        let error = HFError.invalidResponse
        #expect(Detail.fields(for: error) == Detail.Fields(hfErrorCase: "invalid_response"))
        expectNoSecrets(in: error)
    }

    @Test func rateLimitedCarriesStatusNotMessage() {
        let error = HFError.rateLimited(statusCode: 429, message: Self.secretMessage)
        #expect(Detail.fields(for: error) == Detail.Fields(hfErrorCase: "rate_limited", httpStatus: 429))
        expectNoSecrets(in: error)
    }

    @Test func downloadFailedWithHTTPUnderlying() {
        let error = HFError.downloadFailed(
            path: Self.secretPath,
            underlying: NSError(domain: "HTTP", code: 403)
        )
        let fields = Detail.fields(for: error)
        #expect(fields == Detail.Fields(
            hfErrorCase: "download_failed",
            httpStatus: 403,
            underlyingErrorDomain: "HTTP",
            underlyingErrorCode: 403
        ))
        let extras = Detail.extras(for: fields, keyPrefix: "parakeet")
        #expect(extras["parakeet_hf_error_case"] as? String == "download_failed")
        #expect(extras["parakeet_http_status"] as? Int == 403)
        #expect(extras["parakeet_underlying_error_domain"] as? String == "HTTP")
        #expect(extras["parakeet_underlying_error_code"] as? Int == 403)
        expectNoSecrets(in: error)
    }

    @Test func downloadFailedWithURLErrorTimeout() {
        let error = HFError.downloadFailed(path: Self.secretPath, underlying: URLError(.timedOut))
        #expect(Detail.fields(for: error) == Detail.Fields(
            hfErrorCase: "download_failed",
            httpStatus: nil,
            underlyingErrorDomain: NSURLErrorDomain,
            underlyingErrorCode: URLError.timedOut.rawValue
        ))
        expectNoSecrets(in: error)
    }

    @Test func modelNotFoundDropsPath() {
        let error = HFError.modelNotFound(path: Self.secretPath)
        #expect(Detail.fields(for: error) == Detail.Fields(hfErrorCase: "model_not_found"))
        expectNoSecrets(in: error)
    }

    @Test func htmlErrorResponseDropsPathAndSnippet() {
        let error = HFError.htmlErrorResponse(path: Self.secretPath, snippet: Self.secretSnippet)
        #expect(Detail.fields(for: error) == Detail.Fields(hfErrorCase: "html_error_response"))
        expectNoSecrets(in: error)
    }

    // MARK: - CoreML

    /// HYPERWHISPER-Z8: `com.apple.CoreML` code 0 with the cause one level down.
    @Test func coreMLErrorReadsUnderlyingKey() {
        let error = NSError(domain: "com.apple.CoreML", code: 0, userInfo: [
            NSUnderlyingErrorKey: NSError(domain: NSPOSIXErrorDomain, code: 28),
            NSFilePathErrorKey: Self.secretPath,
            NSLocalizedDescriptionKey: "Failed to compile \(Self.secretPath)",
        ])
        #expect(Detail.fields(for: error) == Detail.Fields(
            underlyingErrorDomain: NSPOSIXErrorDomain,
            underlyingErrorCode: 28
        ))
        expectNoSecrets(in: error)
    }

    /// HYPERWHISPER-W2: Nemotron's preload fails with the same generic CoreML
    /// error. With no underlying error, nothing is invented.
    @Test func nemotronStyleCoreMLError() {
        let bare = NSError(domain: "com.apple.CoreML", code: 0, userInfo: [
            NSLocalizedDescriptionKey: "Unable to load model at \(Self.secretPath)",
        ])
        #expect(Detail.fields(for: bare) == Detail.Fields())
        #expect(Detail.logSummary(for: Detail.fields(for: bare))
            == "hfErrorCase=none httpStatus=none underlyingErrorDomain=none underlyingErrorCode=none")
        expectNoSecrets(in: bare)

        let wrapped = NSError(domain: "com.apple.CoreML", code: 0, userInfo: [
            NSUnderlyingErrorKey: NSError(domain: "com.apple.CoreML", code: 1, userInfo: [
                NSFilePathErrorKey: Self.secretPath,
            ]),
            NSLocalizedDescriptionKey: "Unable to load model at \(Self.secretPath)",
        ])
        #expect(Detail.fields(for: wrapped) == Detail.Fields(
            underlyingErrorDomain: "com.apple.CoreML",
            underlyingErrorCode: 1
        ))
        expectNoSecrets(in: wrapped)
    }

    // MARK: - Load phase

    private func phase(_ phase: DownloadUtils.DownloadPhase) -> Detail.LoadPhase {
        Detail.LoadPhase(phase)
    }

    @Test func compilingModelNamesBecomeFixedSlugs() {
        #expect(phase(.compiling(modelName: "Preprocessor.mlmodelc")) == .compiling(modelSlug: "preprocessor"))
        #expect(phase(.compiling(modelName: "Encoder.mlmodelc")) == .compiling(modelSlug: "encoder"))
        #expect(phase(.compiling(modelName: "EncoderInt4.mlmodelc")) == .compiling(modelSlug: "encoder"))
        #expect(phase(.compiling(modelName: "Decoder.mlmodelc")) == .compiling(modelSlug: "decoder"))
        #expect(phase(.compiling(modelName: "JointDecision.mlmodelc")) == .compiling(modelSlug: "joint"))
        #expect(phase(.compiling(modelName: "JointDecisionv3.mlmodelc")) == .compiling(modelSlug: "joint"))
        #expect(phase(.compiling(modelName: Self.secretPath)) == .compiling(modelSlug: "other"))
        // `loadModels` closes with an empty name once every model compiled.
        #expect(phase(.compiling(modelName: "")) == .compiled)
        #expect(phase(.listing) == .listing)
        #expect(phase(.downloading(completedFiles: 3, totalFiles: 12))
            == .downloading(completedFiles: 3, totalFiles: 12))
    }

    @Test func recorderKeepsOnlyTheLastPhase() {
        let recorder = LocalModelLoadPhaseRecorder()
        #expect(recorder.lastPhase == nil)
        recorder.record(DownloadUtils.DownloadProgress(fractionCompleted: 0, phase: .listing))
        recorder.record(DownloadUtils.DownloadProgress(
            fractionCompleted: 0.2, phase: .downloading(completedFiles: 2, totalFiles: 9)))
        recorder.record(DownloadUtils.DownloadProgress(
            fractionCompleted: 0.6, phase: .compiling(modelName: "Encoder.mlmodelc")))
        #expect(recorder.lastPhase == .compiling(modelSlug: "encoder"))
    }

    @Test func phaseExtras() {
        let none = Detail.extras(for: nil, keyPrefix: "parakeet")
        #expect(none.count == 1)
        #expect(none["parakeet_last_phase"] as? String == "none")

        let downloading = Detail.extras(
            for: .downloading(completedFiles: 4, totalFiles: 12), keyPrefix: "parakeet")
        #expect(downloading["parakeet_last_phase"] as? String == "downloading")
        #expect(downloading["parakeet_files_completed"] as? Int == 4)
        #expect(downloading["parakeet_files_total"] as? Int == 12)

        let compiling = Detail.extras(for: .compiling(modelSlug: "joint"), keyPrefix: "parakeet")
        #expect(compiling["parakeet_last_phase"] as? String == "compiling")
        #expect(compiling["parakeet_compiling_model"] as? String == "joint")

        #expect(Detail.logSummary(for: .downloading(completedFiles: 4, totalFiles: 12))
            == "lastPhase=downloading filesCompleted=4 filesTotal=12")
        #expect(Detail.logSummary(for: .compiling(modelSlug: "joint"))
            == "lastPhase=compiling compilingModel=joint")
        #expect(Detail.logSummary(for: nil) == "lastPhase=none")
    }

    // MARK: - Sentry allow-list

    /// Every key the helper can emit with the Parakeet prefix is on the exact
    /// allow-list, so a String value is not replaced with `[redacted]`.
    @Test func everyParakeetKeyIsAllowListed() {
        let fullFields = Detail.Fields(
            hfErrorCase: "download_failed",
            httpStatus: 403,
            underlyingErrorDomain: "HTTP",
            underlyingErrorCode: 403
        )
        var keys = Set(Detail.extras(for: fullFields, keyPrefix: "parakeet").keys)
        keys.formUnion(Detail.extras(for: .downloading(completedFiles: 1, totalFiles: 2), keyPrefix: "parakeet").keys)
        keys.formUnion(Detail.extras(for: .compiling(modelSlug: "encoder"), keyPrefix: "parakeet").keys)
        #expect(keys.count == 8)
        for key in keys {
            #expect(SentryService.allowedExtraKeys.contains(key), "\(key)")
        }
    }

    // MARK: - Nemotron runtime load failure event (#1980)

    /// HYPERWHISPER-W2 shape: CoreML code 0 with a path in the description,
    /// in `NSFilePathErrorKey`, and in the underlying error.
    private static var nemotronCoreMLError: NSError {
        NSError(domain: "com.apple.CoreML", code: 0, userInfo: [
            NSUnderlyingErrorKey: NSError(domain: NSCocoaErrorDomain, code: 260, userInfo: [
                NSFilePathErrorKey: secretPath,
                NSLocalizedDescriptionKey: "The file \(secretPath) couldn't be opened.",
            ]),
            NSFilePathErrorKey: secretPath,
            NSLocalizedDescriptionKey: "Unable to load model at \(secretPath)",
        ])
    }

    private func expectAllowListedAndPathFree(_ values: [String: Any]) {
        for (key, value) in values {
            #expect(SentryService.allowedExtraKeys.contains(key), "\(key)")
            let text = String(describing: value)
            for secret in Self.secrets {
                #expect(!text.contains(secret), "leaked \(secret) in \(key)=\(text)")
            }
            #expect(!text.contains("/Users/"), "path in \(key)=\(text)")
        }
    }

    @Test func nemotronFailureExtrasFromCoreMLErrorWithPaths() {
        let extras = NemotronProvider.loadFailureExtras(for: Self.nemotronCoreMLError)
        #expect(Set(extras.keys) == [
            "nemotron_error_code",
            "nemotron_underlying_error_domain",
            "nemotron_underlying_error_code",
        ])
        #expect(extras["nemotron_error_code"] as? Int == 0)
        #expect(extras["nemotron_underlying_error_domain"] as? String == NSCocoaErrorDomain)
        #expect(extras["nemotron_underlying_error_code"] as? Int == 260)
        expectAllowListedAndPathFree(extras)

        let tags = NemotronProvider.loadFailureTags(
            variantSlug: "multilingual",
            errorDomain: Self.nemotronCoreMLError.domain
        )
        #expect(tags["nemotron_variant"] == "multilingual")
        #expect(tags["nemotron_error_domain"] == "com.apple.CoreML")
        expectAllowListedAndPathFree(tags)
    }

    @Test func nemotronFailureExtrasFromDownloadFailedWithPath() {
        let error = HFError.downloadFailed(
            path: Self.secretPath,
            underlying: NSError(domain: "HTTP", code: 403, userInfo: [
                NSLocalizedDescriptionKey: "GET \(Self.secretPath) returned 403",
            ])
        )
        let extras = NemotronProvider.loadFailureExtras(for: error)
        #expect(extras["nemotron_hf_error_case"] as? String == "download_failed")
        #expect(extras["nemotron_http_status"] as? Int == 403)
        #expect(extras["nemotron_underlying_error_domain"] as? String == "HTTP")
        #expect(extras["nemotron_underlying_error_code"] as? Int == 403)
        #expect(extras.count == 5)
        expectAllowListedAndPathFree(extras)
    }

    /// A cancelled load (reset / invalidate, a cancelled download) sends no
    /// "Nemotron runtime load failed" event; a real failure does.
    @Test func nemotronLoadCancellationClassification() {
        #expect(NemotronProvider.isLoadCancellation(CancellationError()))
        #expect(NemotronProvider.isLoadCancellation(URLError(.cancelled)))
        #expect(NemotronProvider.isLoadCancellation(
            NSError(domain: NSURLErrorDomain, code: NSURLErrorCancelled)
        ))
        #expect(!NemotronProvider.isLoadCancellation(URLError(.timedOut)))
        #expect(!NemotronProvider.isLoadCancellation(Self.nemotronCoreMLError))
    }

    // MARK: - Public log summary (#1980)

    @Test func publicSummaryOfNSErrorWithPathIsPathFree() {
        let summary = Detail.publicSummary(for: Self.nemotronCoreMLError)
        #expect(summary == "errorDomain=com.apple.CoreML errorCode=0 hfErrorCase=none httpStatus=none underlyingErrorDomain=\(NSCocoaErrorDomain) underlyingErrorCode=260")
        for secret in Self.secrets {
            #expect(!summary.contains(secret), "leaked \(secret) in \(summary)")
        }
    }

    @Test func publicSummaryOfHuggingFaceDownloadFailedIsPathFree() {
        let error = HFError.downloadFailed(path: Self.secretPath, underlying: URLError(.timedOut))
        // The description this replaces in the streaming logs holds the path.
        let summary = Detail.publicSummary(for: error)
        #expect(summary.hasPrefix("errorDomain="))
        #expect(summary.contains("hfErrorCase=download_failed httpStatus=none underlyingErrorDomain=\(NSURLErrorDomain) underlyingErrorCode=\(URLError.timedOut.rawValue)"))
        #expect(!summary.contains("/Users/"))
        for secret in Self.secrets {
            #expect(!summary.contains(secret), "leaked \(secret) in \(summary)")
        }
    }

    // MARK: - Live streaming log lines (#1980)

    /// The live local-streaming files must not put an error's description in a
    /// public os_log interpolation. Comments are stripped before the scan.
    @Test func liveStreamingFilesLogNoPublicErrorDescription() throws {
        let directory = "app/macos/hyperwhisper/Managers/AudioRecording/Streaming/Local/"
        for file in [
            "LocalParakeetStreamingClient.swift",
            "LocalNemotronStreamingClient.swift",
            "ParakeetStreamingSession.swift",
            "NemotronStreamingSession.swift",
        ] {
            let code = try ProductionSource.code(of: directory + file)
            #expect(!code.contains("localizedDescription, privacy: .public"), "\(file)")
            #expect(code.contains("LocalModelLoadFailureDetail.publicSummary(for: error)"), "\(file)")
        }
    }
}
