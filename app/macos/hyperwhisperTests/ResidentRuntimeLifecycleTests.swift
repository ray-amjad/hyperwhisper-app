//
//  ResidentRuntimeLifecycleTests.swift
//  hyperwhisperTests
//
//  Covers the OWNER half of the HYPERWHISPER-SQ fix: `AsyncSerialLock`, and the
//  lock + generation pattern `LibWhisperProvider` uses to keep a superseded
//  teardown from freeing a runtime that has just been handed out.
//  `ResidentRuntimeClaim.acquire` documents the bug; this file does not restate
//  it.
//

import Foundation
import Testing
@testable import HyperWhisper

// MARK: - Test doubles

/// Highest number of tasks ever inside the critical section at once.
private actor OverlapTracker {
    private var current = 0
    private(set) var peak = 0

    func enter() {
        current += 1
        peak = max(peak, current)
    }

    func leave() {
        current -= 1
    }
}

/// A runtime owner that follows `LibWhisperProvider`'s lifecycle exactly: load
/// and teardown both take `AsyncSerialLock`, both retire the old runtime BEFORE
/// freeing it, and teardown captures its generation before it queues so it can
/// recognise itself as superseded.
///
/// It is a stand-in, not the provider: `LibWhisperProvider` needs a real
/// `WhisperModelManager` and a real `whisper_context`, so it cannot be built in
/// a unit test — the same reason `ResidentRuntimeClaim` exists as a free
/// function. An `actor` reproduces the hazard faithfully, because actors are
/// reentrant across `await` in exactly the way the provider's plain-class
/// methods are.
private actor FakeRuntimeOwner {
    private let registry: ModelResidencyRegistry
    private let id: String
    private let lifecycleLock = AsyncSerialLock()
    private var generation: UInt64 = 0
    private var runtime: String?
    private var loads = 0

    private(set) var freedRuntimes: [String] = []
    private(set) var teardownsThatStoodDown = 0

    /// Parks `teardown()` between capturing its generation and queueing on the
    /// lock — the window in which the registry has invoked the evict closure but
    /// no state has been touched yet.
    private let parkTeardown: ResidencyGate
    /// Signalled when `teardown()` reaches that park.
    private let teardownParked: ResidencyGate

    init(registry: ModelResidencyRegistry, id: String, parkTeardown: ResidencyGate, teardownParked: ResidencyGate) {
        self.registry = registry
        self.id = id
        self.parkTeardown = parkTeardown
        self.teardownParked = teardownParked
    }

    func currentRuntime() -> String? {
        runtime
    }

    /// Mirrors `LibWhisperProvider.performLoad` under `loadModel`'s lock.
    func load() async {
        await lifecycleLock.lock()
        if let retired = runtime {
            runtime = nil
            generation &+= 1
            await registry.deregister(id: id)
            freedRuntimes.append(retired)
        }
        loads += 1
        runtime = "runtime-\(loads)"
        generation &+= 1
        await registry.register(id: id, tier: .stt) { [weak self] in
            await self?.teardown()
        }
        await lifecycleLock.unlock()
    }

    /// Mirrors `LibWhisperProvider.cleanup`, the registry's evict closure.
    func teardown() async {
        let entryGeneration = generation
        teardownParked.signal()
        await parkTeardown.wait()

        await lifecycleLock.lock()
        guard entryGeneration == generation else {
            teardownsThatStoodDown += 1
            await lifecycleLock.unlock()
            return
        }
        let retired = runtime
        runtime = nil
        generation &+= 1
        await registry.deregister(id: id)
        if let retired = retired {
            freedRuntimes.append(retired)
        }
        await lifecycleLock.unlock()
    }
}

// MARK: - AsyncSerialLock

struct AsyncSerialLockTests {

    /// The property an `actor` does NOT give you. Every task below suspends
    /// inside its critical section; with plain actor isolation the others would
    /// pour in at that suspension point, which is the reentrancy behind
    /// HYPERWHISPER-SQ.
    @Test func holdersDoNotOverlapEvenAcrossASuspensionPoint() async {
        let lock = AsyncSerialLock()
        let tracker = OverlapTracker()

        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<8 {
                group.addTask {
                    await lock.lock()
                    await tracker.enter()
                    await Task.yield()
                    await tracker.leave()
                    await lock.unlock()
                }
            }
        }

        let peak = await tracker.peak
        #expect(peak == 1)
    }

    /// `loadModel` unlocks in its `catch` as well as on the way out, and this is
    /// why that matters: a lock leaked on a throw would wedge every later load
    /// and every memory-pressure teardown for the rest of the session.
    @Test func aHolderThatThrowsStillReleasesTheLock() async {
        struct LoadFailure: Error {}
        let lock = AsyncSerialLock()

        func guardedWork(shouldThrow: Bool) async throws {
            await lock.lock()
            do {
                if shouldThrow { throw LoadFailure() }
            } catch {
                await lock.unlock()
                throw error
            }
            await lock.unlock()
        }

        var threw = false
        do {
            try await guardedWork(shouldThrow: true)
        } catch {
            threw = true
        }
        #expect(threw)

        // Provable rather than merely non-hanging: the lock is free again, so a
        // fresh holder takes it without suspending.
        try? await guardedWork(shouldThrow: false)
        await lock.lock()
        await lock.unlock()
    }
}

// MARK: - The lifecycle invariant

struct ResidentRuntimeLifecycleTests {

    /// THE Group 1 invariant: **a runtime handed back as `.claimed` cannot be
    /// freed or deregistered by a teardown that started before the claim.**
    ///
    /// The interleaving: a pressure sweep marks the entry `.freeing` and invokes
    /// the evict closure; before that closure touches anything, a transcription
    /// has its claim refused, reloads, and installs a fresh runtime. The old
    /// teardown then resumes. Without the generation check it would free the
    /// FRESH runtime and deregister its entry — reproducing the very bug the fix
    /// is for, from the other side.
    @Test func aSupersededTeardownLeavesTheFreshRuntimeAlone() async {
        let registry = ModelResidencyRegistry()
        let teardownParked = ResidencyGate()
        let parkTeardown = ResidencyGate()
        let owner = FakeRuntimeOwner(
            registry: registry,
            id: "stt",
            parkTeardown: parkTeardown,
            teardownParked: teardownParked
        )

        await owner.load()

        let eviction = Task {
            await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        }
        await teardownParked.wait()

        // The transcription that lands mid-eviction: refused because the entry
        // is `.freeing`, so it reloads. That reload frees runtime-1 itself and
        // installs runtime-2.
        let claimDuringEviction = await registry.markBusy(id: "stt")
        #expect(claimDuringEviction == .evicting)
        await owner.load()

        // Now let the parked teardown run. It was sent for the generation that
        // the reload has already retired.
        parkTeardown.open()
        await eviction.value

        let live = await owner.currentRuntime()
        #expect(live == "runtime-2")
        let freed = await owner.freedRuntimes
        #expect(freed == ["runtime-1"])
        let stoodDown = await owner.teardownsThatStoodDown
        #expect(stoodDown == 1)

        // And the fresh entry is intact, so the next pass gets a real claim
        // rather than being told the model could not be reclaimed.
        let claimAfterEviction = await registry.markBusy(id: "stt")
        #expect(claimAfterEviction == .claimed)
        let resident = await registry.snapshot()
        #expect(resident.ids == ["stt"])

        teardownParked.open()
    }

    /// The control case, so the test above is not passing for the wrong reason:
    /// with nothing superseding it, the teardown really does free the runtime
    /// and drop the entry.
    @Test func anUnsupersededTeardownFreesTheRuntime() async {
        let registry = ModelResidencyRegistry()
        let teardownParked = ResidencyGate()
        let parkTeardown = ResidencyGate()
        let owner = FakeRuntimeOwner(
            registry: registry,
            id: "stt",
            parkTeardown: parkTeardown,
            teardownParked: teardownParked
        )

        await owner.load()
        parkTeardown.open()

        await registry.evict(aggressive: false, reason: "test", minIdle: 0)

        let live = await owner.currentRuntime()
        #expect(live == nil)
        let freed = await owner.freedRuntimes
        #expect(freed == ["runtime-1"])
        let stoodDown = await owner.teardownsThatStoodDown
        #expect(stoodDown == 0)
        let claimAfterEviction = await registry.markBusy(id: "stt")
        #expect(claimAfterEviction == .notResident)

        teardownParked.open()
    }
}
