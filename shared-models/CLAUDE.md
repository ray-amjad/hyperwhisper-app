# Shared Models Catalog

Cross-platform source of truth for per-model metadata that doesn't belong inside any single platform's local registry. Both macOS and Windows bundle and read `models-catalog.json` so these fields are defined exactly once.

## What's in scope

Three fields per `(provider, id)`:

| Field | Meaning |
|---|---|
| `displayName` | The model's user-visible name, and the ONLY place it is written. See "Naming" below. |
| `supportsCustomVocabulary` | Model accepts user-supplied keyword / keyterm / `initial_prompt` boosts at request time. Cross-checked against the actual transcription request site, not vendor marketing. |
| `availableViaHyperWhisperCloud` | Model is reachable through the credit-based HyperWhisper Cloud routing service (Fly backend at `hyperwhisper-cloud`). When `true`, users without their own API key for that provider can still use the model via cloud credits. |

## Naming

A model has exactly 1 name. It is written here, and every head reads it through
the shared core (`models_entry(provider, kind, id).displayName`).

Warning: never write a model name anywhere else. Not in
`CloudTranscriptionModels.swift`, not in `CloudTranscriptionModel.cs`, not in a
`.resx`, not in a `.strings`. A name is a proper noun, so it is never
translated. Before #837 those copies had drifted: `Whisper Large V3` on Windows
against `Whisper Large v3` on macOS, `Async v5` in the catalog against
`STT Async v5` on both heads, and `(Preview)` glued onto 4 names beside the
`previewStatus` field that already said so.

The style rule, which `hw-catalog::style_violation` enforces on every catalog
name:

- The vendor's own name for the model — `Nova 3 General`, `Voxtral Mini`.
- No company prefix. The Provider row above already says the company.
- No status suffix. `previewStatus` is a field and the UI draws the badge.
- No bracketed domain — `Universal-2 Medical`, not `Universal-2 (Medical)`.
- No version padding — `Grok Voice Transcribe 1`, not `Grok Voice Transcribe 1.0`.
- A version is a lower-case `v` — `Whisper Large v3`, `Scribe v2`.

A wildcard row (`id: "*"`) carries NO `displayName`. It stands for a whole
family, and each head's on-device registry names those models.

Two catalogs name models, because they cover different sets:

| File | Covers |
|---|---|
| `shared-models/models-catalog.json` | every model a head can list, cloud and BYOK |
| `shared-app-classification/cloud-stt-catalog.json` | every model offered through HyperWhisper Cloud, including `gemini-3.5-transcribe-live`, which is WebSocket-only and is deliberately NOT a Model Library row |

Where both hold a row the 2 names must be identical.
`hw-catalog::names::tests::the_two_catalogs_agree_on_every_shared_model_name`
fails the build when they are not.

Plus a `platforms` array so a model can sit in the catalog before any given app ships it — each app filters out entries whose platform isn't in the list.

What is **not** in the catalog: pricing, descriptions, endpoint URLs, popular/curated flags, speed/accuracy benchmark scores. Those stay in the per-platform registries (`CloudTranscriptionModels.swift`, `CloudTranscriptionModel.cs`, `PostProcessingModels.swift`, `LanguageModelInfo.cs`) because they're large, change frequently for marketing reasons, or are computed from benchmarks.

## Files

- `models-catalog.json` — the catalog
- `schema.json` — JSON Schema for the catalog
- `CLAUDE.md` — this file

## Keying

Entries are keyed by `(provider, id)`, **not** by `id` alone. The same id string can appear under multiple providers:

- `gpt-oss-120b` under `cerebras` and `openai/gpt-oss-120b` under `groq` are distinct deployments.
- `default` is used as a sentinel id by some Windows registries.

Wildcard entries use `id: "*"` and apply to every model from that provider. Lookup precedence: exact `(provider, id)` → `(provider, "*")` → defaults `(false, false)`.

<important if="you are editing the catalog loaders or wiring catalog flags into macOS/Windows app code">

## Platform consumers

| Platform | Bundling | Loader |
|---|---|---|
| macOS | Xcode folder reference at `../../shared-models` (added to `Resources` build phase) | `app/macos/hyperwhisper/Utilities/SharedModelsCatalog.swift` reads via `Bundle.main.url(forResource:withExtension:subdirectory:)` |
| Windows | `<EmbeddedResource Include="..\..\..\shared-models\models-catalog.json" ... />` in `HyperWhisper.csproj` | `app/windows/HyperWhisper/Services/SharedModelsCatalog.cs` reads via `Assembly.GetManifestResourceStream` |

Each loader exposes:

```
supportsCustomVocabulary(provider:String, modelId:String) -> Bool
availableViaHyperWhisperCloud(provider:String, modelId:String) -> Bool
isExposedOnThisPlatform(provider:String, modelId:String) -> Bool
```

Row builders in `ModelLibraryManager` (both platforms) call these instead of hard-coding the flags.

</important>

<important if="you are adding/removing a model, or changing whether a model is cloud-routed or supports custom vocabulary">

## When to update this catalog

You MUST update `models-catalog.json` when:

- Adding a new cloud transcription or post-processing model to either platform — add an entry with the correct `provider`, `id`, `kind`, `platforms`, and the two booleans.
- The Fly backend (`hyperwhisper-cloud/src/routes/transcribe.ts` and `hyperwhisper-cloud/src/lib/llm-provider.ts`) starts or stops routing for a model — flip `availableViaHyperWhisperCloud` accordingly.
- A provider gains or loses custom-vocabulary support in a model upgrade (e.g., Scribe v1 → v2) — flip `supportsCustomVocabulary`.

You do **not** need to update this catalog for changes to pricing, descriptions, accuracy ratings, default-picker promotion (`isPopular`), or any field that's still platform-local.

</important>

<important if="you are flipping availableViaHyperWhisperCloud or supportsCustomVocabulary to true for a model">

## Verification

Before flipping `availableViaHyperWhisperCloud = true` for a model:

1. Confirm the Fly backend routes the upstream id. Transcription IDs are in `hyperwhisper-cloud/src/routes/transcribe.ts`; LLM IDs are in `hyperwhisper-cloud/src/lib/llm-provider.ts` (`LLM_PROVIDER_NAMES`).
2. Confirm the id string exactly matches what each platform's registry uses. Watch for prefix differences (`openai/gpt-oss-120b` vs `gpt-oss-120b`).

Before flipping `supportsCustomVocabulary = true` for a transcription model:

1. Open the provider's request site in `app/macos/hyperwhisper/Services/Cloud*Provider.swift` or `app/windows/HyperWhisper/Services/Cloud*TranscriptionService.cs` and confirm it actually sends one of: `vocabulary`, `keyterm`, `keywords`, `initial_prompt`, `contextualWords`.
2. Don't trust marketing pages — many providers advertise "custom vocabulary" via console-side config that the API doesn't expose.

</important>

<important if="you are adding or editing catalog entries">

## Platform-specific quirks captured here

- Windows registers `HyperWhisperCloud / "default"` as a literal transcription model row in `CloudTranscriptionModel.cs`. As of the catalog introduction that fake row is being removed; the catalog has no entry for it.
- macOS has a `hyperwhisper-cloud` post-processing model that Windows does not. The catalog marks it `platforms: ["macos"]`.
- The Grok cloud-transcription model had an empty-string id until 2026-09-19, because an xAI request named no model. Grok Voice Transcribe 2.0 gave `/v1/stt` a `model` parameter, so the catalog now spells the id out (`grok-voice-transcribe-2.0`) and every head resolves a stored blank to it.

</important>
