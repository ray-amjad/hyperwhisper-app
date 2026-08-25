//
//  ModelResidencyRegistry.swift
//  hyperwhisper
//
//  Minimal model-residency registry — the Stage 1 substrate for memory-pressure
//  eviction and residency telemetry.
//
//  Heavy local models (the Parakeet/Whisper STT runtimes, the local LLM server)
//  register a weak `evict` closure here when they load, mark themselves busy
//  around an in-flight transcription, and deregister when freed. The
//  `MemoryPressureMonitor` uses this to reclaim IDLE models under macOS memory
//  pressure WITHOUT disturbing a transcription that is currently running.
//
//  This is deliberately small: it tracks what is resident, how many in-flight
//  uses each model has, and how to evict it — plus the telemetry (cold-load vs cache-hit are logged
//  at the call sites; co-residence and inter-use idle gaps are logged here).
//  The fuller policy — a slot invariant, an idle-unload timer, and pre-warm —
//  is Stage 2 (see the model-memory-management follow-up issue). The free
//  parameters of that policy (e.g. the idle timeout) should be set from the
//  `model.use … idle_gap_s` distribution this registry emits.
//

import Foundation
import os

actor ModelResidencyRegistry {

    static let shared = ModelResidencyRegistry()

    /// Eviction tier — how aggressively a resident model is reclaimed.
    enum Tier: String {
        case stt   // reclaimed when idle under .warning OR .critical pressure
        case llm   // reclaimed only under .critical pressure (bigger, costlier to reload)
    }

    /// The outcome of `markBusy(id:)` — and the caller's contract, which is not
    /// advisory.
    ///
    /// - `.claimed` — honored. The caller holds exactly ONE outstanding claim,
    ///   named by the `ClaimToken` that came back with this verdict, and MUST
    ///   balance it with a later `markIdle(_:)` passing THAT token. An unmatched
    ///   claim pins the model resident for the rest of the session: no other
    ///   release can repay it.
    /// - `.evicting` / `.notResident` — NO claim was taken, and there is no
    ///   token. The caller must NOT call `markIdle(_:)` and must NOT treat the
    ///   runtime as usable.
    ///
    /// The two refusals are separate cases because they describe different
    /// worlds, and a bare `false` conflated them. `.evicting` says a specific
    /// runtime is being torn down RIGHT NOW, so whatever the caller can still
    /// see of it is doomed. `.notResident` says only that no entry exists — the
    /// model was never loaded, or was freed, or its owner has built it but not
    /// yet registered it — so what the caller can see may be perfectly live and
    /// the recovery is to let the owner finish, not to assume a teardown.
    enum ClaimResult: Sendable, Equatable {
        case claimed
        case evicting
        case notResident

        /// True only for `.claimed`. This is the question "do I owe a
        /// `markIdle`?", and it is the only correct basis for balancing one.
        var isHonored: Bool { self == .claimed }
    }

    /// The identity of ONE honored claim: which slot it was taken on, which
    /// registration of that slot it was taken against, and — the part that makes
    /// this an identity rather than a description — which individual claim it is.
    ///
    /// Claims used to be anonymous — a `+1` on a per-entry refcount — and that
    /// is the second arm of HYPERWHISPER-SQ. Three things follow from anonymity,
    /// and every one of them frees a runtime out from under a live
    /// transcription:
    ///
    /// - A `register` on a slot that already has claims built a fresh entry with
    ///   `useCount: 0`, so every outstanding claim on that slot silently ceased
    ///   to exist and the next pressure sweep saw an idle model.
    /// - A `markIdle(id:)` keyed only on the id, so pass A finishing late repaid
    ///   itself out of pass B's claim.
    /// - A release that ran TWICE — a duplicated cleanup path, a `defer` that
    ///   also fired on an early return — repaid a claim that was already repaid,
    ///   and the one it actually consumed was somebody else's.
    ///
    /// The third is why `serial` exists, and why the ledger holds a set of
    /// serials rather than a count. `id` + `generation` names a GROUP of claims,
    /// not one: two concurrent passes against a single registration would get
    /// byte-identical tokens, and a doubled release of one of them would consume
    /// the other's protection. A serial is minted per `markBusy`, so no two
    /// honored claims ever share a token and a release either finds its own
    /// serial live or finds nothing and is ignored.
    ///
    /// A token makes the two ends of a claim recognise each other, the same way
    /// `LibWhisperProvider.tearDownContext(registeredGeneration:)` makes a
    /// teardown recognise the context it was actually sent for. This is the
    /// established shape in this codebase, not a new one.
    ///
    /// `Sendable` is load-bearing: a release routinely crosses a `Task {}` hop
    /// away from the code that took the claim (see `AIPostProcessor`'s `defer`,
    /// where `markIdle` is actor-isolated and `defer` is not `async`).
    struct ClaimToken: Sendable, Equatable, Hashable {
        /// The registry slot — the same `id` that was handed to `markBusy`.
        let id: String
        /// Which registration of that slot the claim was taken against. No
        /// longer part of the ledger key — `serial` is — but carried because it
        /// is what makes a release legible in the log, and what says whether a
        /// late release belongs to the runtime that is resident right now.
        let generation: UInt64
        /// THIS claim, and no other. Monotonic per registry and 1-based, so
        /// `serial: 0` is a token no `markBusy` can ever have issued — which is
        /// what lets a test fabricate a provably-never-live token without
        /// reaching into private state.
        let serial: UInt64
    }

    /// What `markBusy(id:)` hands back: the unchanged verdict, plus the token
    /// naming the claim when one was actually taken.
    ///
    /// A parallel struct rather than an associated value on `ClaimResult`, on
    /// purpose. `ClaimResult` is the vocabulary the reload path speaks in — it
    /// is what `ResidentRuntimeClaim.acquire` hands to `reload` so an owner can
    /// tell `.evicting` from `.notResident` — and it is interpolated verbatim
    /// into logs and test expectations. Hanging a payload off it would change
    /// every one of those strings for no gain: only ONE case ever carries a
    /// token, so the type would be lying about the other two anyway.
    struct ClaimReceipt: Sendable, Equatable {
        /// The verdict, unchanged. Switch on this for the three-way branch.
        let result: ClaimResult
        /// Non-nil exactly when `result` is `.claimed`. This is the claim the
        /// caller now owes a `markIdle(_:)` for.
        let token: ClaimToken?

        /// True iff a claim was taken, and therefore iff one is owed back.
        /// Reads the token rather than the verdict deliberately: the token is
        /// the thing that can actually repay the claim, so "is there a token"
        /// and "do I owe a release" are the same question by construction.
        var isHonored: Bool { token != nil }
    }

    /// Where an entry sits w.r.t. an in-progress pressure eviction.
    private enum EvictPhase {
        case none       // resident, not selected for eviction
        case selected   // chosen this round, free not yet started — a fresh claim still saves it
        case freeing    // its evict closure is awaiting; the runtime is going away, claims are rejected
    }

    private struct Entry {
        let id: String
        let tier: Tier
        /// Which registration of this slot this entry is. Stamped once at
        /// `register` and never mutated: it is the identity every claim taken
        /// against this entry is issued under.
        let generation: UInt64
        /// Eviction lifecycle. Set synchronously before/around the eviction
        /// await so `markBusy` can observe it, and used as an identity marker so
        /// a reload that `register`s a fresh entry mid-eviction is not clobbered.
        var phase: EvictPhase
        let loadedAt: Date
        var lastUsedAt: Date
        let evict: @Sendable () async -> Void
    }

    private var entries: [String: Entry] = [:]

    /// The claim ledger: slot id → the serials of every claim still outstanding
    /// on that slot, across every generation of it.
    ///
    /// A SET of individual claims rather than a count, because a count cannot be
    /// audited. Overlapping consumers of one shared runtime (e.g. concurrent
    /// Local API `/post-process` calls against the single local llama-server)
    /// are still why more than one claim can be outstanding at once — but under
    /// a count, the first finisher releasing twice is indistinguishable from
    /// both finishers releasing once, and the slot goes evictable while the
    /// second consumer is still using the runtime. Under a set, a release
    /// removes its OWN serial or nothing at all, which makes duplicate, stale
    /// and fabricated releases the same harmless no-op.
    ///
    /// It lives HERE and not on `Entry` because that is the whole fix. A model
    /// switch on both STT providers is `deregister` then `register`, so the
    /// entry is destroyed in between; a ledger carried on the entry cannot
    /// survive that no matter how carefully `register` copies it forward. This
    /// one is keyed on the id alone, so a claim outlives the entry it was taken
    /// against and keeps protecting the slot until it is repaid.
    private var liveClaims: [String: Set<UInt64>] = [:]

    /// The generation stamped on the next `register`. 1-based, and monotonic
    /// across the whole registry rather than per slot, so no two registrations
    /// ever share an identity and `generation: 0` stays permanently unissued.
    private var nextGeneration: UInt64 = 1

    /// The serial minted for the next honored claim. Same discipline as
    /// `nextGeneration` and for a stricter reason: this is the ledger key, so a
    /// value issued twice would let one claim's release repay another's — the
    /// defect a per-generation key still had. 1-based, which is what leaves
    /// `serial: 0` permanently unissued and therefore safe to fabricate.
    private var nextClaimSerial: UInt64 = 1

    private let log = Logger(subsystem: "com.hyperwhisper.app", category: "memory")

    // MARK: - Registration

    /// Record that a heavy model is now resident. Re-registering the same `id`
    /// overwrites the prior entry (e.g. a Parakeet V2→V3 switch reuses the slot)
    /// under a FRESH generation. `evict` MUST free the model and should capture
    /// its owner weakly.
    ///
    /// What the overwrite deliberately does NOT touch is `liveClaims`. It used
    /// to reset the slot's refcount to 0, which meant a model switch landing
    /// while a transcription was in flight erased that transcription's claim and
    /// handed the next pressure sweep an apparently-idle runtime to free —
    /// HYPERWHISPER-SQ. Claims taken before this call stay outstanding and keep
    /// the slot protected until their owners repay them; each one names itself
    /// by serial, so no release — early, late or duplicated — can be mistaken
    /// for another's.
    func register(id: String, tier: Tier, evict: @escaping @Sendable () async -> Void) {
        let now = Date()
        let generation = nextGeneration
        nextGeneration &+= 1
        entries[id] = Entry(id: id, tier: tier, generation: generation, phase: .none, loadedAt: now, lastUsedAt: now, evict: evict)
        let count = entries.count
        log.info("model.resident.register id=\(id, privacy: .public) tier=\(tier.rawValue, privacy: .public) generation=\(generation, privacy: .public) resident_count=\(count, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
        if count >= 2 {
            // Co-residence: the exact condition behind the 4.9 GB peak. Logged so
            // we can measure how often multiple heavy stacks are resident at once.
            let ids = entries.keys.sorted().joined(separator: ",")
            log.notice("model.coresidence count=\(count, privacy: .public) ids=\(ids, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
        }
    }

    /// Drop a model's registry entry when it is freed by its owner (version
    /// switch, delete, explicit cleanup). Safe to call for an unknown id.
    ///
    /// Outstanding claims on the slot survive this, deliberately, and that is
    /// what makes a claim survive a model switch. Both STT providers switch
    /// models as `deregister` immediately followed by `register`
    /// (`LibWhisperProvider.performLoad`, `ParakeetProvider` via
    /// `ParakeetRuntime.reset()`), so there is a window in which no entry exists
    /// at all. Dropping the ledger here would lose exactly the claims that
    /// window exists to endanger, and the claim's owner would then repay a claim
    /// the registry no longer believes in — see `markIdle(_:)`, which logs that
    /// as `model.use.release.unmatched` rather than corrupting a live count.
    func deregister(id: String) {
        guard entries.removeValue(forKey: id) != nil else { return }
        log.info("model.resident.deregister id=\(id, privacy: .public) resident_count=\(self.entries.count, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
    }

    // MARK: - Busy tracking (prevents mid-use eviction)

    /// Claim a model for an in-flight operation (a transcription or an LLM
    /// request). Every honored call takes ONE separately identified claim, so it
    /// is safe to nest and to call from overlapping concurrent uses of one
    /// shared runtime. Also emits the idle gap since its last use — the
    /// distribution that should set Stage 2's idle-unload timeout.
    ///
    /// See `ClaimResult` for the caller's obligations on each outcome. They are
    /// binding, not advisory: proceeding on a refusal is the HYPERWHISPER-SQ bug.
    /// The `ClaimReceipt`'s token is how the caller discharges the obligation —
    /// it names the one claim this call took, and nothing else can repay it.
    ///
    /// Deliberately NOT `@discardableResult`. Every call site consumes the
    /// result, and the compiler is what keeps the next one from quietly
    /// reverting to "claim and hope".
    func markBusy(id: String) -> ClaimReceipt {
        guard var e = entries[id] else { return ClaimReceipt(result: .notResident, token: nil) }
        // Once the evict closure has actually started (`.freeing`), the runtime
        // is committed to being torn down — refuse the claim rather than
        // advertise a model that is going away. The caller's own load path will
        // re-register a fresh entry if it still needs the model. A claim that
        // arrives while merely `.selected` IS honored: Phase 2 of evict() sees
        // the live claim and spares the model.
        //
        // This refusal stays AHEAD of the ledger write on purpose: a refused
        // claim must leave no trace, or it would pin a slot nobody ever repays.
        if e.phase == .freeing {
            log.notice("model.use.rejected id=\(id, privacy: .public) reason=freeing")
            return ClaimReceipt(result: .evicting, token: nil)
        }
        let gap = Date().timeIntervalSince(e.lastUsedAt)
        e.lastUsedAt = Date()
        entries[id] = e
        // Mint an identity for THIS claim and record it. What goes in the ledger
        // is the serial, not the generation: two passes against one registration
        // are two claims, and only a per-claim key lets one of them be released
        // without disturbing the other. The generation rides along on the token
        // for the log and for the reader, and a re-registration of this slot can
        // erase neither.
        let serial = nextClaimSerial
        nextClaimSerial &+= 1
        liveClaims[id, default: []].insert(serial)
        // `uses=` is now the whole slot's outstanding total across every
        // generation, which is the number that decides evictability. During a
        // model switch it can briefly count claims on two generations at once —
        // that is the ledger working, not a leak.
        let outstanding = outstandingClaims(id: id)
        log.info("model.use id=\(id, privacy: .public) idle_gap_s=\(String(format: "%.1f", gap), privacy: .public) generation=\(e.generation, privacy: .public) serial=\(serial, privacy: .public) uses=\(outstanding, privacy: .public)")
        return ClaimReceipt(result: .claimed, token: ClaimToken(id: id, generation: e.generation, serial: serial))
    }

    /// Release one claim, naming it by the token `markBusy` issued for it. The
    /// slot becomes evictable only when the LAST outstanding claim on it — any
    /// generation — is repaid.
    ///
    /// A token whose serial names no live claim is IGNORED and logged, never
    /// applied to whatever claim happens to be there. That single rule covers
    /// all three ways the old anonymous release went wrong: a stale one from
    /// before a re-register removes only its own serial and leaves a live claim
    /// on the new runtime alone; a duplicated one finds its serial already gone;
    /// a fabricated one was never issued. `markIdle(id:)` could tell none of
    /// them from a real release and just decremented, so a pass finishing late
    /// repaid itself out of the CURRENT pass's claim and exposed a runtime that
    /// was actively decoding. `model.use.release.unmatched` is how a lost
    /// release becomes visible instead of becoming somebody else's crash.
    func markIdle(_ token: ClaimToken) {
        guard var live = liveClaims[token.id], live.contains(token.serial) else {
            log.notice("model.use.release.unmatched id=\(token.id, privacy: .public) generation=\(token.generation, privacy: .public) serial=\(token.serial, privacy: .public)")
            return
        }
        live.remove(token.serial)
        if live.isEmpty {
            // Prune the id once its last claim is repaid, so a fully repaid slot
            // leaves the ledger empty rather than an id mapped to an empty set —
            // `hasLiveClaims` and `outstandingClaims` stay honest, and the
            // ledger cannot grow one dead entry per slot for the life of the
            // process.
            liveClaims.removeValue(forKey: token.id)
        } else {
            liveClaims[token.id] = live
        }
        // Only a MATCHED release refreshes the idle clock, and only on whatever
        // entry currently holds the slot. An unmatched one must not push out the
        // eviction of a model it has no live interest in.
        if var e = entries[token.id] {
            e.lastUsedAt = Date()
            entries[token.id] = e
        }
    }

    /// Outstanding claims on a slot, across every generation of it.
    ///
    /// Exposed because the regression risk this design accepts runs the other
    /// way from the old one: a release that never arrives no longer gets healed
    /// by the next `register`, it pins the slot for the session. This is the
    /// accessor that makes such a leak assertable in a test and inspectable in a
    /// diagnostic, next to `model.use.release.unmatched` in the log.
    func outstandingClaims(id: String) -> Int {
        liveClaims[id]?.count ?? 0
    }

    /// Whether the slot still owes ANY claim, taken against any generation of it.
    ///
    /// The idle test, and deliberately the coarser of the two available ones: it
    /// asks about the slot, not about the resident entry's generation. During a
    /// model switch a claim on the outgoing generation therefore protects the
    /// incoming runtime too, for as long as it takes its owner to finish and
    /// release. That over-protects for a few seconds; the alternative reading
    /// under-protects, and under-protecting is the bug.
    private func hasLiveClaims(_ id: String) -> Bool {
        !(liveClaims[id]?.isEmpty ?? true)
    }

    // MARK: - Eviction

    /// Reclaim idle models. Never evicts a busy (in-flight) model. `aggressive`
    /// (critical pressure) also evicts the `.llm` tier; otherwise only idle
    /// `.stt` models that have been idle at least `minIdle` seconds.
    func evict(aggressive: Bool, reason: String, minIdle: TimeInterval) async {
        let before = MemoryFootprint.currentMB()
        let now = Date()

        // Phase 1 — selection (fully synchronous, so no markBusy/register can
        // interleave): pick idle victims and move them to `.selected` IN PLACE.
        // Crucially the entries STAY in the map, so a concurrent markBusy still
        // finds them and records a claim — which Phase 2 honors before freeing.
        let victimIds = entries.values.filter { e in
            if hasLiveClaims(e.id) || e.phase != .none { return false }   // skip active or already-selected models
            if now.timeIntervalSince(e.lastUsedAt) < minIdle { return false }
            switch e.tier {
            case .stt: return true
            case .llm: return aggressive
            }
        }.map { $0.id }
        guard !victimIds.isEmpty else {
            log.notice("model.evict.noop reason=\(reason, privacy: .public) resident=\(self.entries.count, privacy: .public) footprintMB=\(before, privacy: .public)")
            return
        }
        for id in victimIds { entries[id]?.phase = .selected }

        log.notice("model.evict.begin reason=\(reason, privacy: .public) victims=\(victimIds.count, privacy: .public) footprintMB=\(before, privacy: .public)")
        var freed = 0
        // Phase 2 — free each victim, awaiting the (possibly slow) closure.
        for id in victimIds {
            // Re-read under the synchronous section: a markBusy that raced in
            // during a PREVIOUS victim's eviction await recorded a claim on this
            // still-`.selected` entry. Honor that fresh claim and abandon this
            // eviction — never free a model a new request just started using.
            // This re-check MUST use the same ledger test as the Phase 1 filter
            // above; a version of this fix that changed only one of the two
            // would leave the whole bug reachable through the other.
            guard var e = entries[id] else { continue }                  // deregistered meanwhile
            // A reload during a PRIOR victim's await may have replaced this
            // entry with a fresh one (register resets phase to `.none`). Only
            // proceed if it is STILL the entry we selected this round — never
            // evict a just-loaded runtime out from under the request that
            // loaded it (the stale `victimIds` id would otherwise hit it).
            guard e.phase == .selected else {
                log.notice("model.evict.skip id=\(id, privacy: .public) reason=re-registered")
                continue
            }
            if hasLiveClaims(id) {
                e.phase = .none
                entries[id] = e
                log.notice("model.evict.skip id=\(id, privacy: .public) reason=claimed-during-eviction")
                continue
            }
            // Commit to freeing: move to `.freeing` so any claim arriving during
            // the (slow) await is rejected by markBusy — the runtime is going
            // away now and cannot be handed to a new request.
            e.phase = .freeing
            entries[id] = e
            await e.evict()
            // Drop the entry only if it is STILL the one we froze. A reload that
            // `register`ed a fresh entry mid-eviction resets phase to `.none`,
            // and we must not clobber the newly-resident model.
            if entries[id]?.phase == .freeing {
                entries.removeValue(forKey: id)
            }
            freed += 1
            log.notice("model.evict.done id=\(id, privacy: .public) reason=\(reason, privacy: .public)")
        }
        log.notice("model.evict.end reason=\(reason, privacy: .public) freed=\(freed, privacy: .public) footprintMB_before=\(before, privacy: .public) footprintMB_after=\(MemoryFootprint.currentMB(), privacy: .public)")
    }

    /// Current residency snapshot (for diagnostics / future idle-timer ticks).
    func snapshot() -> (count: Int, ids: [String]) {
        (entries.count, entries.keys.sorted())
    }
}
