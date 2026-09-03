//
//  SeededModeValues.swift
//  hyperwhisper
//
//  The ONE mode a brand-new install creates, in macOS' own column names.
//
//  What the mode is no longer lives here: it lives in the shared Rust core
//  (`shared-core-rs/crates/hw-catalog/src/mode_seed.rs`) and reaches this file
//  through the UniFFI export `modeSeedDefault(region:)`. macOS used to seed one
//  mode ("Default", `language "en"`, provider `"hyperwhisper"`, a hardcoded
//  `claudeHaiku`) while the two .NET heads seeded six; three implementations
//  gave three answers. This type is only the translation layer, and it is
//  deliberately dumb — every field is copied straight across, and the two
//  places where the shared vocabulary and macOS' schema disagree are called out
//  below.
//
//  `shared-conformance/mode-seed-vectors.json` pins the field set per region and
//  is run against THIS type by `ModeSeedConformanceVectorTests`, alongside the
//  Rust and .NET runners of the same file.
//

import CoreData
import Foundation

/// The exact set of `Mode` columns a fresh install writes, resolved from the
/// shared core for a host region.
///
/// It exists as a value type so the seed can be tested without a Core Data
/// stack, and so `Mode.applySeededValues(_:)` has one obvious list of what
/// "everything the seeder owns" means — see the hostile-defaults note there.
struct SeededModeValues: Equatable {

    /// Used only if the core ever hands back an id macOS cannot parse as a
    /// UUID. It is the same literal the core ships, so the arm is unreachable
    /// unless the core changes the id — which the conformance vectors would
    /// fail on first. Anchored by onboarding restore-point lookups
    /// (`LiveOnboardingSourceCommitter.defaultModeID`) and by both .NET heads.
    static let fallbackID = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!

    let id: UUID
    let name: String
    let preset: String
    let language: String

    /// The shared seed calls this `providerType`; macOS stores it in
    /// `mode.model`. There is NO `providerType` column on macOS, and `model` is
    /// what the cloud/local routing reads (`APIKeySettingsManager.swift`,
    /// `TranscribeEndpoint.swift`). The C# entity has both columns and they are
    /// not interchangeable — do not "fix" this mapping.
    let model: String

    /// The cloud *transcription* provider. Not `postProcessingProvider`.
    let cloudProvider: String
    let cloudAccuracyTier: String
    let cloudTranscriptionModel: String
    let postProcessingMode: Int16
    let postProcessingProvider: String

    /// `"<engineId>:<modelId>"`, stored VERBATIM. `ModeModels.swift`'s
    /// `CloudPostProcessingModel.fromStorageValue` falls back to **Grok** on a
    /// value it cannot split, so dropping the engine prefix would silently seed
    /// the wrong vendor.
    let cloudPostProcessingModel: String

    /// Never empty. `""` is a real `EnglishSpelling` token meaning "emit no
    /// spelling instruction", which is never right to seed.
    let englishSpelling: String

    let punctuation: Bool
    let capitalization: Bool
    let profanityFilter: Bool
    let customInstructions: String
    let isDefault: Bool
    let isSystemProvided: Bool
    let sortOrder: Int16

    init(seed: ModeSeed) {
        self.id = UUID(uuidString: seed.id) ?? Self.fallbackID
        self.name = seed.name
        self.preset = seed.preset
        self.language = seed.language
        self.model = seed.providerType
        self.cloudProvider = seed.cloudProvider
        self.cloudAccuracyTier = seed.cloudAccuracyTier
        self.cloudTranscriptionModel = seed.cloudTranscriptionModel
        // The core carries these as `Int32`; the Core Data attributes are
        // `Integer 16`. Clamping rather than trapping: an out-of-range value
        // would be a core bug, and crashing on the first-launch path is the
        // worst possible way to report one.
        self.postProcessingMode = Int16(clamping: seed.postProcessingMode)
        self.postProcessingProvider = seed.postProcessingProvider
        self.cloudPostProcessingModel = seed.cloudPostProcessingModel
        self.englishSpelling = seed.englishSpelling
        self.punctuation = seed.punctuation
        self.capitalization = seed.capitalization
        self.profanityFilter = seed.profanityFilter
        self.customInstructions = seed.customInstructions
        self.isDefault = seed.isDefault
        self.isSystemProvided = seed.isSystemProvided
        self.sortOrder = Int16(clamping: seed.sortOrder)
    }

    /// The seed for an explicit ISO 3166-1 alpha-2 region. `nil`, empty and
    /// unknown all resolve to American spelling inside the core — Rust owns the
    /// nil case, exactly as it does for `englishSpellingForRegion(region:)`.
    static func forRegion(_ region: String?) -> SeededModeValues {
        SeededModeValues(seed: modeSeedDefault(region: region))
    }

    /// The seed for the host's own region. The only region source macOS uses,
    /// and the same one `EnglishSpelling.defaultForCurrentRegion` reads.
    static var forCurrentRegion: SeededModeValues {
        forRegion(Locale.current.region?.identifier)
    }

    /// The seeded mode's name — `"Hyper"`, the product name two of the three
    /// heads already shipped.
    ///
    /// Region-invariant (`englishSpelling` is the only field a region moves,
    /// which the conformance vectors assert field by field), so callers that
    /// need just the name do not have to invent a region. Use this rather than
    /// a `"Default"` literal anywhere the value can be WRITTEN back to a mode;
    /// `?? "Default"` stays correct only where it means "this mode is unnamed".
    static var seededName: String {
        forRegion(nil).name
    }
}

extension Mode {

    /// Writes **every** column the seed owns onto a freshly inserted `Mode`.
    ///
    /// ⚠️ Core Data attribute defaults on `Mode` are hostile: a field this
    /// method stops writing does NOT come out null, it comes out with a stale
    /// legacy value. From `HyperWhisper_v30.xcdatamodel`:
    ///
    ///     language                 = "en"
    ///     model                    = "base"
    ///     cloudProvider            = "openai"
    ///     postProcessingProvider   = "openai"
    ///     postProcessingMode       = 1
    ///     cloudTranscriptionModel  = "whisper-1"   (a stale BYOK id that is not
    ///                                               valid for the ElevenLabs
    ///                                               tier — the provider would
    ///                                               fall back silently while the
    ///                                               stored value stayed wrong)
    ///     cloudPostProcessingModel = "claudeHaiku" (the legacy single-token
    ///                                               form, not "engine:model")
    ///     englishSpelling          = "american"
    ///     customInstructions       = nil
    ///
    /// So every assignment below is load-bearing even where it looks like it is
    /// restating a default. Do not delete one because "the model already says
    /// that" — the model saying that is the bug this guards against.
    ///
    /// - Parameters:
    ///   - values: the resolved seed.
    ///   - timestamp: `createdDate` / `modifiedDate`. Both are non-optional with
    ///     no model default, so they must be written too.
    func applySeededValues(_ values: SeededModeValues, timestamp: Date = Date()) {
        id = values.id
        name = values.name
        preset = values.preset
        language = values.language
        model = values.model
        punctuation = values.punctuation
        capitalization = values.capitalization
        profanityFilter = values.profanityFilter
        isDefault = values.isDefault
        isSystemProvided = values.isSystemProvided
        createdDate = timestamp
        modifiedDate = timestamp
        sortOrder = values.sortOrder
        customInstructions = values.customInstructions
        postProcessingMode = values.postProcessingMode
        postProcessingProvider = values.postProcessingProvider
        cloudProvider = values.cloudProvider
        cloudAccuracyTier = values.cloudAccuracyTier
        cloudTranscriptionModel = values.cloudTranscriptionModel
        cloudPostProcessingModel = values.cloudPostProcessingModel
        englishSpelling = values.englishSpelling
    }
}
