//
//  SoundEffectsManagerTests.swift
//  hyperwhisperTests
//

import Foundation
import os
import Testing
@testable import HyperWhisper

/// A fake player whose `play()` blocks until the test releases it, so a test
/// can see where and when the manager loads, prepares and plays a sound.
private final class BlockingSoundPlayer: SoundEffectPlayer, @unchecked Sendable {
    private struct State {
        var loadRanOnMainThread: Bool?
        var prepareRanOnMainThread: Bool?
        var prepareCount = 0
        var playRanOnMainThread: Bool?
        var playCount = 0
        var finishedPlay = false
    }

    private let blocks: Bool
    private let state = OSAllocatedUnfairLock(initialState: State())
    private let entered = DispatchSemaphore(value: 0)
    private let releaseSemaphore = DispatchSemaphore(value: 0)

    var volume: Float = 1
    var currentTime: TimeInterval = 3

    /// `blocks: false` makes `play()` return at once, for tests that play more
    /// than once.
    init(blocks: Bool = true) {
        self.blocks = blocks
    }

    var loadRanOnMainThread: Bool? { state.withLock { $0.loadRanOnMainThread } }
    var prepareRanOnMainThread: Bool? { state.withLock { $0.prepareRanOnMainThread } }
    var prepareCount: Int { state.withLock { $0.prepareCount } }
    var playRanOnMainThread: Bool? { state.withLock { $0.playRanOnMainThread } }
    var playCount: Int { state.withLock { $0.playCount } }
    var finishedPlay: Bool { state.withLock { $0.finishedPlay } }

    func recordLoad() {
        state.withLock { $0.loadRanOnMainThread = Thread.isMainThread }
    }

    @discardableResult func prepareToPlay() -> Bool {
        let onMain = Thread.isMainThread
        state.withLock { current in
            current.prepareRanOnMainThread = onMain
            current.prepareCount += 1
        }
        return true
    }

    @discardableResult func play() -> Bool {
        let onMain = Thread.isMainThread
        state.withLock { current in
            current.playRanOnMainThread = onMain
            current.playCount += 1
        }
        entered.signal()
        if blocks {
            _ = releaseSemaphore.wait(timeout: .now() + 5)
        }
        state.withLock { $0.finishedPlay = true }
        return true
    }

    /// True once `play()` has been entered, or false after `timeout` seconds.
    func waitUntilBlocked(timeout: Double = 5) async -> Bool {
        await Task.detached {
            self.entered.wait(timeout: .now() + timeout) == .success
        }.value
    }

    func release() {
        releaseSemaphore.signal()
    }
}

/// HYPERWHISPER-KY: the start and stop sounds must never load, prepare or play
/// on the main thread, because every AVAudioPlayer call can wait on a busy HAL.
@MainActor
struct SoundEffectsManagerTests {

    private static func makeManager(
        start: BlockingSoundPlayer,
        stop: BlockingSoundPlayer
    ) -> SoundEffectsManager {
        SoundEffectsManager { name in
            let player = name == "start1_quarter" ? start : stop
            player.recordLoad()
            return player
        }
    }

    @Test func startSoundReturnsWhilePlayerIsBlockedOffMain() async {
        let start = BlockingSoundPlayer()
        let stop = BlockingSoundPlayer()
        defer { start.release() }
        let manager = Self.makeManager(start: start, stop: stop)

        manager.playStartSound(volume: 0.5)

        // An inline play would have run to its 5 s release timeout by now.
        #expect(start.finishedPlay == false)
        #expect(await start.waitUntilBlocked())
        #expect(start.loadRanOnMainThread == false)
        #expect(start.playRanOnMainThread == false)
        #expect(start.volume == 0.5)
        #expect(start.currentTime == 0)
        #expect(stop.playRanOnMainThread == nil)
    }

    @Test func stopSoundPlaysStopPlayerOffMain() async {
        let start = BlockingSoundPlayer()
        let stop = BlockingSoundPlayer()
        defer { stop.release() }
        let manager = Self.makeManager(start: start, stop: stop)

        manager.playStopSound(volume: 0.25)

        #expect(stop.finishedPlay == false)
        #expect(await stop.waitUntilBlocked())
        #expect(stop.loadRanOnMainThread == false)
        #expect(stop.playRanOnMainThread == false)
        #expect(stop.volume == 0.25)
        #expect(start.playRanOnMainThread == nil)
    }

    /// Each player is prepared once, off main, at load; a play does not
    /// prepare it again (the per-play `prepareToPlay()` was one more HAL trip).
    @Test func preparesOnceAtLoadOffMainAndNotPerPlay() async {
        let start = BlockingSoundPlayer(blocks: false)
        let stop = BlockingSoundPlayer(blocks: false)
        let manager = Self.makeManager(start: start, stop: stop)

        manager.playStartSound(volume: 1)
        manager.playStartSound(volume: 1)
        manager.playStopSound(volume: 1)

        #expect(await start.waitUntilBlocked())
        #expect(await start.waitUntilBlocked())
        #expect(await stop.waitUntilBlocked())
        #expect(start.playCount == 2)
        #expect(stop.playCount == 1)
        #expect(start.prepareCount == 1)
        #expect(stop.prepareCount == 1)
        #expect(start.prepareRanOnMainThread == false)
        #expect(stop.prepareRanOnMainThread == false)
    }

    /// A sound file that fails to load leaves its sound silent, and the queue
    /// still plays the other sound.
    @Test func missingStartSoundDoesNotBlockStopSound() async {
        let stop = BlockingSoundPlayer()
        defer { stop.release() }
        let manager = SoundEffectsManager { name in
            if name == "start1_quarter" { return nil }
            return stop
        }

        manager.playStartSound(volume: 1)
        manager.playStopSound(volume: 0.75)

        #expect(await stop.waitUntilBlocked())
        #expect(stop.playRanOnMainThread == false)
        #expect(stop.volume == 0.75)
        #expect(stop.prepareCount == 1)
    }

    /// The queue is serial: a stop sound queued behind a start sound does not
    /// touch the HAL until the start sound's `play()` has returned.
    @Test func stopSoundWaitsForStartSoundOnTheSerialQueue() async {
        let start = BlockingSoundPlayer()
        let stop = BlockingSoundPlayer()
        defer {
            start.release()
            stop.release()
        }
        let manager = Self.makeManager(start: start, stop: stop)

        manager.playStartSound(volume: 1)
        manager.playStopSound(volume: 1)

        #expect(await start.waitUntilBlocked())
        #expect(await stop.waitUntilBlocked(timeout: 0.3) == false)
        #expect(stop.playCount == 0)

        start.release()

        #expect(await stop.waitUntilBlocked())
        #expect(start.finishedPlay)
        #expect(stop.playRanOnMainThread == false)
    }
}
