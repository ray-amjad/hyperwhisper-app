//
//  ModelResidencyRegistryTests.swift
//  hyperwhisperTests
//
//  First coverage for `ModelResidencyRegistry`, pinning the claim/refusal
//  contract that HYPERWHISPER-SQ depends on: `markBusy` now reports whether the
//  claim was honored, so a caller can tell "the model is yours" apart from "the
//  model is being freed right now" instead of reading a runtime that is on its
//  way out.
//

import Foundation
import Testing
@testable import HyperWhisper

// MARK: - Test doubles

/// Records what the registry actually evicted.
///
/// The `evict` closure is `@Sendable () async -> Void`, so the bookkeeping has
/// to live behind a concurrency boundary — capturing a plain `var` from those
/// closures would be a data race, not just a warning.
private actor EvictionProbe {
    private(set) var evicted: [String] = []

    func recordEviction(of id: String) {
        evicted.append(id)
    }

    func evictionCount(for id: String) -> Int {
        evicted.filter { $0 == id }.count
    }
}

/// A deterministic handshake between a test and an `evict` closure it is
/// deliberately blocking inside.
///
/// Built on `AsyncStream` rather than `Task.sleep` so the ordering is exact
/// instead of hopefully-long-enough: `signal()` buffers, so it is safe to call
/// before the waiter has started, and `open()` is permanent, so every gate a
/// test opens on its way out releases current *and* future waiters. That last
/// property is what keeps a failed `#expect` from parking a closure forever and
/// hanging the whole suite in CI.
private struct ResidencyGate {
    private let stream: AsyncStream<Void>
    private let continuation: AsyncStream<Void>.Continuation

    init() {
        let made = AsyncStream.makeStream(of: Void.self)
        self.stream = made.stream
        self.continuation = made.continuation
    }

    /// Release one waiter (buffered if nobody is waiting yet).
    func signal() {
        continuation.yield(())
    }

    /// Release every current and future waiter, permanently.
    func open() {
        continuation.finish()
    }

    /// Suspend until `signal()` or `open()`.
    func wait() async {
        var iterator = stream.makeAsyncIterator()
        _ = await iterator.next()
    }
}

// MARK: - Tests

/// Every test builds its OWN `ModelResidencyRegistry()`.
///
/// `ModelResidencyRegistry.shared` is off limits here: unit tests run inside
/// `HyperWhisper.app` via `TEST_HOST` (see `AutoDeleteCleanupServiceTests`), so
/// `.shared` is the running app's live registry — claiming or evicting in it
/// would fight the developer's real STT/LLM runtimes and could tear a loaded
/// model out from under them. The synthesized `init()` is internal and reachable
/// through `@testable import`.
struct ModelResidencyRegistryTests {

    /// Nothing to claim, so nothing is claimed. Under HYPERWHISPER-SQ this was
    /// indistinguishable from success at the call site.
    @Test func aClaimOnAnUnknownIdIsRefused() async {
        let registry = ModelResidencyRegistry()

        let honored = await registry.markBusy(id: "not-resident")

        #expect(honored == false)
    }

    /// The ordinary lifecycle: a claim on a resident model is honored, it
    /// protects the model from a pressure sweep, and one `markIdle` releases it
    /// again — the balance the `true` return is a promise about.
    @Test func aClaimOnAResidentModelIsHonoredAndMarkIdleBalancesIt() async {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        let honored = await registry.markBusy(id: "stt")
        #expect(honored == true)

        // Claimed: a sweep must leave it strictly alone.
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsWhileClaimed = await probe.evictionCount(for: "stt")
        #expect(evictionsWhileClaimed == 0)
        let whileClaimed = await registry.snapshot()
        #expect(whileClaimed.ids == ["stt"])

        // Balanced: the same entry is evictable again.
        await registry.markIdle(id: "stt")
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsAfterRelease = await probe.evictionCount(for: "stt")
        #expect(evictionsAfterRelease == 1)
        let afterRelease = await registry.snapshot()
        #expect(afterRelease.ids.isEmpty)
    }

    /// THE refusal HYPERWHISPER-SQ is about: once the evict closure is actually
    /// running (`phase == .freeing`) the runtime is committed to going away, so
    /// the claim is rejected. The caller used to get `Void` back and carried on
    /// with a context that was being released underneath it.
    @Test func aClaimIsRefusedWhileTheModelIsBeingFreed() async {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        let freeingStarted = ResidencyGate()
        let releaseEviction = ResidencyGate()

        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
            freeingStarted.signal()
            await releaseEviction.wait()
        }

        let eviction = Task {
            await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        }

        // The registry is an actor, but it is REENTRANT across `await e.evict()`
        // — which is precisely why a claim can land in this window at all.
        await freeingStarted.wait()
        let honored = await registry.markBusy(id: "stt")
        #expect(honored == false)

        releaseEviction.open()
        freeingStarted.open()
        await eviction.value

        // And the refusal was honest: the model really did go.
        let evictions = await probe.evictionCount(for: "stt")
        #expect(evictions == 1)
        let resident = await registry.snapshot()
        #expect(resident.ids.isEmpty)
    }

    /// The other half of the contract, and the one a "reject everything during
    /// eviction" simplification would quietly break: a model that has merely
    /// been SELECTED this round is still claimable, and that fresh claim saves
    /// it — `evict`'s phase 2 re-reads `useCount` and abandons the eviction.
    @Test func aClaimOnAModelOnlySelectedForEvictionIsStillHonored() async {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        let freeingStarted = ResidencyGate()
        let releaseEviction = ResidencyGate()

        for id in ["stt-a", "stt-b"] {
            await registry.register(id: id, tier: .stt) {
                await probe.recordEviction(of: id)
                freeingStarted.signal()
                await releaseEviction.wait()
            }
        }

        let eviction = Task {
            await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        }

        // Phase 1 selected BOTH synchronously; phase 2 is now blocked inside the
        // first one's closure. Which one that is comes out of a dictionary, so
        // ask the probe rather than assuming an order.
        await freeingStarted.wait()
        let frozen = await probe.evicted
        #expect(frozen.count == 1)
        let survivor = frozen.first == "stt-a" ? "stt-b" : "stt-a"

        // The survivor is `.selected`, not `.freeing` — this claim must land.
        let honored = await registry.markBusy(id: survivor)
        #expect(honored == true)

        releaseEviction.open()
        freeingStarted.open()
        await eviction.value

        // Saved: its closure was never called and it is still resident.
        let evicted = await probe.evicted
        #expect(evicted.count == 1)
        #expect(!evicted.contains(survivor))
        let resident = await registry.snapshot()
        #expect(resident.ids == [survivor])
    }

    /// `markIdle` on an entry nobody claimed must not drive `useCount` negative.
    ///
    /// If it underflowed, the two stray releases below would bank credit and the
    /// following honored claim would land on a still-negative count, leaving the
    /// model evictable while a transcription was live — the exact mid-use
    /// eviction the refcount exists to prevent.
    @Test func markIdleOnAnUnclaimedEntryDoesNotUnderflowTheRefcount() async {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        await registry.markIdle(id: "stt")
        await registry.markIdle(id: "stt")

        // One honored claim is still exactly one claim: the model is protected.
        let honored = await registry.markBusy(id: "stt")
        #expect(honored == true)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsWhileClaimed = await probe.evictionCount(for: "stt")
        #expect(evictionsWhileClaimed == 0)

        // And one `markIdle` still releases it: the model is not pinned either.
        await registry.markIdle(id: "stt")
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsAfterRelease = await probe.evictionCount(for: "stt")
        #expect(evictionsAfterRelease == 1)
        let resident = await registry.snapshot()
        #expect(resident.ids.isEmpty)
    }
}
