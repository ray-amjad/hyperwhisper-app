//
//  ResidentRuntimeClaimTests.swift
//  hyperwhisperTests
//
//  Pins the acquire sequence behind HYPERWHISPER-SQ's whisper.cpp arm ("No
//  context available for transcription"): claim residency FIRST, re-read the
//  runtime under that claim, reload exactly once if it is gone — and never leave
//  a claim outstanding on a path that does not return `.claimed`.
//
//  `LibWhisperProvider` itself is not unit-testable (it wants a real
//  `WhisperModelManager` and a real `whisper_context`), which is exactly why the
//  sequencing lives in a closure-injected helper. These tests are the coverage
//  that provider cannot have.
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
/// stale claim was released BEFORE the reload — the invariant that keeps a claim
/// from being erased by `register(id:tier:evict:)` resetting `useCount` to 0.
private actor ClaimProbe {
    /// Honored/refused answers for the 1st and 2nd `claim()`. Anything past the
    /// end of the script is refused; nothing should ever get that far.
    private let claimAnswers: [Bool]
    private let runtimeAfterReload: String?
    private let reloadError: Error?

    private var runtime: String?
    private var claimAttempts = 0

    private(set) var events: [String] = []
    private(set) var honoredClaims = 0
    private(set) var releases = 0
    private(set) var reloads = 0

    init(
        claimAnswers: [Bool],
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
    func claim() -> Bool {
        let honored = claimAttempts < claimAnswers.count ? claimAnswers[claimAttempts] : false
        claimAttempts += 1
        if honored {
            honoredClaims += 1
            events.append("claim.honored")
        } else {
            events.append("claim.refused")
        }
        return honored
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
}

// MARK: - Tests

struct ResidentRuntimeClaimTests {

    /// The ordinary case: nothing is evicting, the claim lands, the runtime is
    /// there. One claim outstanding, no reload, no wasted work.
    @Test func aLiveRuntimeUnderAnHonoredClaimIsReturnedWithoutReloading() async throws {
        let probe = ClaimProbe(claimAnswers: [true], runtime: "whisper-context")

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
        #expect(events == ["claim.honored"])
    }

    /// THE BUG. The eviction closure is already running, so the claim is refused
    /// (`phase == .freeing`) and the context is gone with it. That refusal used
    /// to be invisible and the caller transcribed on the freed context; now it
    /// is what triggers the reload, and the caller ends up with a live runtime
    /// and exactly one claim on it.
    @Test func aClaimRefusedDuringEvictionReloadsAndClaimsTheFreshRuntime() async throws {
        let probe = ClaimProbe(
            claimAnswers: [false, true],
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
        #expect(events == ["claim.refused", "reload", "claim.honored"])
    }

    /// The defensive window: the entry is still registered, so the claim IS
    /// honored, but the runtime behind it has already been freed. That claim
    /// protects nothing, and the reload's `register(...)` would reset the
    /// entry's `useCount` to 0 — erasing it beyond repayment, since `markIdle`
    /// floors at 0. So it has to be given back BEFORE the reload, not after.
    @Test func aStaleClaimOnAMissingRuntimeIsReleasedBeforeTheReload() async throws {
        let probe = ClaimProbe(
            claimAnswers: [true, true],
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
        #expect(events == ["claim.honored", "release", "reload", "claim.honored"])
        // Two claims taken, one given back: the caller still owns exactly one.
        let outstanding = await probe.outstandingClaims
        #expect(outstanding == 1)
    }

    /// A reload that fails (model file deleted, out of memory) must surface as
    /// itself — the caller's own error, not something this helper invented — and
    /// must leave nothing claimed, because a caller that gets a throw never
    /// reaches its `markIdle`.
    @Test func aFailedReloadPropagatesUnchangedAndLeaksNoClaim() async throws {
        let probe = ClaimProbe(
            claimAnswers: [false, true],
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
        #expect(events == ["claim.refused", "reload"])
    }

    /// Sustained pressure: the reload succeeds, the fresh runtime is evicted
    /// straight away too, and the second claim is refused as well. The answer is
    /// `.unavailable` after EXACTLY ONE reload — `MemoryPressureMonitor` evicts
    /// with `minIdle: 0` under critical pressure, so a retry loop here would
    /// reload and lose the model over and over instead of failing honestly.
    @Test func sustainedPressureGivesUpAfterExactlyOneReload() async throws {
        let probe = ClaimProbe(
            claimAnswers: [false, false],
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
        let reloads = await probe.reloads
        #expect(reloads == 1)
        let outstanding = await probe.outstandingClaims
        #expect(outstanding == 0)
        let events = await probe.events
        #expect(events == ["claim.refused", "reload", "claim.refused"])
    }
}
