//
//  TranscriptionModelCatalog.swift
//  HyperWhisper
//
//  Single source of truth for turning a stored transcription model id (the raw
//  string persisted on `Mode.model`) into the names shown in the UI.
//
//  Background: mode cards and the create/edit model picker each carried their
//  own copy of an id → display-name switch, including a hardcoded "NVIDIA "
//  prefix for Nemotron. The copies had already drifted (different check order),
//  and every new local model family had to be added in both. This catalog owns
//  that resolution so every surface agrees.
//
//  The display *string* for each family still lives with that family
//  (`WhisperCppModel.displayName`, `ParakeetModel.displayName`, the various
//  `*.Constants` display names). The catalog only aggregates them and owns the
//  cross-family rules: brand prefixing and the Apple/Qwen/cloud special cases.
//

import Foundation

/// Resolved naming for one transcription model.
struct TranscriptionModelDescriptor {
    /// The raw id persisted on `Mode.model` (e.g. "base", "nemotron-asr-3.5-latin").
    let id: String

    /// Bare name as defined by the model's family, e.g. "Nemotron 3.5 (Latin)".
    /// Use this where a separate provider column is shown (the Model Library).
    let baseName: String

    /// Brand label for the family, e.g. "NVIDIA", "Whisper", "Apple Speech".
    /// `nil` when the family has no brand distinct from its name (unknown ids,
    /// the `"cloud"` sentinel). Values match `LibraryProviderKey.displayName`.
    let providerName: String?

    /// True for the `"cloud"` sentinel — provider/model are resolved elsewhere
    /// (see `CloudTranscriptionModels`), so callers should branch before this.
    let isCloud: Bool

    /// Single-line name for surfaces with no separate provider column
    /// (mode cards, the create/edit model picker). Only families whose
    /// `baseName` doesn't already carry the brand get it prepended — today that
    /// is Nemotron, matching the historical card/picker behaviour.
    let displayName: String
}

/// Resolves transcription model ids to display names. Construct it from the
/// live managers (cheap — it only holds two references) and call per id.
@MainActor
struct TranscriptionModelCatalog {
    let whisper: WhisperModelManager
    let parakeet: ParakeetModelManager

    /// Branded single-line name for `id`, as shown on mode cards and in the
    /// create/edit model picker. Falls back to the raw id when unknown.
    func displayName(for id: String) -> String {
        descriptor(for: id).displayName
    }

    /// Full naming for `id`. The id namespaces are disjoint, so lookup order
    /// doesn't affect correctness — the order below just reads cheapest-first.
    func descriptor(for id: String) -> TranscriptionModelDescriptor {
        if id.lowercased() == "cloud" {
            return TranscriptionModelDescriptor(
                id: id, baseName: id, providerName: nil, isCloud: true, displayName: id
            )
        }

        if id == "apple-speech-analyzer" {
            return Self.plain(id: id, baseName: "Apple Speech", providerName: "Apple Speech")
        }

        if id == Qwen3AsrModelManager.Constants.modelId {
            return Self.plain(
                id: id,
                baseName: Qwen3AsrModelManager.Constants.displayName,
                providerName: "Qwen3 ASR"
            )
        }

        if let model = whisper.availableModels.first(where: { $0.name == id }) {
            return Self.plain(id: id, baseName: model.displayName, providerName: "Whisper")
        }

        if let model = parakeet.availableModels.first(where: { $0.name == id }) {
            return Self.plain(id: id, baseName: model.displayName, providerName: "NVIDIA")
        }

        if id == NemotronModelManager.Constants.latinModelId {
            return Self.branded(
                id: id, baseName: NemotronModelManager.Constants.latinDisplayName, providerName: "NVIDIA"
            )
        }
        if id == NemotronModelManager.Constants.multilingualModelId {
            return Self.branded(
                id: id, baseName: NemotronModelManager.Constants.multilingualDisplayName, providerName: "NVIDIA"
            )
        }

        // Unknown id — surface it verbatim rather than inventing a name.
        return TranscriptionModelDescriptor(
            id: id, baseName: id, providerName: nil, isCloud: false, displayName: id
        )
    }

    /// Descriptor whose single-line name is just the base name (no brand prefix).
    private static func plain(id: String, baseName: String, providerName: String) -> TranscriptionModelDescriptor {
        TranscriptionModelDescriptor(
            id: id, baseName: baseName, providerName: providerName, isCloud: false, displayName: baseName
        )
    }

    /// Descriptor whose single-line name prepends the brand,
    /// e.g. "NVIDIA Nemotron 3.5 (Latin)".
    private static func branded(id: String, baseName: String, providerName: String) -> TranscriptionModelDescriptor {
        TranscriptionModelDescriptor(
            id: id, baseName: baseName, providerName: providerName, isCloud: false,
            displayName: "\(providerName) \(baseName)"
        )
    }
}
