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
/// ## The contract, which the caller must honor exactly
///
/// - `.claimed` — the caller holds EXACTLY ONE claim and owns the matching
///   release (`ModelResidencyRegistry.markIdle`). It must run on every exit path.
/// - `.unavailable`, or any error thrown out of `reload` — the caller holds NO
///   claim and must NOT release. Releasing anyway would decrement somebody
///   else's claim and expose their runtime to eviction mid-use.
///
/// The release BEFORE the reload is not tidiness, it is required: `register(...)`
/// overwrites the entry with `useCount: 0`, so a claim held across a reload is
/// silently erased and can never be repaid (`markIdle` floors at 0), pinning the
/// model resident for the rest of the session.
enum ResidentRuntimeClaim {

    /// The outcome of `acquire(claim:release:runtime:reload:)`.
    enum Acquisition<Runtime> {
        /// The runtime is claimed and safe to use for this operation. The caller
        /// holds exactly one claim and owns the release.
        case claimed(Runtime)
        /// No runtime could be claimed, even after one reload attempt. The
        /// caller holds nothing and must not release.
        case unavailable
    }

    /// Claims residency, then re-reads the runtime under that claim, reloading
    /// once if it is gone.
    ///
    /// The ordering below IS the invariant — claim first, read second, and never
    /// reload while holding a claim:
    ///
    /// 1. Claim. If the claim is honored AND the runtime is still there, it
    ///    cannot now be evicted mid-use (`evict` skips any entry with
    ///    `useCount > 0`), so hand it straight back.
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
    ///   - claim: Takes a residency claim; `true` when it was honored. Typically
    ///     `ModelResidencyRegistry.markBusy(id:)`.
    ///   - release: Gives back one honored claim. Typically `markIdle(id:)`.
    ///     Only ever called here for a claim this call itself took.
    ///   - runtime: Reads the caller's current runtime, `nil` when it has been
    ///     freed. Must be re-read (not captured beforehand) — reading it early
    ///     is the bug. `async` so a test can hold the runtime behind an actor;
    ///     a synchronous property read converts to it.
    ///   - reload: Loads the runtime again, which is expected to `register` a
    ///     fresh residency entry. Anything it throws propagates unchanged.
    /// - Returns: `.claimed` holding the live runtime, or `.unavailable`.
    static func acquire<Runtime>(
        claim: () async -> Bool,
        release: () async -> Void,
        runtime: () async -> Runtime?,
        reload: () async throws -> Void
    ) async rethrows -> Acquisition<Runtime> {
        var honored = await claim()
        if honored, let live = await runtime() {
            return .claimed(live)
        }
        // A claim on a runtime that is already gone: it protects nothing, and
        // the reload below would erase it anyway. Give it back first.
        if honored {
            await release()
        }
        try await reload()
        honored = await claim()
        if honored, let live = await runtime() {
            return .claimed(live)
        }
        if honored {
            await release()
        }
        return .unavailable
    }
}
