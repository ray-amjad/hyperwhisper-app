//
//  RetryingLoopTests.swift
//  hyperwhisperTests
//
//  Drives the `performWithRetry` loop itself (not just the
//  `RetryConfiguration` numbers) through its injected sleeper, so no test
//  sleeps for real. See #686.
//

import Foundation
import Testing
@testable import HyperWhisper

struct RetryingLoopTests {

    /// Records every delay the loop asks to sleep for, and every attempt the
    /// operation saw. The loop runs its closures serially, so no lock is needed.
    private final class Recorder: @unchecked Sendable {
        var sleeps: [TimeInterval] = []
        var attempts: [Int] = []
    }

    private struct NumberedError: Error, Equatable {
        let attempt: Int
    }

    @Test func cancellationIsRethrownOnTheFirstAttemptWithoutSleeping() async {
        let recorder = Recorder()

        await #expect(throws: CancellationError.self) {
            try await performWithRetry(
                config: .postProcessing,
                sleep: { recorder.sleeps.append($0) }
            ) { attempt -> Int in
                recorder.attempts.append(attempt)
                throw CancellationError()
            }
        }

        #expect(recorder.attempts == [1])
        #expect(recorder.sleeps.isEmpty)
    }

    @Test func nonRetryableTranscriptionErrorThrowsOnTheFirstAttemptWithoutSleeping() async {
        let recorder = Recorder()
        #expect(TranscriptionError.invalidRequest.isRetryable == false)

        do {
            _ = try await performWithRetry(
                config: .licenseLaunchValidation,
                sleep: { recorder.sleeps.append($0) }
            ) { attempt -> Int in
                recorder.attempts.append(attempt)
                throw TranscriptionError.invalidRequest
            }
            Issue.record("performWithRetry returned instead of throwing")
        } catch let error as TranscriptionError {
            guard case .invalidRequest = error else {
                Issue.record("unexpected error \(error)")
                return
            }
        } catch {
            Issue.record("unexpected error \(error)")
        }

        #expect(recorder.attempts == [1])
        #expect(recorder.sleeps.isEmpty)
    }

    @Test func retryableErrorRunsMaxAttemptsAndSleepsOneFewerTime() async {
        let recorder = Recorder()
        let config = RetryConfiguration.licenseLaunchValidation
        #expect(TranscriptionError.transientNetwork(details: nil).isRetryable)

        await #expect(throws: TranscriptionError.self) {
            try await performWithRetry(
                config: config,
                sleep: { recorder.sleeps.append($0) }
            ) { attempt -> Int in
                recorder.attempts.append(attempt)
                throw TranscriptionError.transientNetwork(details: "attempt \(attempt)")
            }
        }

        #expect(recorder.attempts == Array(1...config.maxAttempts))
        #expect(recorder.sleeps.count == config.maxAttempts - 1)

        // The documented launch-validation budget (Retrying.swift): about
        // 1.5–1.8 s of total backoff across the 2 sleeps, whatever the jitter.
        let total = recorder.sleeps.reduce(0, +)
        #expect(total >= 1.5 - 1e-9)
        #expect(total <= 1.8 + 1e-9)
    }

    @Test func exhaustionThrowsTheLastErrorNotTheFirst() async {
        let recorder = Recorder()
        let config = RetryConfiguration.postProcessing

        do {
            _ = try await performWithRetry(
                config: config,
                sleep: { recorder.sleeps.append($0) }
            ) { attempt -> Int in
                throw NumberedError(attempt: attempt)
            }
            Issue.record("performWithRetry returned instead of throwing")
        } catch let error as NumberedError {
            #expect(error == NumberedError(attempt: config.maxAttempts))
        } catch {
            Issue.record("unexpected error \(error)")
        }
    }

    @Test func plainURLErrorIsRetriedAndASuccessStopsTheLoop() async throws {
        let recorder = Recorder()

        let value = try await performWithRetry(
            config: .cloud,
            sleep: { recorder.sleeps.append($0) }
        ) { attempt -> String in
            recorder.attempts.append(attempt)
            if attempt < 3 { throw URLError(.notConnectedToInternet) }
            return "ok on \(attempt)"
        }

        #expect(value == "ok on 3")
        #expect(recorder.attempts == [1, 2, 3])
        #expect(recorder.sleeps.count == 2)
    }

    @Test func delayWithZeroJitterIsPureBackoffAndClampsAtMaxDelay() {
        let config = RetryConfiguration.postProcessing

        for attempt in 1...4 {
            let expected = config.initialDelay * pow(config.backoffMultiplier, Double(attempt - 1))
            #expect(config.delay(for: attempt, jitterUnit: 0) == expected)
        }
        // 1.0 * 2^4 = 16 s, clamped to the 10 s ceiling.
        #expect(config.delay(for: 5, jitterUnit: 0) == config.maxDelay)
        // The top of the jitter range adds 20 % of the base delay.
        #expect(abs(config.delay(for: 2, jitterUnit: 0.2) - 2.4) < 1e-9)
    }
}
