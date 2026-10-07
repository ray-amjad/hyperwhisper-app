//
//  Qwen3AsrRuntimeLoadTests.swift
//  hyperwhisperTests
//
//  Pins the Qwen3 ASR compute-unit fallback and what it sends to Sentry.
//  FluidAudio loads Qwen3 with `.all` by default; on some Macs the Neural
//  Engine cannot compile the decoder (CoreML error -14) and nothing fell back,
//  so the user got "Failed to initialize runtime" and Sentry got only that
//  reason string. A real load needs a 4 GB CoreML model, so these tests drive
//  `Qwen3AsrRuntimeLoad.loadWithFallback` with a fake loader instead.
//

import CoreML
import Foundation
import Testing
@testable import HyperWhisper

struct Qwen3AsrRuntimeLoadTests {

    /// The error the Neural Engine compile gives on the MacBook Neo. Its
    /// userInfo carries the model path, which holds the account name.
    private static let aneCompileError = NSError(
        domain: "com.apple.CoreML",
        code: -14,
        userInfo: [
            NSLocalizedDescriptionKey: "Failed to compile /Users/jane.doe/Library/Application Support/FluidAudio/Models/qwen3-asr-0.6b/decoder.mlmodelc",
            NSFilePathErrorKey: "/Users/jane.doe/Library/Application Support/FluidAudio/Models/qwen3-asr-0.6b"
        ]
    )

    private static let gpuLoadError = NSError(
        domain: "com.apple.CoreML",
        code: 0,
        userInfo: [NSLocalizedDescriptionKey: "Failed to load /Users/jane.doe/model"]
    )

    /// Records the compute units it was called with. An actor, because the
    /// loader closure is async.
    private actor Calls {
        private(set) var units: [MLComputeUnits] = []
        func record(_ value: MLComputeUnits) { units.append(value) }
    }

    // MARK: - The fallback

    @Test func aFailedAllLoadIsRetriedOnCpuAndGpu() async throws {
        let calls = Calls()
        let loaded = try await Qwen3AsrRuntimeLoad.loadWithFallback { units in
            await calls.record(units)
            if units == .all { throw Self.aneCompileError }
            return "manager"
        }

        #expect(loaded.value == "manager")
        #expect(loaded.computeUnits == .cpuAndGPU)
        #expect(await calls.units == [.all, .cpuAndGPU])
        #expect(loaded.failures == [
            .init(computeUnits: .all, errorDomain: "com.apple.CoreML", errorCode: -14)
        ])
    }

    @Test func aWorkingAllLoadIsNotRetried() async throws {
        let calls = Calls()
        let loaded = try await Qwen3AsrRuntimeLoad.loadWithFallback { units in
            await calls.record(units)
            return 1
        }

        #expect(loaded.computeUnits == .all)
        #expect(loaded.failures.isEmpty)
        #expect(await calls.units == [.all])
    }

    @Test func whenBothLoadsFailTheErrorCarriesBothCoreMLCodes() async {
        let calls = Calls()
        do {
            _ = try await Qwen3AsrRuntimeLoad.loadWithFallback { units -> Int in
                await calls.record(units)
                throw units == .all ? Self.aneCompileError : Self.gpuLoadError
            }
            Issue.record("expected AllAttemptsFailedError")
        } catch let error as Qwen3AsrRuntimeLoad.AllAttemptsFailedError {
            #expect(error.failures == [
                .init(computeUnits: .all, errorDomain: "com.apple.CoreML", errorCode: -14),
                .init(computeUnits: .cpuAndGPU, errorDomain: "com.apple.CoreML", errorCode: 0)
            ])
            let last = error.lastError as NSError
            #expect(last.domain == "com.apple.CoreML")
            #expect(last.code == 0)
            #expect(last.userInfo.isEmpty)
        } catch {
            Issue.record("unexpected error \(error)")
        }
        #expect(await calls.units == [.all, .cpuAndGPU])
    }

    @Test func aCancelledLoadIsNotRetried() async {
        let calls = Calls()
        do {
            _ = try await Qwen3AsrRuntimeLoad.loadWithFallback { units -> Int in
                await calls.record(units)
                throw CancellationError()
            }
            Issue.record("expected CancellationError")
        } catch {
            #expect(error is CancellationError)
        }
        #expect(await calls.units == [.all])
    }

    // MARK: - What reaches the user

    @Test func whenBothLoadsFailTheUserGetsAClearError() {
        let error = Qwen3AsrRuntimeLoad.userFacingError()
        guard case .providerNotAvailable(let provider, let reason) = error else {
            Issue.record("expected providerNotAvailable, got \(error)")
            return
        }
        #expect(provider == "Qwen3 ASR")
        #expect(reason?.contains("could not load on this Mac") == true)
        #expect(reason?.contains("choose another model") == true)
        #expect(error.errorDescription?.contains("could not load on this Mac") == true)
        #expect(error.isRetryable)
    }

    @Test func onlyTheReportedLoadFailureIsSkippedByTheSecondCapture() {
        #expect(Qwen3AsrRuntimeLoad.isReportedAtSource(Qwen3AsrRuntimeLoad.userFacingError()))
        // The other Qwen3 failures are not reported at the source, so the
        // pipeline must still send them.
        #expect(!Qwen3AsrRuntimeLoad.isReportedAtSource(
            TranscriptionError.providerNotAvailable(provider: "Qwen3 ASR", reason: "Failed to initialize runtime")
        ))
        #expect(!Qwen3AsrRuntimeLoad.isReportedAtSource(
            TranscriptionError.providerNotAvailable(provider: "Parakeet", reason: Qwen3AsrRuntimeLoad.reportedFailureReason)
        ))
        #expect(!Qwen3AsrRuntimeLoad.isReportedAtSource(Self.aneCompileError))
    }

    // MARK: - What reaches Sentry

    @Test func whenBothLoadsFailSentryGetsTheRealCoreMLDomainAndCode() throws {
        let report = try #require(Qwen3AsrRuntimeLoad.sentryReport(
            failures: [
                .init(computeUnits: .all, errorDomain: "com.apple.CoreML", errorCode: -14),
                .init(computeUnits: .cpuAndGPU, errorDomain: "com.apple.CoreML", errorCode: 0)
            ],
            loadedOn: nil,
            lastError: Self.gpuLoadError,
            elapsedMs: 4200
        ))

        #expect(report.kind == .allAttemptsFailed)
        #expect(report.message == "Qwen3 ASR runtime load failed")

        // The captured error is the CoreML one, not the TranscriptionError
        // wrapper, and it carries no description or path.
        let captured = try #require(report.error) as NSError
        #expect(captured.domain == "com.apple.CoreML")
        #expect(captured.code == 0)
        #expect(captured.userInfo.isEmpty)

        #expect(report.extras["qwen3_all_error_domain"] as? String == "com.apple.CoreML")
        #expect(report.extras["qwen3_all_error_code"] as? Int == -14)
        #expect(report.extras["qwen3_cpuAndGPU_error_domain"] as? String == "com.apple.CoreML")
        #expect(report.extras["qwen3_cpuAndGPU_error_code"] as? Int == 0)
        #expect(report.extras["qwen3_load_attempts"] as? [String] == ["all", "cpuAndGPU"])
        #expect(report.extras["qwen3_load_duration_ms"] as? Int == 4200)
        #expect(report.extras["qwen3_loaded_compute_units"] == nil)

        #expect(report.tags["qwen3_first_error_domain"] == "com.apple.CoreML")
        #expect(report.tags["qwen3_first_error_code"] == "-14")
        #expect(report.fingerprint == [
            "qwen3-asr-runtime-load", "com.apple.CoreML", "-14", "com.apple.CoreML", "0"
        ])

        let everything = "\(report.extras) \(report.tags) \(report.message)"
        #expect(!everything.contains("jane.doe"))
    }

    @Test func aFallbackThatWorksIsALogWithTheNeuralEngineCode() throws {
        let report = try #require(Qwen3AsrRuntimeLoad.sentryReport(
            failures: [
                .init(computeUnits: .all, errorDomain: "com.apple.CoreML", errorCode: -14)
            ],
            loadedOn: .cpuAndGPU,
            lastError: nil,
            elapsedMs: 9000
        ))

        #expect(report.kind == .fallbackSucceeded)
        #expect(report.error == nil)
        #expect(report.extras["qwen3_all_error_code"] as? Int == -14)
        #expect(report.extras["qwen3_loaded_compute_units"] as? String == "cpuAndGPU")
        #expect(report.tags["qwen3_loaded_compute_units"] == "cpuAndGPU")
        // A warning goes to the Sentry logs quota, not the Issues list.
        #expect(SentryService.store(for: .warning) == .log)
    }

    @Test func aFirstTryLoadSendsNothing() {
        #expect(Qwen3AsrRuntimeLoad.sentryReport(
            failures: [],
            loadedOn: .all,
            lastError: nil,
            elapsedMs: 3000
        ) == nil)
    }
}
