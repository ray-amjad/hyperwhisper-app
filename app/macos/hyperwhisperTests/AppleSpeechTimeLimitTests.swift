//
//  AppleSpeechTimeLimitTests.swift
//  hyperwhisperTests
//
//  Issue #1701: Apple Speech awaited `analyzeSequence` and `transcriber.results`
//  with no time limit, so any input that never ends the results stream hung a
//  dictation forever. STEP 5 of `AppleSpeechAnalyzerProvider.transcribe` now
//  runs through `AppleSpeechTimeLimit.run`.
//
//  The hung analyzer is modelled by `HungResults`: an async sequence whose
//  `next()` parks on a continuation that IGNORES task cancellation, the way a
//  framework call that never checks it would. A task-group race would throw the
//  timeout and then wait on that child forever; these tests prove `run` does
//  not. No Speech assets and no macOS 26 are needed.
//
//  The timer is a `ManualDeadline` the test fires itself (#2400). On a loaded
//  CI runner the Swift pool ran 150 ms timers 18-21 s late, so any test that
//  raced real time against a sub-second limit, or bounded a wait by 10 s,
//  failed with correct code. Waits for something that MUST happen are bounded
//  only by `hangGuard`; one test still runs the default `Task.sleep` timer.
//

import Foundation
import Testing
@testable import HyperWhisper

struct AppleSpeechTimeLimitTests {

    // MARK: - Stubs

    /// Parks every caller until `release()`. Cancellation does not wake it.
    private final class Parking: @unchecked Sendable {
        private let lock = NSLock()
        private var parked: [CheckedContinuation<Void, Never>] = []
        private var released = false
        /// Signalled once per caller that parks.
        let didPark = DispatchSemaphore(value: 0)

        var parkedCount: Int {
            lock.lock()
            defer { lock.unlock() }
            return parked.count
        }

        func park() async {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                lock.lock()
                if released {
                    lock.unlock()
                    continuation.resume()
                    return
                }
                parked.append(continuation)
                lock.unlock()
                didPark.signal()
            }
        }

        func release() {
            lock.lock()
            released = true
            let waiting = parked
            parked = []
            lock.unlock()
            waiting.forEach { $0.resume() }
        }
    }

    /// A stand-in for `transcriber.results` that never yields and never ends
    /// until its `Parking` is released.
    private struct HungResults: AsyncSequence {
        typealias Element = String
        let parking: Parking

        struct AsyncIterator: AsyncIteratorProtocol {
            let parking: Parking
            mutating func next() async throws -> String? {
                await parking.park()
                return nil
            }
        }

        func makeAsyncIterator() -> AsyncIterator { AsyncIterator(parking: parking) }
    }

    /// Reads every element of `results`, like `collectTranscriptionResults`.
    private static func collect(_ results: HungResults) async throws -> [String] {
        var segments: [String] = []
        for try await segment in results {
            segments.append(segment)
        }
        return segments
    }

    private struct StubFailure: Error, Equatable {}

    /// The timer `run` sleeps on, ended by the test with `fire()` instead of
    /// by the clock. Like `Task.sleep`, it throws when its task is cancelled.
    private final class ManualDeadline: @unchecked Sendable {
        private let lock = NSLock()
        private var waiting: CheckedContinuation<Void, Error>?
        private var fired = false
        private var cancelled = false
        private var requested: Duration?

        /// Signalled when `run` starts sleeping on this deadline.
        let armed = DispatchSemaphore(value: 0)
        /// Signalled when that sleep is cancelled (the work ended first).
        let didCancel = DispatchSemaphore(value: 0)

        /// The limit `run` asked to sleep for.
        var requestedLimit: Duration? {
            lock.lock()
            defer { lock.unlock() }
            return requested
        }

        func sleep(_ limit: Duration) async throws {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    lock.lock()
                    requested = limit
                    if cancelled {
                        lock.unlock()
                        continuation.resume(throwing: CancellationError())
                        return
                    }
                    if fired {
                        lock.unlock()
                        continuation.resume()
                        return
                    }
                    waiting = continuation
                    lock.unlock()
                    armed.signal()
                }
            } onCancel: {
                lock.lock()
                cancelled = true
                let continuation = waiting
                waiting = nil
                lock.unlock()
                continuation?.resume(throwing: CancellationError())
                didCancel.signal()
            }
        }

        /// The limit is up.
        func fire() {
            lock.lock()
            fired = true
            let continuation = waiting
            waiting = nil
            lock.unlock()
            continuation?.resume()
        }
    }

    /// Bounds only a wait for something that must happen. No assertion relies
    /// on it: a hang is forever, a stalled runner is not.
    private static let hangGuard: Double = 120

    // MARK: - A hung analyzer ends with an error in bounded time

    @Test func aResultsStreamThatNeverEndsTimesOutAndRunsTheHook() async {
        let parking = Parking()
        let deadline = ManualDeadline()
        defer {
            parking.release()
            deadline.fire()
        }
        let hookRan = DispatchSemaphore(value: 0)
        let ended = DispatchSemaphore(value: 0)
        let limit: Duration = .milliseconds(150)

        let caller = Task { () -> Result<[String], Error> in
            defer { ended.signal() }
            do {
                return .success(try await AppleSpeechTimeLimit.run(
                    limit: limit,
                    sleep: { try await deadline.sleep($0) },
                    onTimeout: { hookRan.signal() },
                    operation: { try await Self.collect(HungResults(parking: parking)) }
                ))
            } catch {
                return .failure(error)
            }
        }

        #expect(await waitOffThePool(for: deadline.armed, seconds: Self.hangGuard), "run never started its timer")
        #expect(await waitOffThePool(for: parking.didPark, seconds: Self.hangGuard), "the operation never started")
        #expect(deadline.requestedLimit == limit)
        // Before the limit is up, the hung work holds `run`.
        #expect(await waitOffThePool(for: ended, seconds: 0.2) == false, "run returned before the limit")

        deadline.fire()
        #expect(await waitOffThePool(for: ended, seconds: Self.hangGuard), "run waited on the hung work")
        switch await caller.value {
        case .success(let segments):
            Issue.record("A results stream that never ends returned \(segments) instead of timing out")
        case .failure(let error as AppleSpeechTimeLimit.TimedOut):
            #expect(error.limit == limit)
        case .failure(let error):
            Issue.record("Expected AppleSpeechTimeLimit.TimedOut, got \(error)")
        }

        #expect(await waitOffThePool(for: hookRan, seconds: Self.hangGuard), "onTimeout never ran")
        // The stub ignored the cancellation and is still parked: `run` returned
        // without waiting for it, which a task-group race could not do.
        #expect(parking.parkedCount == 1)
    }

    /// The provider's real hook: `cancelAndFinishNow()` ends the hung stream.
    /// Here the hook releases the stub, and the abandoned work then finishes.
    /// The one test on the default `Task.sleep` timer: it only waits for the
    /// timeout, so a late timer makes it slower, never wrong.
    @Test func theHookCanEndTheHungWork() async {
        let parking = Parking()
        defer { parking.release() }
        let workEnded = DispatchSemaphore(value: 0)

        await #expect(throws: AppleSpeechTimeLimit.TimedOut.self) {
            try await AppleSpeechTimeLimit.run(
                limit: .milliseconds(100),
                onTimeout: { parking.release() },
                operation: {
                    defer { workEnded.signal() }
                    return try await Self.collect(HungResults(parking: parking))
                }
            )
        }
        #expect(await waitOffThePool(for: workEnded, seconds: Self.hangGuard), "the hook did not end the hung work")
    }

    /// `cancelAndFinishNow()` runs on the analyzer actor that may itself be
    /// stuck. A hook that never returns must not hold the timeout back.
    @Test func aHookThatNeverReturnsDoesNotDelayTheTimeout() async {
        let work = Parking()
        let hook = Parking()
        let deadline = ManualDeadline()
        defer {
            work.release()
            hook.release()
            deadline.fire()
        }
        let ended = DispatchSemaphore(value: 0)

        let caller = Task { () -> Error? in
            defer { ended.signal() }
            do {
                _ = try await AppleSpeechTimeLimit.run(
                    limit: .milliseconds(100),
                    sleep: { try await deadline.sleep($0) },
                    onTimeout: { await hook.park() },
                    operation: { try await Self.collect(HungResults(parking: work)) }
                )
                return nil
            } catch {
                return error
            }
        }

        #expect(await waitOffThePool(for: deadline.armed, seconds: Self.hangGuard), "run never started its timer")
        deadline.fire()
        #expect(await waitOffThePool(for: hook.didPark, seconds: Self.hangGuard), "onTimeout never ran")
        // The hook is parked for good; `run` must not wait on it.
        #expect(await waitOffThePool(for: ended, seconds: Self.hangGuard), "the timeout waited on the hook")
        let error = await caller.value
        #expect(error is AppleSpeechTimeLimit.TimedOut, "got \(String(describing: error))")
    }

    // MARK: - A normal transcription is not cut short

    @Test func aFastOperationReturnsItsValueAndTheHookNeverRuns() async throws {
        let deadline = ManualDeadline()
        let hookRan = DispatchSemaphore(value: 0)

        let value = try await AppleSpeechTimeLimit.run(
            limit: .seconds(1),
            sleep: { try await deadline.sleep($0) },
            onTimeout: { hookRan.signal() },
            operation: { () async throws -> [String] in
                try await Task.sleep(for: .milliseconds(50))
                return ["hello", "world"]
            }
        )

        #expect(value == ["hello", "world"])
        // The timer was cancelled with the work's result, so the hook (the
        // analyzer cancel) never runs on a finished run. Firing the deadline
        // afterwards wakes nothing: the cancelled sleep has already thrown.
        #expect(await waitOffThePool(for: deadline.didCancel, seconds: Self.hangGuard), "the timer was never cancelled")
        deadline.fire()
        #expect(await waitOffThePool(for: hookRan, seconds: 0.5) == false, "onTimeout ran after a finished operation")
    }

    @Test func anOperationErrorPassesThroughUnchanged() async {
        let deadline = ManualDeadline()
        await #expect(throws: StubFailure.self) {
            try await AppleSpeechTimeLimit.run(
                limit: .seconds(5),
                sleep: { try await deadline.sleep($0) },
                onTimeout: {},
                operation: { () async throws -> [String] in throw StubFailure() }
            )
        }
    }

    // MARK: - Caller cancellation stays a cancellation

    @Test func aCallerCancellationSurfacesAsAGenuineCancellation() async throws {
        let deadline = ManualDeadline()
        defer { deadline.fire() }
        let started = DispatchSemaphore(value: 0)
        let hookRan = DispatchSemaphore(value: 0)
        let ended = DispatchSemaphore(value: 0)

        let caller = Task { () -> (error: Error?, isTaskCancelled: Bool) in
            defer { ended.signal() }
            do {
                _ = try await AppleSpeechTimeLimit.run(
                    limit: .seconds(30),
                    sleep: { try await deadline.sleep($0) },
                    onTimeout: { hookRan.signal() },
                    operation: { () async throws -> [String] in
                        started.signal()
                        // Honours cancellation, as `analyzeSequence` does today.
                        try await Task.sleep(for: .seconds(3_600))
                        return []
                    }
                )
                return (nil, Task.isCancelled)
            } catch {
                // Read at the catch site, exactly as the provider does.
                return (error, Task.isCancelled)
            }
        }

        #expect(await waitOffThePool(for: started, seconds: Self.hangGuard))
        caller.cancel()
        // The limit is never up here, so a `run` that waited for it would hang.
        #expect(await waitOffThePool(for: ended, seconds: Self.hangGuard), "the cancellation waited for the limit")
        deadline.fire()
        let outcome = await caller.value

        let error = try #require(outcome.error)
        #expect(error is CancellationError, "got \(error)")
        #expect(!(error is AppleSpeechTimeLimit.TimedOut))
        #expect(
            TranscriptionCancellationPolicy.outcome(for: error, isTaskCancelled: outcome.isTaskCancelled)
                == .genuineCancellation
        )
        #expect(await waitOffThePool(for: hookRan, seconds: 0.2) == false, "a cancellation ran the timeout hook")
    }

    /// The analyzer ignores the caller's cancel: the limit still ends it, and
    /// the provider's timeout arm then sees `Task.isCancelled` and throws
    /// `CancellationError` rather than an error the user would see.
    @Test func aCancellationTheWorkIgnoresStillEndsAtTheLimit() async {
        let parking = Parking()
        let deadline = ManualDeadline()
        defer {
            parking.release()
            deadline.fire()
        }
        let started = DispatchSemaphore(value: 0)
        let ended = DispatchSemaphore(value: 0)

        let caller = Task { () -> Error? in
            defer { ended.signal() }
            do {
                _ = try await AppleSpeechTimeLimit.run(
                    limit: .milliseconds(200),
                    sleep: { try await deadline.sleep($0) },
                    onTimeout: {},
                    operation: {
                        started.signal()
                        return try await Self.collect(HungResults(parking: parking))
                    }
                )
                return nil
            } catch {
                return error
            }
        }

        #expect(await waitOffThePool(for: started, seconds: Self.hangGuard))
        #expect(await waitOffThePool(for: deadline.armed, seconds: Self.hangGuard), "run never started its timer")
        caller.cancel()
        // The work ignores the cancel, so `run` is still waiting...
        #expect(await waitOffThePool(for: ended, seconds: 0.2) == false, "the ignored cancel ended run")
        // ...until the limit is up.
        deadline.fire()
        #expect(await waitOffThePool(for: ended, seconds: Self.hangGuard), "the limit did not end run")
        let error = await caller.value
        #expect(error is AppleSpeechTimeLimit.TimedOut, "got \(String(describing: error))")
    }

    /// A timeout on a task nobody cancelled is a provider failure, never a
    /// benign cancellation that would hide it from Sentry.
    @Test func aTimeoutIsNotAGenuineCancellation() {
        let timeout = AppleSpeechTimeLimit.TimedOut(limit: .seconds(60))
        #expect(TranscriptionCancellationPolicy.outcome(for: timeout, isTaskCancelled: false) == .providerFailure)
    }

    // MARK: - The limit: 60 s + 1x the audio

    @Test func theLimitIsSixtySecondsPlusTheAudio() {
        #expect(AppleSpeechTimeLimit.limitSeconds(forAudioDuration: 0) == 60)
        #expect(AppleSpeechTimeLimit.limitSeconds(forAudioDuration: 30) == 90)
        #expect(AppleSpeechTimeLimit.limitSeconds(forAudioDuration: 12.5) == 72.5)
        #expect(AppleSpeechTimeLimit.limitSeconds(forAudioDuration: 3_600) == 3_660)
    }

    @Test func aNonsenseDurationFallsBackToTheBase() {
        #expect(AppleSpeechTimeLimit.limitSeconds(forAudioDuration: -5) == 60)
        #expect(AppleSpeechTimeLimit.limitSeconds(forAudioDuration: .nan) == 60)
        #expect(AppleSpeechTimeLimit.limitSeconds(forAudioDuration: .infinity) == 60)
        #expect(AppleSpeechTimeLimit.limitSeconds(forAudioDuration: 1e300) == 60 + AppleSpeechTimeLimit.maxAudioSeconds)
    }

    @Test func theAudioDurationIsFramesOverSampleRate() {
        #expect(AppleSpeechTimeLimit.audioDuration(frameCount: 160_000, sampleRate: 16_000) == 10)
        #expect(AppleSpeechTimeLimit.audioDuration(frameCount: 22_050, sampleRate: 44_100) == 0.5)
        #expect(AppleSpeechTimeLimit.audioDuration(frameCount: 0, sampleRate: 16_000) == 0)
    }

    @Test func anOddSampleRateGivesZeroDurationNotATrap() {
        for rate in [0, -16_000, Double.nan, Double.infinity] {
            #expect(AppleSpeechTimeLimit.audioDuration(frameCount: 16_000, sampleRate: rate) == 0, "rate \(rate)")
        }
        // A tiny positive rate is clamped, so `Duration.seconds` cannot overflow.
        let tiny = AppleSpeechTimeLimit.audioDuration(frameCount: .max, sampleRate: .leastNonzeroMagnitude)
        #expect(tiny == AppleSpeechTimeLimit.maxAudioSeconds)
        _ = Duration.seconds(AppleSpeechTimeLimit.limitSeconds(forAudioDuration: tiny))
    }

    // MARK: - Wiring

    /// The provider's STEP 5 runs through the limit, cancels the analyzer on
    /// timeout, and catches the timeout ahead of the "Transcription failed"
    /// catch-all. Source text is the last resort (see `ProductionSource`): the
    /// provider needs macOS 26 and Speech assets to call.
    @Test func theProviderRunsStepFiveUnderTheLimit() throws {
        let step5 = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Providers/Local/AppleSpeechAnalyzerProvider.swift",
            from: "let limitSeconds = AppleSpeechTimeLimit.limitSeconds(forAudioDuration: audioDuration)",
            to: "private static func collectTranscriptionResults("
        )
        let run = try #require(step5.range(of: "AppleSpeechTimeLimit.run("))
        let onTimeout = try #require(step5.range(of: "onTimeout: {"))
        let operation = try #require(step5.range(of: "operation: {"))
        let analyze = try #require(step5.range(of: "analyzer.analyzeSequence(from: audioFile)"))
        #expect(run.lowerBound < onTimeout.lowerBound)
        #expect(onTimeout.lowerBound < operation.lowerBound)
        #expect(operation.lowerBound < analyze.lowerBound)
        #expect(step5[onTimeout.upperBound..<operation.lowerBound].contains("analyzer.cancelAndFinishNow()"))

        let timeoutArm = try #require(step5.range(of: "} catch is AppleSpeechTimeLimit.TimedOut {"))
        let catchAll = try #require(step5.range(of: "} catch {"))
        #expect(timeoutArm.lowerBound < catchAll.lowerBound)
        let arm = step5[timeoutArm.upperBound..<catchAll.lowerBound]
        #expect(arm.contains("reason: \"Transcription timed out\""))
        #expect(arm.contains("\"locale\": locale.identifier"))
        #expect(!arm.contains("audioURL"), "the timeout breadcrumb must not carry the file name or path")
    }
}
