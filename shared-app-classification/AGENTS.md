# CLAUDE.md

Cross-platform classification catalogs — `app-type-catalog.json` (which apps count as email / IDE / browser etc. for app-aware behavior), `cloud-stt-catalog.json` (cloud STT provider capabilities) and `cloud-pp-catalog.json` (cloud post-processing / LLM engines), all driving macOS + Windows UI.

## Catalog files

- `app-type-catalog.json` — maps macOS bundle IDs, Windows process names, and browser hosts to coarse app types
- `cloud-stt-catalog.json` — per-provider STT capability matrix: access mode (HW Cloud tier vs BYOK), credits/min, custom-vocabulary support, supported languages, accepted formats, feature flags
- `cloud-pp-catalog.json` — per-engine HyperWhisper Cloud **post-processing** (LLM) matrix: `X-LLM-Provider` / `X-LLM-Model` header values, per-model token prices, recommended/default flags, accuracy/speed gauges, and an `enabled` rollout gate. Loaded by `CloudPPCatalog.swift` (macOS) and `CloudPpCatalog.cs` (Windows) to drive the credit-billed post-processing Engine + Model picker.

<important if="you are adding or modifying any entry in cloud-pp-catalog.json">

Both clients load this file as the source of truth for the HyperWhisper Cloud post-processing Engine + Model picker. Wrong data ships a UI bug — and wrong prices mis-bill credits.

- **Pricing-sync invariant (critical):** `pricePerMInput` / `pricePerMOutput` are USD per 1M tokens and are DISPLAY/estimate only. The ACTUAL billing constants live in `hyperwhisper-cloud/src/lib/cost-calculator.ts`. The two MUST match. If you change a price here, change it in `cost-calculator.ts` in the same release (and vice-versa); they are independent copies, so they silently drift if you touch only one. Re-confirm prices against the provider's pricing page before merging — they feed billing.
- **`enabled` rollout gate:** an engine or model with `enabled: false` is hidden by both clients. Exposing a new engine BEFORE the backend deploys it would make `X-LLM-Provider: <new>` silently fall back to Cerebras (wrong model + wrong billing), so new engines ship `enabled: false` and are flipped to `true` only once `hyperwhisper-cloud` is live with that provider's API key (synced via Infisical). A missing `enabled` is treated as enabled (older catalogs).
- **`id` is the storage prefix:** each provider `id` is persisted as the prefix of `Mode.cloudPostProcessingModel` (`<id>:<modelId>`). Renaming an `id` breaks persisted user modes — keep the old `id` or add migration mapping in `CloudPostProcessingModel.fromStorageValue` (Swift) and the Windows equivalent in the same release. The `id` prefix exists so Groq vs Cerebras don't collide on the shared `gpt-oss-120b` model id.
- `llmProvider` is the `X-LLM-Provider` header value; `llmModelHeader` (falling back to model `id`) is the `X-LLM-Model` value. The backend `extractLLMProvider` / `extractLLMModel` allowlist must accept exactly these values.
- `accuracy` / `speed` (1–5) are gauge hints; the Model Library's authoritative ratings live in `ModelLibraryManager.swift` / `ModelLibraryManager.cs` `postProcessingRatings` and must match the new model ids there.
- Bump `version` when the schema shape changes; bump `updated` on any data change.
- After editing, run the macOS Debug build AND the Windows build — both have loaders that fall back to an empty catalog (logged) if a required field is missing.
</important>

<important if="you are adding or modifying any entry in cloud-stt-catalog.json">

Both clients load this file as the single source of truth — wrong data here ships a UI bug to every user on next release.

- Every factual field MUST be verifiable from upstream docs OR our own backend integration. Cite the source in `caveats` when a claim isn't obvious.
- If a fact is uncertain, set the field to the string `"unverified"` rather than guessing. The UI treats `"unverified"` as the conservative default (vocabulary field hidden, auto-detect off).
- **Custom vocabulary**: what counts as "supported" is what OUR BACKEND forwards, not what the upstream API documents. Example: xAI Grok STT documents `keyterm`, but `hyperwhisper-cloud/src/providers/xai-stt.ts` does not pass `initial_prompt` through — so `grokStt.customVocabulary.supported = false` in the catalog. Flip when wired.
- **Cloud tier vs BYOK**: `access.cloudTierEligible: true` means the provider appears under the HyperWhisper Cloud accuracy dropdown. `access.byokEligible: true` means it appears in the BYOK provider list. Both can be true. `azureMaiTranscribe` and `googleChirp3` are cloud-only (no BYOK in v1).
- `cloudTier.creditsPerMinute` is display-only — actual billing comes from `hyperwhisper-cloud/src/lib/cost-calculator.ts` and depends on per-provider minimum-billable-duration rules. Keep the two roughly aligned; if they drift > 10%, update the catalog.
- Bump `version` (integer) when you change the schema shape. Bump `updated` (ISO date) on any data change.
- After editing, run the macOS Debug build AND the Windows build — both have schema validators that fail at startup if a required field is missing.
- Every entry should carry a `sources: [<url>, ...]` array so future edits can re-check the doc page that backed the original claim.
- `languages.count` should equal `languages.codes.length` for verified providers. The intentional exception is Deepgram Nova-3 — `count` reports the number of unique BASE codes while `codes` includes regional variants (e.g. `ar-AE`, `en-US`); the `notes` field documents the gap. UI code should treat `codes.length` as the operational truth.
- Vendor-claimed counts can lie. Azure docs say "43 languages" but list 42; xAI docs say "24" but list 25; Whisper docs say "99" but the tokenizer has 100. The catalog reports the array length, not the marketing number.
</important>

<important if="you are adding or modifying any entry in app-type-catalog.json">

- `macBundleIds` are the macOS app bundle identifiers (`com.apple.mail`, etc.)
- `windowsProcesses` are Windows process names without `.exe`
- `hosts` are browser hostnames for web apps that should be classified under the same type
- Keep entries lowercase except where the platform requires otherwise (Windows process names sometimes use mixed case — match what the OS reports)
</important>

<important if="you are renaming a provider id or changing access.cloudTierEligible on cloud-stt-catalog.json">

This is a breaking change for persisted user modes — Swift's `CloudAccuracyTier` and the Windows equivalent serialize the `id` string into user data. Either:
1. Keep the old id and add a new one (preferred), or
2. Add a `migrateFrom: ["oldId1", "oldId2"]` array on the new entry — both clients must implement the migration in the same release.
</important>

<important if="you are adding aliases that should fold a persisted value onto a HW Cloud tier">

The catalog has TWO intentionally separate alias namespaces. Picking the wrong one silently rewrites unrelated user data — pick deliberately.

- `migrateFrom` — legacy `cloudAccuracyTier` values that should resolve to this entry's id. Read by `CloudAccuracyTier.fromStorageValue` / `CloudAccuracyTierExtensions.FromString`. Safe to include BYOK provider raw values here (`"deepgram"`, `"groq"`, `"grok"`, `"elevenlabs"`) so legacy tier-bucket strings like `"medium"` or a stray `"deepgram"` in the tier slot resolve to the right tier id.
- `legacyCloudProviderAliases` — legacy standalone `cloudProvider` storage values that should fold onto `cloudProvider="hyperwhisper"` + this entry's id as the tier. Read by `normalizeCloudProvider` only. Include ONLY values that were once their own standalone-provider rows and no longer exist as standalone providers (`"microsoftazurespeech"`, `"googlespeech"`). NEVER put a BYOK provider name here — doing so silently disables every user's BYOK setup for that provider on next launch / backup restore / Local API write.
</important>
