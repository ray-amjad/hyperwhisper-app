//
//  ResidentRuntimeClaimTests.swift
//  hyperwhisperTests
//
//  Pins the acquire sequence. See `ResidentRuntimeClaim.acquire` for what it is
//  for and why — that doc is the canonical account of HYPERWHISPER-SQ's
//  whisper.cpp arm and is not restated here.
//

import Foundation
import Testing
@testable import HyperWhisper

// MARK: - Test doubles

/// What the probe's `reload` throws when a test asks it to fail.
private struct ReloadFailure: Error, Equatable {
    let stage: String
}

/// Stands in for a provider + `ModelResidencyRegistry` pair: a scripted answer
/// for each successive claim, and a runtime that only `reload()` installs.
///
/// An actor because the helper calls these closures across suspension points, so
/// the bookkeeping cannot be a plain captured `var`. `events` is the point of
/// the whole double: the counts say what happened, the order says whether a
/// stale claim was released BEFORE the reload.
private actor ClaimProbe {
    /// Scripted answers for the 1st and 2nd `claim()`. Anything past the end of
    /// the script is `.notResident`; nothing should ever get that far.
    private let claimAnswers: [ModelResidencyRegistry.ClaimResult]
    private let runtimeAfterReload: String?
    private let reloadError: Error?

    private var runtime: String?
    private var claimAttempts = 0

    private(set) var events: [String] = []
    private(set) var honoredClaims = 0
    private(set) var releases = 0
    private(set) var reloads = 0

    init(
        claimAnswers: [ModelResidencyRegistry.ClaimResult],
        runtime: String?,
        runtimeAfterReload: String? = nil,
        reloadError: Error? = nil
    ) {
        self.claimAnswers = claimAnswers
        self.runtime = runtime
        self.runtimeAfterReload = runtimeAfterReload
        self.reloadError = reloadError
    }

    /// Stands in for `ModelResidencyRegistry.markBusy(id:)`.
    func claim() -> ModelResidencyRegistry.ClaimResult {
        let result = claimAttempts < claimAnswers.count ? claimAnswers[claimAttempts] : .notResident
        claimAttempts += 1
        if result.isHonored {
            honoredClaims += 1
        }
        events.append("claim.\(result)")
        return result
    }

    /// Stands in for `ModelResidencyRegistry.markIdle(id:)`.
    func release() {
        releases += 1
        events.append("release")
    }

    /// Stands in for reading `LibWhisperProvider.whisperContext`.
    func currentRuntime() -> String? {
        runtime
    }

    /// Stands in for `LibWhisperProvider.loadModel(named:)`.
    func reload() throws {
        reloads += 1
        events.append("reload")
        if let reloadError = reloadError {
            throw reloadError
        }
        runtime = runtimeAfterReload
    }

    /// Claims taken and never given back. MUST be 1 when `acquire` returns
    /// `.claimed` (the caller owns that one and balances it) and 0 on every
    /// other exit. Higher pins the model resident for the rest of the session;
    /// lower decrements a claim that belongs to somebody else.
    var outstandingClaims: Int {
        honoredClaims - releases
    }
}

private extension ResidentRuntimeClaim.Acquisition {
    /// The claimed runtime, or `nil` for `.unavailable`.
    var claimedRuntime: Runtime? {
        if case .claimed(let runtime) = self { return runtime }
        return nil
    }

    /// `nil` for `.claimed`; otherwise whether the FINAL refusal was a teardown
    /// in progress rather than a missing registration.
    var unavailableStillEvicting: Bool? {
        if case .unavailable(let stillEvicting) = self { return stillEvicting }
        return nil
    }
}

// MARK: - Tests

struct ResidentRuntimeClaimTests {

    /// The ordinary case: nothing is evicting, the claim lands, the runtime is
    /// there. One claim outstanding, no reload, no wasted work.
    @Test func aLiveRuntimeUnderAnHonoredClaimIsReturnedWithoutReloading() async throws {
        let probe = ClaimProbe(claimAnswers: [.claimed], runtime: "whisper-context")

        let acquisition: ResidentRuntimeClaim.Acquisition<String> =
            try await ResidentRuntimeClaim.acquire(
                claim: { await probe.claim() },
                release: { await probe.release() },
                runtime: { await probe.currentRuntime() },
                reload: { try await probe.reload() }
            )

        #expect(acquisition.claimedRuntime == "whisper-context")
        let reloads = await probe.reloads
        #expect(reloads == 0)
        let outstanding = await probe.outstandingClaims
        #expect(outstanding == 1)
        let events = await probe.events
        #expect(events == ["claim.claimed"])
    }

    /// THE BUG. The eviction closure is already running, so the claim is refused
    /// (`phase == .freeing`) and the context is gone with it. That refusal used
    /// to be invisible and the caller transcribed on the freed context; now it
    /// is what triggers the reload, and the caller ends up with a live runtime
    /// and exactly one claim on it.
    @Test func aClaimRefusedDuringEvictionReloadsAndClaimsTheFreshRuntime() async throws {
        let probe = ClaimProbe(
            claimAnswers: [.evicting, .claimed],
            runtime: nil,
            runtimeAfterReload: "reloaded-context"
        )

        let acquisition: ResidentRuntimeClaim.Acquisition<String> =
            try await ResidentRuntimeClaim.acquire(
                claim: { await probe.claim() },
                release: { await probe.release() },
                runtime: { await probe.currentRuntime() },
                reload: { try await probe.reload() }
            )

        #expect(acquisition.claimedRuntime == "reloaded-context")
        let reloads = await probe.reloads
        #expect(reloads == 1)
        let outstanding = await probe.outstandingClaims
        #expect(outstanding == 1)
        // And no release against the refused claim: a refusal means no claim was
        // taken, so releasing would decrement somebody else's.
        let events = await probe.events
        #expect(events == ["claim.evicting", "reload", "claim.claimed"])
    }

    /// The OTHER refusal, and the reason `markBusy` reports two: `.notResident`
    /// is not a teardown. It is also what a caller sees in the window where an
    /// owner has built its runtime but not yet registered it. The recovery is
    /// the same shape — reload, which under `LibWhisperProvider`'s lifecycle
    /// lock queues behind that owner and returns without rebuilding anything.
    @Test func aClaimRefusedBecauseNothingIsResidentAlsoReloadsAndClaims() async throws {
        let probe = ClaimProbe(
            claimAnswers: [.notResident, .claimed],
            runtime: nil,
            runtimeAfterReload: "reloaded-context"
        )

        let acquisition: ResidentRuntimeClaim.Acquisition<String> =
            try await ResidentRuntimeClaim.acquire(
                claim: { await probe.claim() },
                release: { await probe.release() },
                runtime: { await probe.currentRuntime() },
                reload: { try await probe.reload() }
            )

        #expect(acquisition.claimedRuntime == "reloaded-context")
        let outstanding = await probe.outstandingClaims
        #expect(outstanding == 1)
        let events = await probe.events
        #expect(events == ["claim.notResident", "reload", "claim.claimed"])
    }

    /// The defensive window: the entry is still registered, so the claim IS
    /// honored, but the runtime behind it has already been freed. The claim must
    /// be given back BEFORE the reload — `acquire`'s doc says why carrying it
    /// across would pin the model resident for the session.
    @Test func aStaleClaimOnAMissingRuntimeIsReleasedBeforeTheReload() async throws {
        let probe = ClaimProbe(
            claimAnswers: [.claimed, .claimed],
            runtime: nil,
            runtimeAfterReload: "reloaded-context"
        )

        let acquisition: ResidentRuntimeClaim.Acquisition<String> =
            try await ResidentRuntimeClaim.acquire(
                claim: { await probe.claim() },
                release: { await probe.release() },
                runtime: { await probe.currentRuntime() },
                reload: { try await probe.reload() }
            )

        #expect(acquisition.claimedRuntime == "reloaded-context")
        let events = await probe.events
        #expect(events == ["claim.claimed", "release", "reload", "claim.claimed"])
        // Two claims taken, one given back: the caller still owns exactly one.
        let outstanding = await probe.outstandingClaims
        #expect(outstanding == 1)
    }

    /// The same stale claim, but on the SECOND attempt — the branch that had no
    /// coverage at all. The reload registered a fresh entry (so the claim is
    /// honored) and the runtime was freed again before we could read it, so
    /// `acquire` must give that claim back too and report `.unavailable`.
    /// Keeping it would pin the model resident for the rest of the session,
    /// since the caller is told it holds nothing and never releases.
    @Test func aStaleClaimOnTheSecondAttemptIsAlsoReleased() async throws {
        let probe = ClaimProbe(
            claimAnswers: [.evicting, .claimed],
            runtime: nil,
            runtimeAfterReload: nil
        )

        let acquisition: ResidentRuntimeClaim.Acquisition<String> =
            try await ResidentRuntimeClaim.acquire(
                claim: { await probe.claim() },
                release: { await probe.release() },
                runtime: { await probe.currentRuntime() },
                reload: { try await probe.reload() }
            )

        #expect(acquisition.claimedRuntime == nil)
        // The final refusal was not a teardown — the claim was honored, the
        // runtime was simply not there.
        #expect(acquisition.unavailableStillEvicting == false)
        let outstanding = await probe.outstandingClaims
        #expect(outstanding == 0)
        let events = await probe.events
        #expect(events == ["claim.evicting", "reload", "claim.claimed", "release"])
    }

    /// A reload that fails (model file deleted, out of memory) must surface as
    /// itself — the caller's own error, not something this helper invented — and
    /// must leave nothing claimed, because a caller that gets a throw never
    /// reaches its `markIdle`.
    @Test func aFailedReloadPropagatesUnchangedAndLeaksNoClaim() async throws {
        let probe = ClaimProbe(
            claimAnswers: [.evicting, .claimed],
            runtime: nil,
            reloadError: ReloadFailure(stage: "load-model")
        )

        var caught: Error?
        do {
            let acquisition: ResidentRuntimeClaim.Acquisition<String> =
                try await ResidentRuntimeClaim.acquire(
                    claim: { await probe.claim() },
                    release: { await probe.release() },
                    runtime: { await probe.currentRuntime() },
                    reload: { try await probe.reload() }
                )
            Issue.record("acquire should have rethrown the reload failure, got \(acquisition.claimedRuntime ?? "unavailable")")
        } catch {
            caught = error
        }

        #expect(caught as? ReloadFailure == ReloadFailure(stage: "load-model"))
        let reloads = await probe.reloads
        #expect(reloads == 1)
        let outstanding = await probe.outstandingClaims
        #expect(outstanding == 0)
        // The second scripted claim is never reached: acquire left immediately.
        let events = await probe.events
        #expect(events == ["claim.evicting", "reload"])
    }

    /// Sustained pressure: the reload succeeds, the fresh runtime is evicted
    /// straight away too, and the second claim is refused as well. The answer is
    /// `.unavailable` after EXACTLY ONE reload — see `acquire`'s doc for why a
    /// retry loop would livelock rather than recover.
    @Test func sustainedPressureGivesUpAfterExactlyOneReload() async throws {
        let probe = ClaimProbe(
            claimAnswers: [.evicting, .evicting],
            runtime: nil,
            runtimeAfterReload: "reloaded-context"
        )

        let acquisition: ResidentRuntimeClaim.Acquisition<String> =
            try await ResidentRuntimeClaim.acquire(
                claim: { await probe.claim() },
                release: { await probe.release() },
                runtime: { await probe.currentRuntime() },
                reload: { try await probe.reload() }
            )

        #expect(acquisition.claimedRuntime == nil)
        // And it says WHY: still being torn down, so this is sustained pressure
        // rather than a model that was never registered.
        #expect(acquisition.unavailableStillEvicting == true)
        let reloads = await probe.reloads
        #expect(reloads == 1)
        let outstanding = await probe.outstandingClaims
        #expect(outstanding == 0)
        let events = await probe.events
        #expect(events == ["claim.evicting", "reload", "claim.evicting"])
    }
}
