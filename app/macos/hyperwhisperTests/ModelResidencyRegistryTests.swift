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

    // MARK: - Claim identity across a re-registration (HYPERWHISPER-SQ, second arm)

    /// A live claim survives an IN-PLACE re-registration of its slot.
    ///
    /// Buys back the first half of the residency arm: `register` used to install
    /// a fresh `Entry(useCount: 0)`, so a model switch landing while a
    /// transcription was in flight erased that transcription's claim, and the
    /// very next pressure sweep saw an idle model and freed the runtime it was
    /// decoding on — `provider_not_available`, or a use-after-free inside
    /// whisper.cpp.
    @Test func aClaimSurvivesAnInPlaceReRegisterOfItsSlot() async throws {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        let claim = await registry.markBusy(id: "stt")
        #expect(claim.result == .claimed)
        let token = try #require(claim.token)

        // The switch: same slot, new runtime, entry overwritten in place.
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }
        let outstandingAfterReRegister = await registry.outstandingClaims(id: "stt")
        #expect(outstandingAfterReRegister == 1)

        // Still owed, therefore still not a victim.
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsWhileClaimed = await probe.evictionCount(for: "stt")
        #expect(evictionsWhileClaimed == 0)
        let whileClaimed = await registry.snapshot()
        #expect(whileClaimed.ids == ["stt"])

        // And the survivor is still repayable by its own token — protecting the
        // claim must not mean pinning the slot for the session.
        await registry.markIdle(token)
        let outstandingAfterRelease = await registry.outstandingClaims(id: "stt")
        #expect(outstandingAfterRelease == 0)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsAfterRelease = await probe.evictionCount(for: "stt")
        #expect(evictionsAfterRelease == 1)
        let resident = await registry.snapshot()
        #expect(resident.ids.isEmpty)
    }

    /// The same survival, across the `deregister` → `register` pair — which is
    /// the shape a model switch ACTUALLY takes on both STT providers
    /// (`LibWhisperProvider.performLoad`, `ParakeetProvider` via
    /// `ParakeetRuntime.reset()`). There is a window in which no entry exists at
    /// all, so this is the case a fix that only carried a refcount forward
    /// inside `register` would miss entirely: there is nothing to carry it from.
    @Test func aClaimSurvivesADeregisterThenReRegisterOfItsSlot() async throws {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        let claim = await registry.markBusy(id: "stt")
        let token = try #require(claim.token)

        // The provider's switch: the old runtime is torn down and deregistered,
        // then the new one is registered. The claim spans the gap.
        await registry.deregister(id: "stt")
        let outstandingWhileUnregistered = await registry.outstandingClaims(id: "stt")
        #expect(outstandingWhileUnregistered == 1)
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsWhileClaimed = await probe.evictionCount(for: "stt")
        #expect(evictionsWhileClaimed == 0)
        let whileClaimed = await registry.snapshot()
        #expect(whileClaimed.ids == ["stt"])

        await registry.markIdle(token)
        let outstandingAfterRelease = await registry.outstandingClaims(id: "stt")
        #expect(outstandingAfterRelease == 0)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsAfterRelease = await probe.evictionCount(for: "stt")
        #expect(evictionsAfterRelease == 1)
    }

    /// THE headline defect: a release arriving late from the PREVIOUS
    /// registration must repay its own claim and nobody else's.
    ///
    /// `markIdle(id:)` keyed on the slot alone, so pass A finishing a few
    /// seconds after a model switch decremented the count that pass B — actively
    /// decoding on the new runtime — was being protected by. The model was then
    /// freed mid-use by the next sweep. Here A's release must consume A's claim
    /// and leave B's standing.
    @Test func aStaleReleaseDoesNotFreeALiveClaimOnANewGeneration() async throws {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        // Pass A claims the outgoing runtime.
        let claimA = await registry.markBusy(id: "stt")
        let tokenA = try #require(claimA.token)

        // The model switch.
        await registry.deregister(id: "stt")
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        // Pass B claims the incoming one. Two live claims, two identities.
        let claimB = await registry.markBusy(id: "stt")
        #expect(claimB.result == .claimed)
        let tokenB = try #require(claimB.token)
        #expect(tokenA != tokenB)
        let bothOutstanding = await registry.outstandingClaims(id: "stt")
        #expect(bothOutstanding == 2)

        // Pass A finishes LATE and repays itself — out of its OWN claim.
        await registry.markIdle(tokenA)
        let afterStaleRelease = await registry.outstandingClaims(id: "stt")
        #expect(afterStaleRelease == 1)

        // B is still decoding, so the sweep must leave the runtime alone.
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsWhileBIsLive = await probe.evictionCount(for: "stt")
        #expect(evictionsWhileBIsLive == 0)
        let whileBIsLive = await registry.snapshot()
        #expect(whileBIsLive.ids == ["stt"])

        // Only B's own release can free the slot.
        await registry.markIdle(tokenB)
        let afterBReleases = await registry.outstandingClaims(id: "stt")
        #expect(afterBReleases == 0)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsAfterB = await probe.evictionCount(for: "stt")
        #expect(evictionsAfterB == 1)
    }

    /// A release naming a claim that was never issued is IGNORED — even while a
    /// real claim on the same slot is live.
    ///
    /// This is the anonymous-release bug at its smallest. The old
    /// `markIdle(id:)` could not tell a bogus or duplicate release from a real
    /// one; it decremented whatever count it found, so a release nobody was
    /// issued consumed the live claim and handed the next sweep a model that was
    /// in use. Complements — does not replace —
    /// `markIdleOnAnUnclaimedEntryDoesNotUnderflowTheRefcount`, which covers
    /// stray releases arriving BEFORE any claim exists.
    @Test func aReleaseOfANeverIssuedTokenIsIgnored() async throws {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        // A real, live claim on the slot — a transcription in flight.
        let claim = await registry.markBusy(id: "stt")
        let token = try #require(claim.token)

        // Generation 0 is never issued (the registry's counter is 1-based), so
        // this token provably names no claim that ever existed.
        let neverIssued = ModelResidencyRegistry.ClaimToken(id: "stt", generation: 0)
        await registry.markIdle(neverIssued)
        await registry.markIdle(neverIssued)

        // The live claim is untouched, and the model is still protected.
        let outstandingAfterBogusReleases = await registry.outstandingClaims(id: "stt")
        #expect(outstandingAfterBogusReleases == 1)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsWhileClaimed = await probe.evictionCount(for: "stt")
        #expect(evictionsWhileClaimed == 0)
        let whileClaimed = await registry.snapshot()
        #expect(whileClaimed.ids == ["stt"])

        // Nor did the ignored releases bank credit against the real one: the
        // real token still repays exactly its own claim, and only then.
        await registry.markIdle(token)
        let outstandingAfterRealRelease = await registry.outstandingClaims(id: "stt")
        #expect(outstandingAfterRealRelease == 0)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsAfterRelease = await probe.evictionCount(for: "stt")
        #expect(evictionsAfterRelease == 1)
    }

    /// The anti-pin test, and the price of the fix stated as an assertion.
    ///
    /// Making claims survive a re-registration means a lost release is no longer
    /// silently healed by the next `register` — it would pin the slot resident
    /// for the rest of the session. So overlapping claims spanning a switch must
    /// each be repayable exactly once: the ledger goes 2 → 1 → 0, the slot stays
    /// protected until the LAST one is repaid, and a repeat release of an
    /// already-repaid token cannot drive it below zero either.
    ///
    /// (Both claims here are taken against the SAME registration, so they share
    /// a generation and their tokens are equal by value. That is the
    /// concurrent-consumer shape the per-generation refcount exists for, and it
    /// is why the ledger counts claims per generation instead of holding a set
    /// of distinct tokens.)
    @Test func everyClaimIsRepaidExactlyOnceAcrossAReRegister() async throws {
        let registry = ModelResidencyRegistry()
        let probe = EvictionProbe()
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }

        let claimA = await registry.markBusy(id: "stt")
        let tokenA = try #require(claimA.token)
        let claimB = await registry.markBusy(id: "stt")
        let tokenB = try #require(claimB.token)
        let bothOutstanding = await registry.outstandingClaims(id: "stt")
        #expect(bothOutstanding == 2)

        // The switch lands with BOTH claims still in flight.
        await registry.deregister(id: "stt")
        await registry.register(id: "stt", tier: .stt) {
            await probe.recordEviction(of: "stt")
        }
        let outstandingAcrossTheSwitch = await registry.outstandingClaims(id: "stt")
        #expect(outstandingAcrossTheSwitch == 2)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsWhileBothClaimed = await probe.evictionCount(for: "stt")
        #expect(evictionsWhileBothClaimed == 0)

        // One of the two finishes. Partial repayment is not repayment: the other
        // consumer is still using the runtime.
        await registry.markIdle(tokenB)
        let outstandingAfterFirstRelease = await registry.outstandingClaims(id: "stt")
        #expect(outstandingAfterFirstRelease == 1)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsAfterFirstRelease = await probe.evictionCount(for: "stt")
        #expect(evictionsAfterFirstRelease == 0)
        let stillResident = await registry.snapshot()
        #expect(stillResident.ids == ["stt"])

        // The last one finishes: the ledger is empty and the slot is a victim
        // again. Nothing is pinned by having survived the switch.
        await registry.markIdle(tokenA)
        let outstandingAfterLastRelease = await registry.outstandingClaims(id: "stt")
        #expect(outstandingAfterLastRelease == 0)
        await registry.evict(aggressive: false, reason: "test", minIdle: 0)
        let evictionsAfterLastRelease = await probe.evictionCount(for: "stt")
        #expect(evictionsAfterLastRelease == 1)
        let resident = await registry.snapshot()
        #expect(resident.ids.isEmpty)

        // Exactly once, the other way round: a repeat release of a spent token
        // is unmatched and changes nothing.
        await registry.markIdle(tokenA)
        let outstandingAfterDoubleRelease = await registry.outstandingClaims(id: "stt")
        #expect(outstandingAfterDoubleRelease == 0)
    }
}
