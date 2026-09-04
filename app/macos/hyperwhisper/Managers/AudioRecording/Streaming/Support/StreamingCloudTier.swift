//
//  StreamingCloudTier.swift
//  hyperwhisper
//
//  HYPERWHISPER CLOUD LIVE TIER — the id, the route it derives, and whether it
//  needs a language before it honours vocabulary.
//
//  These four helpers used to be statics on `HyperWhisperCloudStrategy`. That
//  class is gone (issue #326): every live provider now speaks through
//  `hw_net::live` behind `RustLiveStreamingStrategy`, and the wire-side tier
//  resolution moved with it into `hw_net::live::hw_cloud::stt_provider_for_tier`.
//
//  What did NOT move is the SETTINGS side. The picker has to clamp a stored id
//  before it binds one, and the vocabulary warning has to answer with no socket
//  and no credential — neither is a wire question, and neither has anywhere
//  else to live now that the strategy does not exist. So they live here, in
//  Support, next to `StreamingProviderErrorPolicy`, which is the same shape of
//  thing: a policy the UI and the client both read.
//
//  BOTH SIDES READ THE SAME CATALOG. macOS reads
//  `shared-app-classification/cloud-stt-catalog.json` through `CloudSTTCatalog`;
//  the core `include_str!`s that same file. The resolution rules were verified
//  identical against `hw_cloud.rs:64-79` — trim, drop empty, match an eligible
//  entry `caseInsensitive`, otherwise `deepgramNova3`, then read the entry's
//  `sttProvider` and fall back to `deepgram` — so a tier id resolves to one
//  route no matter which side asks.
//

import Foundation

// MARK: - HyperWhisper Cloud Live Tier

/// The HyperWhisper Cloud live tier, as the SETTINGS layer needs it.
///
/// A tier is a path selector inside the one cloud provider, deliberately not
/// its own `StreamingTranscriptionProvider` case: the credit and entitlement
/// gate in `RecordingTranscriptionFlow+Streaming` keys off
/// `provider == "hyperwhisperCloud"` and must keep matching.
enum StreamingCloudTier {

    /// The tier whose derived route (`/ws/streaming-deepgram`) is byte-identical
    /// to the endpoint every already-installed client used before the live tier
    /// picker existed. Anything unrecognised lands back here, on both sides of
    /// the FFI (`hw_cloud.rs DEFAULT_CLOUD_TIER`).
    static let defaultCloudTier = "deepgramNova3"

    /// The stored tier id clamped to the live-eligible set, in the catalog's own
    /// casing.
    ///
    /// The settings `Picker` binds through this: it renders BLANK when the
    /// selection matches no tag, and a stale or imported value — the Local API
    /// and a backup restore both write `streamingCloudTier`, and neither is the
    /// picker — would otherwise show an empty row while the session quietly ran
    /// on Deepgram.
    ///
    /// The core clamps the same way before it derives a route, so this is a
    /// display concern only: it can never disagree with what the socket does.
    static func normalizedCloudTier(_ cloudTier: String?) -> String {
        let trimmed = cloudTier?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let eligible = CloudSTTCatalog.shared.streamingCloudTierEntries.map(\.id)
        return eligible.first { $0.caseInsensitiveCompare(trimmed) == .orderedSame }
            ?? Self.defaultCloudTier
    }

    /// The upstream vendor id a tier routes to — `deepgramNova3` → `deepgram`,
    /// `geminiTranscribe` → `gemini-transcribe` — from which the core derives
    /// `/ws/streaming-{sttProvider}`.
    ///
    /// NOT A PRODUCTION CALL ANY MORE, and deliberately kept. The route is built
    /// in `hw_net::live::hw_cloud`, whose `stt_provider_for_tier` is
    /// `pub(super)` and therefore not exported over the FFI — so this is the
    /// only expression macOS has of "which route does this tier open", and
    /// `CloudSttTierParityTests` is its consumer. That test is the guard on a
    /// catalog edit adding a live-eligible tier with no `sttProvider`, or
    /// renaming one out from under the backend's route table. Deleting this
    /// deletes that gate; it does not delete a duplicate implementation, because
    /// the implementation it duplicates is not reachable from Swift.
    static func resolveSttProvider(_ cloudTier: String?) -> String {
        CloudSTTCatalog.shared.sttProvider(forEntryId: normalizedCloudTier(cloudTier)) ?? "deepgram"
    }

    /// Whether the tier's live vendor needs an explicit language before it
    /// honours vocabulary terms.
    ///
    /// True for Deepgram Nova-3, which silently ignores `keyterm` in
    /// multilingual mode. False for Gemini, which accepts `custom_vocabulary` in
    /// auto-detect (verified live) — and vocabulary is the whole reason to pick
    /// that tier, so applying Deepgram's rule there would silently delete the
    /// headline feature for every auto-detect user.
    ///
    /// ASKS THE CORE, not the catalog, so the settings warning reads the same
    /// rule the wire does. `supports_vocabulary_without_language`
    /// (`capabilities.rs:67`) and `hw_cloud::connect`'s own `vocabulary=` gate
    /// both resolve the tier through `stt_provider_for_tier`, and
    /// `tests.rs:2167 the_vocabulary_without_language_capability_agrees_with_the_built_url`
    /// asserts the capability answers exactly what the built URL does. A UI that
    /// warned from a second, Swift-local rule could warn about a restriction the
    /// socket no longer applies.
    static func tierRequiresLanguageForVocabulary(_ cloudTier: String?) -> Bool {
        !liveSupportsVocabularyWithoutLanguage(provider: .hyperWhisperCloud, cloudTier: cloudTier)
    }
}
