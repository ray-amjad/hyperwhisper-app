import Foundation

/// macOS facade over the shared cloud-STT catalog
/// (`shared-app-classification/cloud-stt-catalog.json`) — the cross-platform
/// source of truth for cloud STT provider capabilities driving UI affordances
/// (custom-vocab field visibility, credits/min caption, cloud-tier-vs-BYOK list
/// filtering, supported-language hints).
///
/// This type used to decode the catalog itself: a `Decodable` tree with three
/// hand-written polymorphic decoders (`IntOrString`, `BoolOrString`,
/// `ArrayOrString`) and a bundle loader. All of that now lives once in
/// `shared-core-rs/crates/hw-catalog` (issue #280), which the Rust core
/// `include_str!`s at compile time — so there is no bundle lookup, no decode
/// failure, and no empty-catalog fallback state to reason about. The copies had
/// already drifted: `deepgramNova3` offered `gu`/`th`/`zh` on Windows and not
/// here. `shared-conformance/catalog-vectors.json` now pins the answers, and
/// `CatalogConformanceVectorTests` runs them through this binding.
///
/// The nested type names are kept as aliases for the shared-core records, so
/// call sites keep reading `CloudSTTCatalog.Model` / `.Entry` / `.VendorGroup`.
struct CloudSTTCatalog {
    /// One cloud STT provider row.
    typealias Entry = SttEntry
    /// A single selectable model within a provider — the Model dropdown
    /// (level 2) of the HyperWhisper Cloud picker and the `X-STT-Model` header.
    typealias Model = SttModel
    /// One row of the Provider dropdown: a company and every cloud-tier entry
    /// it owns.
    typealias VendorGroup = SttVendorGroup

    /// Stateless — every lookup goes straight to the shared core, which parses
    /// the embedded catalog once behind a `OnceLock`. Kept as a value so the
    /// ~30 existing `CloudSTTCatalog.shared.…` call sites read unchanged.
    static let shared = CloudSTTCatalog()

    /// All provider entries, in catalog order.
    var providers: [Entry] { cloudSttEntries() }

    /// Look up an entry by `id` (matches `CloudAccuracyTier.rawValue` for
    /// cloud-tier entries). Case-insensitive, as on every platform.
    func entry(byId id: String) -> Entry? {
        cloudSttEntry(id: id)
    }

    /// Look up an entry whose `migrateFrom` list contains the given alias
    /// (case-insensitive, trimmed). Drives legacy `cloudAccuracyTier`
    /// resolution in `CloudAccuracyTier.fromStorageValue` — NOT `cloudProvider`
    /// rewriting, which is `normalizeCloudProvider`.
    func entry(byMigrateFromAlias alias: String) -> Entry? {
        cloudSttEntryByMigrateFrom(alias: alias)
    }

    /// All entries surfaced under the HyperWhisper Cloud accuracy dropdown,
    /// in catalog order. These are the cloud providers the credit path routes to.
    var cloudTierEntries: [Entry] {
        providers.filter { $0.access?.cloudTierEligible == true }
    }

    /// The cloud-tier entries HyperWhisper Cloud can also serve LIVE, in catalog
    /// order — the eligible set for the streaming cloud-tier picker.
    ///
    /// Catalog-derived in Rust (`cloudTierEligible` AND some model with
    /// `streaming: true`), never a hand-kept list here. Deliberately NOT the
    /// entry-level `features.streaming` hint, which is true for six vendors we
    /// serve no WebSocket route for; offering one of those would ship a 404 at
    /// dictation time, and the STT catalog has no `enabled` gate to hide it.
    var streamingCloudTierEntries: [Entry] {
        cloudSttStreamingCloudTierEntries()
    }

    // MARK: - Vendor groups (catalog v7+)

    /// The Provider dropdown's rows: cloud-tier entries grouped by `vendor` and
    /// sorted by company name, so the list reads alphabetically and each company
    /// appears exactly once. Google owns two entries (Gemini 3.5 Transcribe + Gemini) and so
    /// contributes one row whose model list spans both.
    var cloudTierVendorGroups: [VendorGroup] {
        cloudSttCloudTierVendorGroups()
    }

    /// The vendor group a cloud-tier entry id belongs to, or nil for an unknown
    /// id or one that is not cloud-tier eligible.
    func vendorGroup(forEntryId id: String) -> VendorGroup? {
        cloudSttVendorGroup(id: id)
    }

    // MARK: - Provider → model helpers (catalog v6+)

    /// The `X-STT-Provider` header value for a cloud-tier entry id, sourced
    /// from the catalog `sttProvider` field so it can't drift from the backend.
    /// Returns nil when the entry or its `sttProvider` is missing.
    func sttProvider(forEntryId id: String) -> String? {
        cloudSttProvider(id: id)
    }

    /// The selectable models for a provider entry id (catalog order). Empty
    /// when the entry has no `models[]` (older catalog / unknown id).
    func models(forEntryId id: String) -> [Model] {
        cloudSttModels(id: id)
    }

    /// The default model for a provider entry id — the `isDefault: true` model,
    /// falling back to the first listed model, or nil when none exist.
    func defaultModel(forEntryId id: String) -> Model? {
        let models = models(forEntryId: id)
        return models.first(where: { $0.isDefault == true }) ?? models.first
    }

    /// The default model *id* string for a provider entry id, or "" when the
    /// provider has no models (single implicit model — let the backend default).
    /// Note that "" is also a legitimate catalogued id (Grok's single model).
    func defaultModelId(forEntryId id: String) -> String {
        cloudSttDefaultModelId(id: id) ?? ""
    }

    /// Look up a single model by (provider entry id, model id), case-insensitive
    /// on the model id for parity with the rest of the catalog lookups.
    func model(forEntryId entryId: String, modelId: String) -> Model? {
        models(forEntryId: entryId).first {
            $0.id.caseInsensitiveCompare(modelId) == .orderedSame
        }
    }

    /// The provider's supported languages folded to the two-letter picker code
    /// space (sorted, always including `"auto"`), or nil when the catalog leaves
    /// the set `"unverified"` so the caller keeps its full list.
    ///
    /// The macOS language picker (`STTCapabilities`) deliberately lists BCP-47
    /// region rows — `en-US`, `en-GB`, `pt-BR`, `es-419` — as separate entries,
    /// while this fold collapses to the primary subtag. Match a picker row by
    /// its primary subtag against this set, never by exact code, or every
    /// region row silently disappears.
    func pickerLanguageCodes(forEntryId id: String) -> [String]? {
        cloudSttPickerLanguageCodes(id: id)
    }

    /// Normalize a persisted `cloudProvider` storage value. If the value is a
    /// legacy standalone-provider alias for an entry that is now surfaced as a
    /// HyperWhisper Cloud accuracy tier (e.g. `microsoftazurespeech` →
    /// `azureMaiTranscribe`), returns `(provider: "hyperwhisper", accuracyTier:
    /// <new tier id>)`. Otherwise returns the input unchanged with
    /// `accuracyTier == nil` — critically, BYOK provider names like
    /// `"deepgram"` or `"groq"` pass through untouched even though they appear
    /// in `migrateFrom` for tier-alias resolution.
    func normalizeCloudProvider(_ value: String?) -> (provider: String?, accuracyTier: String?) {
        let normalized = cloudSttNormalizeCloudProvider(value: value)
        return (provider: normalized.provider, accuracyTier: normalized.accuracyTier)
    }

    // MARK: - Live-only models

    /// Cloud model ids HyperWhisper Cloud serves ONLY over the live WebSocket
    /// route. They must never be offered as — or accepted as — a mode's
    /// dictation model: `/transcribe` answers one with a 400, so every
    /// dictation in such a mode fails.
    ///
    /// NOT derivable from the per-model `streaming` flag, despite how that reads.
    /// `streaming: true` means "HyperWhisper Cloud routes this model live", and
    /// `deepgramNova3` carries it on BOTH `nova-3-general` and `nova-3-medical`
    /// — which are the DEFAULT pre-recorded models. Filtering the dictation
    /// picker on `streaming == true` would delete the default dictation model
    /// from it. The catalog has no "live-only" field to key off, so this list is
    /// the macOS mirror of the same fact the other heads state literally:
    /// `GEMINI_TRANSCRIBE_LIVE_MODEL` in
    /// `hyperwhisper-cloud/src/providers/gemini-transcribe.ts` (which raises the
    /// 400), `LIVE_MODEL` in `hw-net`'s `gemini_transcribe.rs`, and the
    /// deliberate omission from `CloudTranscriptionModel.GeminiTranscribe` on
    /// Windows. Adding a catalog field would let all four derive it — see the
    /// note in this PR's review.
    static let liveOnlyModelIds: Set<String> = ["gemini-3.5-transcribe-live"]

    /// Whether `modelId` is one of `liveOnlyModelIds` (case-insensitive, trimmed,
    /// matching the rest of the catalog's model lookups).
    static func isLiveOnlyModel(_ modelId: String?) -> Bool {
        guard let trimmed = modelId?.trimmingCharacters(in: .whitespacesAndNewlines),
              !trimmed.isEmpty else {
            return false
        }
        return liveOnlyModelIds.contains(trimmed.lowercased())
    }
}

// MARK: - SwiftUI conformances on the shared-core records
//
// The generated records carry the data but not the protocol conformances the
// pickers need. `ForEach` needs `Identifiable`; the vendor group's key is
// `vendorKey`, which is what the Provider dropdown tags its rows with.

extension SttEntry: Identifiable {}

extension SttModel: Identifiable {}

extension SttVendorGroup: Identifiable {
    /// The catalog `vendor` key — the dropdown's selection tag.
    public var id: String { vendorKey }

    /// The entry a fresh selection lands on — the first in catalog order, nil
    /// for the synthesized fallback row `CloudAccuracyTier.pickerVendorGroups`
    /// builds when the shared core has no cloud-tier entries.
    var defaultEntry: SttEntry? { entries.first }

    /// Every model in the group, each paired with the entry that owns it.
    /// Ordered by entry, then by the entry's own model order. The owning entry
    /// is what becomes the `X-STT-Provider` header, so a merged company row
    /// (Google) still routes each model correctly.
    var models: [(entry: SttEntry, model: SttModel)] {
        entries.flatMap { entry in entry.models.map { (entry, $0) } }
    }
}
