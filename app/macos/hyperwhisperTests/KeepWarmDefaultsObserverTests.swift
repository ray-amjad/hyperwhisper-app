//
//  KeepWarmDefaultsObserverTests.swift
//  hyperwhisperTests
//
//  HYPERWHISPER-10D (#1648): every UserDefaults write in the process used to
//  hop to the main actor and read keepMicrophoneWarm through @AppStorage, then
//  call keepWarmManager.setEnabled. A slow cfprefsd froze the app on that read.
//  KeepWarmDefaultsObserver now reads the key on a background queue and calls
//  its sink (in the app: syncKeepWarmConfiguration -> keepWarmManager.setEnabled)
//  on the main actor only when the value changed.
//
//  Each test uses a private UserDefaults suite and its own serial read queue,
//  so no test reads or writes the host app's real defaults, and a test can
//  drain or park the read queue.
//

import Foundation
import Testing
@testable import HyperWhisper

/// Records every value the observer hands to its main-actor sink, standing in
/// for `keepWarmManager.setEnabled`.
@MainActor
private final class SetEnabledRecorder {
    var values: [Bool] = []
}

/// Counts `UserDefaults.didChangeNotification` posts, from any thread.
private final class DidChangePostCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    private var token: NSObjectProtocol?

    init() {
        token = NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification,
            object: nil,
            queue: nil
        ) { [weak self] _ in
            self?.increment()
        }
    }

    deinit {
        if let token { NotificationCenter.default.removeObserver(token) }
    }

    private func increment() {
        lock.lock()
        count += 1
        lock.unlock()
    }

    var value: Int {
        lock.lock()
        defer { lock.unlock() }
        return count
    }
}

@Suite(.serialized)
@MainActor
struct KeepWarmDefaultsObserverTests {

    private static let unrelatedKey = "KeepWarmDefaultsObserverTests.unrelatedKey"

    /// A private `UserDefaults` suite. Call `removePersistentDomain(forName:)` when done.
    private func makeDefaultsSuite() throws -> (name: String, defaults: UserDefaults) {
        let name = "KeepWarmDefaultsObserverTests-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: name))
        return (name, defaults)
    }

    private func makeReadQueue() -> DispatchQueue {
        DispatchQueue(label: "KeepWarmDefaultsObserverTests.read-\(UUID().uuidString)")
    }

    /// Waits until every read already queued on `readQueue` has run AND every
    /// main-queue delivery it made has run. The observer schedules its read
    /// from inside the notification, which UserDefaults posts synchronously
    /// within `set(_:forKey:)`, so after a write returns this covers it.
    private static func drain(_ readQueue: DispatchQueue) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            readQueue.async {
                DispatchQueue.main.async {
                    continuation.resume()
                }
            }
        }
    }

    /// Lets every block already on the main queue run.
    private static func drainMainQueue() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            DispatchQueue.main.async {
                continuation.resume()
            }
        }
    }

    /// Polls for up to 5 seconds, as the #880 tests do.
    private static func waitUntil(_ condition: @escaping @MainActor () -> Bool) async {
        let deadline = Date().addingTimeInterval(5)
        while Date() < deadline {
            if condition() { return }
            try? await Task.sleep(nanoseconds: 1_000_000)
        }
        if condition() { return }
        Issue.record("Timed out while waiting for the keep-warm sink")
    }

    // MARK: - Done when (#1648)

    @Test("An unrelated defaults write does not call setEnabled; a keepMicrophoneWarm change calls it once with the new value")
    func unrelatedWriteDoesNothingAndAChangeCallsOnce() async throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        suite.defaults.set(false, forKey: AudioDefaultsKey.keepMicrophoneWarm)

        let readQueue = makeReadQueue()
        let recorder = SetEnabledRecorder()
        let posts = DidChangePostCounter()
        let observer = KeepWarmDefaultsObserver(
            defaults: suite.defaults,
            initialValue: false,
            readQueue: readQueue
        ) { enabled in
            recorder.values.append(enabled)
        }
        observer.start()
        defer { observer.invalidate() }

        // An unrelated key: the old observer called setEnabled(false) here.
        let postsBefore = posts.value
        suite.defaults.set("anything", forKey: Self.unrelatedKey)
        // Guard against a vacuous pass: the write really did post the
        // notification before `set` returned, so the drain below covers it.
        #expect(posts.value > postsBefore)
        await Self.drain(readQueue)
        #expect(recorder.values.isEmpty)

        // The keep-warm key changes: exactly one call, with the new value.
        suite.defaults.set(true, forKey: AudioDefaultsKey.keepMicrophoneWarm)
        await Self.waitUntil { !recorder.values.isEmpty }
        await Self.drain(readQueue)
        #expect(recorder.values == [true])

        // Another unrelated write after the change: still no further call.
        suite.defaults.set(42, forKey: Self.unrelatedKey)
        await Self.drain(readQueue)
        #expect(recorder.values == [true])

        // Writing the same value again is not a change.
        suite.defaults.set(true, forKey: AudioDefaultsKey.keepMicrophoneWarm)
        await Self.drain(readQueue)
        #expect(recorder.values == [true])

        // And back off: one more call, with false.
        suite.defaults.set(false, forKey: AudioDefaultsKey.keepMicrophoneWarm)
        await Self.waitUntil { recorder.values.count >= 2 }
        await Self.drain(readQueue)
        #expect(recorder.values == [true, false])
    }

    // MARK: - Off the main actor

    @Test("The read runs on the read queue, not on the writing thread")
    func readRunsOffTheWritingThread() async throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }

        let readQueue = makeReadQueue()
        let recorder = SetEnabledRecorder()
        let observer = KeepWarmDefaultsObserver(
            defaults: suite.defaults,
            initialValue: false,
            readQueue: readQueue
        ) { enabled in
            recorder.values.append(enabled)
        }
        observer.start()
        defer { observer.invalidate() }

        // Park the read queue, as a slow cfprefsd would park the read.
        let gate = DispatchSemaphore(value: 0)
        readQueue.async { gate.wait() }
        defer { gate.signal() }

        // A main-actor write returns and the main queue keeps running while
        // the read waits: nothing is read or delivered on this thread.
        suite.defaults.set(true, forKey: AudioDefaultsKey.keepMicrophoneWarm)
        await Self.drainMainQueue()
        #expect(recorder.values.isEmpty)

        gate.signal()
        await Self.waitUntil { !recorder.values.isEmpty }
        await Self.drain(readQueue)
        #expect(recorder.values == [true])
    }

    @Test("A burst of writes while a read is pending makes one read and one call with the latest value")
    func burstCoalescesIntoOneCall() async throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }

        let readQueue = makeReadQueue()
        let recorder = SetEnabledRecorder()
        let observer = KeepWarmDefaultsObserver(
            defaults: suite.defaults,
            initialValue: false,
            readQueue: readQueue
        ) { enabled in
            recorder.values.append(enabled)
        }
        observer.start()
        defer { observer.invalidate() }

        let gate = DispatchSemaphore(value: 0)
        readQueue.async { gate.wait() }
        defer { gate.signal() }

        for index in 0..<20 {
            suite.defaults.set(index, forKey: Self.unrelatedKey)
        }
        suite.defaults.set(true, forKey: AudioDefaultsKey.keepMicrophoneWarm)
        suite.defaults.set(false, forKey: AudioDefaultsKey.keepMicrophoneWarm)
        suite.defaults.set(true, forKey: AudioDefaultsKey.keepMicrophoneWarm)

        gate.signal()
        await Self.waitUntil { !recorder.values.isEmpty }
        await Self.drain(readQueue)
        #expect(recorder.values == [true])
    }

    @Test("After invalidate, a change calls nothing")
    func invalidateStopsDelivery() async throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }

        let readQueue = makeReadQueue()
        let recorder = SetEnabledRecorder()
        let observer = KeepWarmDefaultsObserver(
            defaults: suite.defaults,
            initialValue: false,
            readQueue: readQueue
        ) { enabled in
            recorder.values.append(enabled)
        }
        observer.start()
        observer.invalidate()

        suite.defaults.set(true, forKey: AudioDefaultsKey.keepMicrophoneWarm)
        await Self.drain(readQueue)
        #expect(recorder.values.isEmpty)
    }

    // MARK: - Decoding

    @Test("The plain reader decodes keepMicrophoneWarm as @AppStorage with a false default does")
    func readerMatchesAppStorageDefault() throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }

        #expect(KeepWarmDefaultsObserver.isKeepMicrophoneWarmEnabled(in: suite.defaults) == false)
        suite.defaults.set(true, forKey: AudioDefaultsKey.keepMicrophoneWarm)
        #expect(KeepWarmDefaultsObserver.isKeepMicrophoneWarmEnabled(in: suite.defaults) == true)
        suite.defaults.set(false, forKey: AudioDefaultsKey.keepMicrophoneWarm)
        #expect(KeepWarmDefaultsObserver.isKeepMicrophoneWarmEnabled(in: suite.defaults) == false)
        #expect(AudioDefaultsKey.keepMicrophoneWarm == "keepMicrophoneWarm")
    }
}
