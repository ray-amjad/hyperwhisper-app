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
/// and teardown both take `AsyncSerialLock`, load skips the rebuild when the
/// runtime is already resident unless the refusal forced one, both retire the
/// old runtime BEFORE freeing it, and each teardown carries the generation it
/// was REGISTERED for so it can recognise itself as superseded.
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
    /// Every refusal reason `reload(after:)` was handed, in order.
    private(set) var reloadReasons: [ModelResidencyRegistry.ClaimResult] = []

    /// Parks `teardown()` between the registry invoking the evict closure and
    /// that closure queueing on the lock — the window in which the entry is
    /// already `.freeing` but no owner state has been touched, so every local
    /// signal still says the runtime is resident.
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

    /// Mirrors `LibWhisperProvider`'s `reload` closure, whose whole job is to
    /// turn the refusal reason `ResidentRuntimeClaim.acquire` hands it into the
    /// decision of whether a rebuild is mandatory.
    func reload(after refusal: ModelResidencyRegistry.ClaimResult) async {
        reloadReasons.append(refusal)
        await load(forceReload: refusal == .evicting)
    }

    /// Mirrors `LibWhisperProvider.performLoad` under `loadModel`'s lock —
    /// INCLUDING its already-resident early return, which the fake used to omit.
    /// That omission was not cosmetic: without it the fake bumps the generation
    /// twice on every load and so granted the lifecycle invariant a guarantee
    /// production did not have — which is precisely the mechanism of the
    /// reload-no-op regression. `forceReload` mirrors the parameter that fixes
    /// it.
    ///
    /// (Explicit `lock()`/`unlock()` rather than production's `withLock`: this
    /// fake is an `actor`, and a closure formed in an actor-isolated context
    /// inherits that isolation, which is a needless question to ask of a test
    /// double. `AsyncSerialLockTests` covers `withLock` itself directly.)
    func load(forceReload: Bool = false) async {
        await lifecycleLock.lock()
        if !forceReload, runtime != nil {
            await lifecycleLock.unlock()
            return
        }
        if let retired = runtime {
            runtime = nil
            generation &+= 1
            await registry.deregister(id: id)
            freedRuntimes.append(retired)
        }
        loads += 1
        runtime = "runtime-\(loads)"
        generation &+= 1
        let installedGeneration = generation
        await registry.register(id: id, tier: .stt) { [weak self] in
            await self?.teardown(registeredGeneration: installedGeneration)
        }
        await lifecycleLock.unlock()
    }

    /// Mirrors `LibWhisperProvider.tearDownContext`, the registry's evict
    /// closure, which carries the generation it was REGISTERED for.
    func teardown(registeredGeneration: UInt64) async {
        teardownParked.signal()
        await parkTeardown.wait()

        await lifecycleLock.lock()
        guard registeredGeneration == generation else {
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

    /// `withLock` releases on the throwing path as well as the returning one,
    /// and this is why that matters: a lock leaked on a throw would wedge every
    /// later load and every memory-pressure teardown for the rest of the
    /// session. This used to assert the property about a test-local
    /// `do`/`catch` mirror of `loadModel`; it now drives the production helper
    /// that owns the obligation, so deleting the release really does fail here.
    @Test func aHolderThatThrowsStillReleasesTheLock() async {
        struct LoadFailure: Error {}
        let lock = AsyncSerialLock()

        var threw = false
        do {
            try await lock.withLock { () async throws -> Void in
                throw LoadFailure()
            }
        } catch {
            threw = true
        }
        #expect(threw)

        // Provable rather than merely non-hanging: the lock is free again, so a
        // fresh holder takes it without suspending.
        let returned = await lock.withLock { 42 }
        #expect(returned == 42)
        await lock.lock()
        await lock.unlock()
    }

    /// `withLock` is exclusive in its own right, not just a tidier spelling:
    /// bodies that suspend must still not overlap.
    @Test func withLockHoldersDoNotOverlapEitherAcrossASuspensionPoint() async {
        let lock = AsyncSerialLock()
        let tracker = OverlapTracker()

        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<8 {
                group.addTask {
                    await lock.withLock { () async -> Void in
                        await tracker.enter()
                        await Task.yield()
                        await tracker.leave()
                    }
                }
            }
        }

        let peak = await tracker.peak
        #expect(peak == 1)
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
        // is `.freeing`, so it reloads. `forceReload` because the refusal was
        // `.evicting` — exactly what the provider's reload closure passes on
        // that refusal, and what
        // `anEvictingRefusalRebuildsInsteadOfNoOppingOnTheDoomedRuntime` below
        // proves it must. That reload frees runtime-1 itself and installs
        // runtime-2.
        let claimDuringEviction = await registry.markBusy(id: "stt")
        #expect(claimDuringEviction.result == .evicting)
        await owner.load(forceReload: true)

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
        #expect(claimAfterEviction.result == .claimed)
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
        #expect(claimAfterEviction.result == .notResident)

        teardownParked.open()
    }

    /// The regression the `forceReload` parameter exists for, driven through the
    /// REAL `ResidentRuntimeClaim.acquire` rather than a hand-rolled sequence,
    /// so the wiring under test is the wiring that ships: the refusal reason has
    /// to reach `reload` for the owner to know it must rebuild.
    ///
    /// The window: `markBusy` answers `.evicting` because the sweep has moved
    /// the entry to `.freeing`, but the teardown has not taken the lifecycle
    /// lock yet — so the runtime is still installed and still looks resident.
    /// A reload that takes the "already resident, nothing to do" shortcut there
    /// returns without bumping the generation, the parked teardown then passes
    /// its own generation check and frees that very runtime, the second claim is
    /// refused, and the pass dies on a `localSpeechModelEvicted` that is not
    /// retryable. In other words the user loses the dictation to a shortcut
    /// taken a millisecond before a clean load.
    @Test func anEvictingRefusalRebuildsInsteadOfNoOppingOnTheDoomedRuntime() async {
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
        // Parked between `.freeing` and the lifecycle lock: runtime-1 is doomed
        // but every local signal still says it is resident.
        await teardownParked.wait()
        let stillLooksResident = await owner.currentRuntime()
        #expect(stillLooksResident == "runtime-1")

        let acquisition: ResidentRuntimeClaim.Acquisition<String> =
            await ResidentRuntimeClaim.acquire(
                claim: { await registry.markBusy(id: "stt") },
                release: { await registry.markIdle($0) },
                runtime: { await owner.currentRuntime() },
                reload: { await owner.reload(after: $0) }
            )

        // The reason reached the owner, and the owner rebuilt rather than
        // declaring the doomed runtime good enough.
        let reloadReasons = await owner.reloadReasons
        #expect(reloadReasons == [.evicting])
        #expect(acquisition.claimedRuntimeForTest == "runtime-2")

        parkTeardown.open()
        await eviction.value

        // The dictation survives: a live runtime, the superseded teardown stood
        // down, and only the runtime it was sent for was freed.
        let live = await owner.currentRuntime()
        #expect(live == "runtime-2")
        let freed = await owner.freedRuntimes
        #expect(freed == ["runtime-1"])
        let stoodDown = await owner.teardownsThatStoodDown
        #expect(stoodDown == 1)
        let resident = await registry.snapshot()
        #expect(resident.ids == ["stt"])

        teardownParked.open()
    }

    /// The other half, and the reason `forceReload` is a parameter rather than
    /// the new default: with no teardown in flight, a reload that finds the
    /// runtime already resident must NOT rebuild it. Tearing a live runtime down
    /// to build the identical one is the destructive cold reload the whole
    /// sequence exists to avoid, and it is the ordinary outcome of the
    /// `.notResident` refusal a caller sees while an owner is mid-registration.
    @Test func aCheapReloadLeavesAnAlreadyResidentRuntimeInPlace() async {
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
        await owner.load()

        let live = await owner.currentRuntime()
        #expect(live == "runtime-1")
        let freed = await owner.freedRuntimes
        #expect(freed.isEmpty)

        parkTeardown.open()
        teardownParked.open()
    }
}

private extension ResidentRuntimeClaim.Acquisition {
    /// The claimed runtime, or `nil` for `.unavailable`. (`ResidentRuntimeClaimTests`
    /// has its own copy; these are separate files and neither is production API.)
    var claimedRuntimeForTest: Runtime? {
        if case .claimed(let runtime, _) = self { return runtime }
        return nil
    }
}
