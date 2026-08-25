//
//  ResidentRuntimeClaim.swift
//  hyperwhisper
//
//  The claim → read → reload-once sequence a provider must follow before it
//  touches a heavy runtime that `ModelResidencyRegistry` is allowed to evict
//  underneath it.
//
//  A free, closure-injected namespace rather than a method on the provider, for
//  the same reason `TranscriptionCancellationPolicy` is one: the type that needs
//  it (`LibWhisperProvider`) cannot be unit-tested — it wants a real
//  `WhisperModelManager` and a real `whisper_context` — while the *sequencing*
//  is the entire defect and is testable on its own.
//

import Foundation

/// Acquires a memory-resident runtime for one in-flight operation, reloading it
/// exactly once if a memory-pressure eviction got there first.
///
/// **This doc is the canonical account of HYPERWHISPER-SQ's whisper.cpp arm.**
/// The call sites and the tests point here rather than restating it, so there is
/// one description to keep true when the behaviour changes.
///
/// ## The bug this exists to prevent
///
/// HYPERWHISPER-SQ, the whisper.cpp arm — the one that logs `No context
/// available for transcription`. (That Sentry group is fingerprinted on
/// category/kind/stage only, so every local provider lands in it; the Apple
/// Speech arm of the same group is a different fault, fixed separately by
/// `TranscriptionCancellationPolicy`. Do not read the two as one bug.)
///
/// `LibWhisperProvider.transcribe` used to read `whisperContext` into a local
/// BEFORE claiming residency. `ModelResidencyRegistry` is an actor, but it is
/// REENTRANT across the `await e.evict()` inside `evict(...)`, so the claim
/// could land while the evict closure was already running: `markBusy` refused it
/// (`phase == .freeing`), the refusal was invisible, and the transcription ran
/// on a `WhisperContext` whose underlying `whisper_context` had just been freed.
/// The user was told the audio might be corrupted.
///
/// ## Why claiming first is not on its own enough
///
/// Reordering fixes the read. It does not stop the OWNER from mutating its
/// runtime across a suspension point, which is the same reentrancy from the
/// other side: a teardown that suspends inside `releaseResources()` can resume
/// after a reload has installed a fresh runtime, and free THAT one. So the owner
/// must also serialise its own load against its own teardown and make a
/// superseded teardown recognise itself — `LibWhisperProvider` does this with an
/// `AsyncSerialLock` plus a generation counter. The invariant the two halves buy
/// together: **a runtime handed back as `.claimed` cannot be freed or
/// deregistered by a teardown that started before the claim.**
///
/// That serialisation is also why `reload` is cheap in the common case: it
/// queues behind whatever load is in flight and returns without doing anything
/// when the model it wanted is already resident.
///
/// ## The contract, which the caller must honor exactly
///
/// - `.claimed` — the caller holds EXACTLY ONE claim, named by the
///   `ModelResidencyRegistry.ClaimToken` handed back alongside the runtime, and
///   owns the matching `markIdle(_:)` for THAT token. It must run on every exit
///   path.
/// - `.unavailable`, or any error thrown out of `reload` — the caller holds NO
///   claim and has no token to release with. A token kept from an earlier
///   attempt names a claim this helper already repaid — every token names one
///   individual claim, by serial — so releasing it repays nothing and is
///   refused as unmatched. The one genuinely harmful release is with a token
///   belonging to another in-flight operation, which repays THAT operation's
///   claim and exposes its runtime to eviction mid-use.
///
/// The release BEFORE the reload is not tidiness, it is required — though no
/// longer for the reason it originally was. A claim now survives the
/// `deregister`/`register` a reload performs, so carrying one across is no
/// longer silently erased. What makes it wrong is arithmetic: after the reload
/// this helper takes a SECOND claim and returns exactly ONE token, so a first
/// claim carried across would be unrepayable by the caller and would pin the
/// model resident for the rest of the session. One token out, one claim
/// outstanding — the release below is what keeps those two numbers equal.
enum ResidentRuntimeClaim {

    /// The outcome of `acquire(claim:release:runtime:reload:)`.
    enum Acquisition<Runtime> {
        /// The runtime is claimed and safe to use for this operation. The caller
        /// holds exactly one claim, and the token beside the runtime is the only
        /// thing that can give it back — see `ModelResidencyRegistry.markIdle`.
        case claimed(Runtime, ModelResidencyRegistry.ClaimToken)
        /// No runtime could be claimed, even after one reload attempt. The
        /// caller holds nothing and must not release.
        ///
        /// - Parameter stillEvicting: the final refusal was
        ///   `ClaimResult.evicting` — the freshly reloaded runtime is being torn
        ///   down too, i.e. memory pressure is sustained. `false` means the
        ///   reload simply left nothing registered. Both are failures; they are
        ///   distinguished so the failure is legible in logs instead of arriving
        ///   as a bare "not available".
        case unavailable(stillEvicting: Bool)
    }

    /// Claims residency, then re-reads the runtime under that claim, reloading
    /// once if it is gone.
    ///
    /// The ordering below IS the invariant — claim first, read second, and never
    /// reload while holding a claim:
    ///
    /// 1. Claim. If the claim is honored AND the runtime is still there, it
    ///    cannot now be evicted mid-use (`evict` skips any slot with a live
    ///    claim on it), so hand it straight back with its token.
    /// 2. Otherwise the runtime is gone or going. Give back a claim we may have
    ///    taken on it, THEN reload.
    /// 3. Claim the freshly registered entry and re-read. Anything else is
    ///    `.unavailable`.
    ///
    /// There is exactly ONE reload attempt and deliberately no loop:
    /// `MemoryPressureMonitor` evicts with `minIdle: 0` on critical pressure, so
    /// under sustained pressure a freshly re-registered model is immediately
    /// eligible again — a retry loop would livelock instead of failing.
    ///
    /// - Parameters:
    ///   - claim: Takes a residency claim. Typically
    ///     `ModelResidencyRegistry.markBusy(id:)`.
    ///   - release: Gives back one honored claim, by its token. Typically
    ///     `markIdle(_:)`. Only ever called here for a claim this call itself
    ///     took, and only ever with that claim's own token.
    ///   - runtime: Reads the caller's current runtime, `nil` when it has been
    ///     freed. Must be re-read (not captured beforehand) — reading it early
    ///     is the bug. `async` so a test can hold the runtime behind an actor;
    ///     a synchronous property read converts to it.
    ///   - reload: Loads the runtime again, which is expected to `register` a
    ///     fresh residency entry. Anything it throws propagates unchanged.
    ///
    ///     It is handed the outcome of the FIRST attempt, because the two
    ///     refusals need different reloads and an owner that cannot tell them
    ///     apart gets one of them wrong:
    ///
    ///     - `.notResident` — nothing is registered, but what the owner can see
    ///       may be perfectly live (it built the runtime and has not registered
    ///       it yet). A reload that finds that runtime resident should return
    ///       without rebuilding: tearing a live context down to rebuild the same
    ///       one IS the destructive cold reload this whole sequence exists to
    ///       avoid.
    ///     - `.evicting` — a teardown is committed and running. Here the SAME
    ///       cheap "already resident, nothing to do" answer is a trap: the
    ///       context the owner can still see is the one being freed, so
    ///       returning early leaves the teardown to complete and the second
    ///       claim is refused too. The owner must force a genuine rebuild, which
    ///       both installs a runtime the teardown was not sent for and lets that
    ///       teardown recognise itself as superseded.
    /// - Returns: `.claimed` holding the live runtime, or `.unavailable`.
    static func acquire<Runtime>(
        claim: () async -> ModelResidencyRegistry.ClaimReceipt,
        release: (ModelResidencyRegistry.ClaimToken) async -> Void,
        runtime: () async -> Runtime?,
        reload: (ModelResidencyRegistry.ClaimResult) async throws -> Void
    ) async rethrows -> Acquisition<Runtime> {
        // One claim/read/release triple. Returns the live runtime AND the token
        // for the one claim outstanding on it, or `nil` for both with NOTHING
        // outstanding — including the awkward middle case, an honored claim on a
        // runtime that has already gone, where the claim protects nothing and is
        // given back here rather than carried into a reload that hands back a
        // different token.
        //
        // `runtime` and `token` are non-nil together or nil together, by
        // construction. They are returned as separate optionals rather than one
        // tuple because the caller binds them with `if let`, and there is no
        // shape that lets the compiler prove the pairing for us.
        func attempt() async -> (runtime: Runtime?, token: ModelResidencyRegistry.ClaimToken?, refusal: ModelResidencyRegistry.ClaimResult) {
            let receipt = await claim()
            guard let token = receipt.token else { return (nil, nil, receipt.result) }
            if let live = await runtime() {
                return (live, token, receipt.result)
            }
            await release(token)
            return (nil, nil, receipt.result)
        }

        let first = await attempt()
        if let live = first.runtime, let token = first.token {
            return .claimed(live, token)
        }
        // `first.refusal` is `.claimed` in the awkward middle case (honored
        // claim, runtime already gone) — the claim was given back inside
        // `attempt`, and the owner's own state already says "not resident", so
        // an ordinary reload rebuilds. Only `.evicting` needs forcing.
        try await reload(first.refusal)
        let second = await attempt()
        if let live = second.runtime, let token = second.token {
            return .claimed(live, token)
        }
        return .unavailable(stillEvicting: second.refusal == .evicting)
    }
}
