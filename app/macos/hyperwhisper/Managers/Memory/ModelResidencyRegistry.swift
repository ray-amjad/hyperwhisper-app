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

    /// Where an entry sits w.r.t. an in-progress pressure eviction.
    private enum EvictPhase {
        case none       // resident, not selected for eviction
        case selected   // chosen this round, free not yet started — a fresh claim still saves it
        case freeing    // its evict closure is awaiting; the runtime is going away, claims are rejected
    }

    private struct Entry {
        let id: String
        let tier: Tier
        /// Number of in-flight claims. 0 == idle. A refcount (not a Bool) so
        /// overlapping consumers of one shared runtime — e.g. concurrent Local
        /// API `/post-process` calls against the single local llama-server —
        /// don't let the first finisher mark the model idle while another is
        /// still using it.
        var useCount: Int
        /// Eviction lifecycle. Set synchronously before/around the eviction
        /// await so `markBusy` can observe it, and used as an identity marker so
        /// a reload that `register`s a fresh entry mid-eviction is not clobbered.
        var phase: EvictPhase
        let loadedAt: Date
        var lastUsedAt: Date
        let evict: @Sendable () async -> Void
    }

    private var entries: [String: Entry] = [:]
    private let log = Logger(subsystem: "com.hyperwhisper.app", category: "memory")

    // MARK: - Registration

    /// Record that a heavy model is now resident. Re-registering the same `id`
    /// overwrites the prior entry (e.g. a Parakeet V2→V3 switch reuses the slot).
    /// `evict` MUST free the model and should capture its owner weakly.
    func register(id: String, tier: Tier, evict: @escaping @Sendable () async -> Void) {
        let now = Date()
        entries[id] = Entry(id: id, tier: tier, useCount: 0, phase: .none, loadedAt: now, lastUsedAt: now, evict: evict)
        let count = entries.count
        log.info("model.resident.register id=\(id, privacy: .public) tier=\(tier.rawValue, privacy: .public) resident_count=\(count, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
        if count >= 2 {
            // Co-residence: the exact condition behind the 4.9 GB peak. Logged so
            // we can measure how often multiple heavy stacks are resident at once.
            let ids = entries.keys.sorted().joined(separator: ",")
            log.notice("model.coresidence count=\(count, privacy: .public) ids=\(ids, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
        }
    }

    /// Drop a model's registry entry when it is freed by its owner (version
    /// switch, delete, explicit cleanup). Safe to call for an unknown id.
    func deregister(id: String) {
        guard entries.removeValue(forKey: id) != nil else { return }
        log.info("model.resident.deregister id=\(id, privacy: .public) resident_count=\(self.entries.count, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
    }

    // MARK: - Busy tracking (prevents mid-use eviction)

    /// Claim a model for an in-flight operation (a transcription or an LLM
    /// request). Refcounted, so it is safe to nest and to call from overlapping
    /// concurrent uses of one shared runtime. Also emits the idle gap since its
    /// last use — the distribution that should set Stage 2's idle-unload timeout.
    func markBusy(id: String) {
        guard var e = entries[id] else { return }
        // Once the evict closure has actually started (`.freeing`), the runtime
        // is committed to being torn down — refuse the claim rather than
        // advertise a model that is going away. The caller's own load path will
        // re-register a fresh entry if it still needs the model. A claim that
        // arrives while merely `.selected` IS honored: Phase 2 of evict() sees
        // useCount>0 and spares the model.
        if e.phase == .freeing {
            log.notice("model.use.rejected id=\(id, privacy: .public) reason=freeing")
            return
        }
        let gap = Date().timeIntervalSince(e.lastUsedAt)
        e.useCount += 1
        e.lastUsedAt = Date()
        entries[id] = e
        log.info("model.use id=\(id, privacy: .public) idle_gap_s=\(String(format: "%.1f", gap), privacy: .public) uses=\(e.useCount, privacy: .public)")
    }

    /// Release one claim. The model becomes evictable only when the LAST
    /// overlapping claim finishes (useCount returns to 0).
    func markIdle(id: String) {
        guard var e = entries[id] else { return }
        if e.useCount > 0 { e.useCount -= 1 }
        e.lastUsedAt = Date()
        entries[id] = e
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
        // finds them and bumps useCount — which Phase 2 honors before freeing.
        let victimIds = entries.values.filter { e in
            if e.useCount > 0 || e.phase != .none { return false }        // skip active or already-selected models
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
            // during a PREVIOUS victim's eviction await bumped useCount on this
            // still-`.selected` entry. Honor that fresh claim and abandon this
            // eviction — never free a model a new request just started using.
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
            if e.useCount > 0 {
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
