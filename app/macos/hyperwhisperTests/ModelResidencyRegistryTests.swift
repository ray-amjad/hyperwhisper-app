//
//  ModelResidencyRegistryTests.swift
//  hyperwhisperTests
//
//  First coverage for `ModelResidencyRegistry`, pinning the claim/refusal
//  contract. See `ModelResidencyRegistry.ClaimResult` for what each outcome
//  obliges a caller to do, and `ResidentRuntimeClaim.acquire` for the bug behind
//  it — neither is restated here.
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

// The blocking handshake these tests drive their interleavings with lives in
// `ResidencyGate.swift`, shared with `ResidentRuntimeLifecycleTests`.

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

        let claim = await registry.markBusy(id: "not-resident")

        #expect(claim.result == .notResident)
        #expect(claim.isHonored == false)
    }

    /// The ordinary lifecycle: a claim on a resident model is honored, it
    /// protects the model from a pressure sweep, and one `markIdle` releases it
    /// again — the balance the `true` return is a promise about.
    @Test func aClaimOnAResidentModelIsHonoredAndMarkIdleBalancesIt() async throws {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        let claim = await registry.markBusy(id: "stt")
        #expect(claim.result == .claimed)
        #expect(claim.isHonored)
        // An honored claim carries the token that repays it, and only that token
        // can — which is what the release below is spelled with.
        let token = try #require(claim.token)

        // Claimed: a sweep must leave it strictly alone.
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsWhileClaimed = await probe.evictionCount(for: "stt")
        #expect(evictionsWhileClaimed == 0)
        let whileClaimed = await registry.snapshot()
        #expect(whileClaimed.ids == ["stt"])

        // Balanced: the same entry is evictable again.
        await registry.markIdle(token)
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
        let claim = await registry.markBusy(id: "stt")
        // `.evicting`, NOT `.notResident`: the entry is still in the map, it is
        // the runtime behind it that is going away. Conflating the two is what
        // sent a caller into a destructive cold reload.
        #expect(claim.result == .evicting)
        #expect(claim.isHonored == false)

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
    /// it — `evict`'s phase 2 re-reads the claim ledger and abandons the
    /// eviction.
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
        let claim = await registry.markBusy(id: survivor)
        #expect(claim.result == .claimed)

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

    /// `markIdle` on an entry nobody claimed must not drive the claim ledger
    /// negative.
    ///
    /// If it underflowed, the two stray releases below would bank credit and the
    /// following honored claim would land on a still-negative count, leaving the
    /// model evictable while a transcription was live — the exact mid-use
    /// eviction the refcount exists to prevent.
    @Test func markIdleOnAnUnclaimedEntryDoesNotUnderflowTheRefcount() async throws {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        // Generation 0 is never issued (the registry's counter is 1-based), so
        // this is a token no `markBusy` can have handed out — the same "release
        // nobody took" these two lines always stood for.
        let neverIssued = ModelResidencyRegistry.ClaimToken(id: "stt", generation: 0)
        await registry.markIdle(neverIssued)
        await registry.markIdle(neverIssued)

        // One honored claim is still exactly one claim: the model is protected.
        let claim = await registry.markBusy(id: "stt")
        #expect(claim.result == .claimed)
        let token = try #require(claim.token)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsWhileClaimed = await probe.evictionCount(for: "stt")
        #expect(evictionsWhileClaimed == 0)

        // And one `markIdle` still releases it: the model is not pinned either.
        await registry.markIdle(token)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsAfterRelease = await probe.evictionCount(for: "stt")
        #expect(evictionsAfterRelease == 1)
        let resident = await registry.snapshot()
        #expect(resident.ids.isEmpty)
    }
}
