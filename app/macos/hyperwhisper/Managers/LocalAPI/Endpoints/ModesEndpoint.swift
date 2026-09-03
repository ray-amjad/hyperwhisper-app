//
//  ModesEndpoint.swift
//  hyperwhisper
//
//  Implements `/modes` CRUD. Thin wrapper around PersistenceController.
//

import Foundation
import CoreData
import FlyingFox

enum ModesEndpoint {

    // MARK: - List

    @MainActor
    static func list() async -> HTTPResponse {
        let modes = PersistenceController.shared.fetchAllModes()
        let dtos = modes.map(Self.toDTO(_:))
        return LocalAPIResponder.ok(ModesListResponse(ok: true, modes: dtos))
    }

    // MARK: - Get

    @MainActor
    static func get(request: HTTPRequest) async -> HTTPResponse {
        guard let id = idParameter(from: request) else {
            return LocalAPIResponder.failure(code: .invalidRequest, message: "Missing :id path parameter")
        }
        guard let mode = PersistenceController.shared.fetchMode(withId: id) else {
            return LocalAPIResponder.failure(code: .modeNotFound, message: "No mode with id '\(id)'")
        }
        return LocalAPIResponder.ok(ModeResponse(ok: true, mode: Self.toDTO(mode)))
    }

    // MARK: - Create

    /// Takes `body`, not an `HTTPRequest` (issue #375) — already read and
    /// bounded at the shared cap by `LocalAPIServer.bodied`.
    @MainActor
    static func create(body: Data) async -> HTTPResponse {
        // The keys the request ACTUALLY carried (issue #356 item 2, review
        // round 1). This head used to hand `localApiRequiredModeKeys()` to the
        // shared validator, which made `missing_required_mode_keys` empty by
        // construction — the rule was exported for three heads and evaluated on
        // two. Reading the top-level names off the same bytes is what makes the
        // rule real here: a required key added to `hw-localapi` that `ModeDTO`
        // does not happen to declare non-optional is now refused on this head
        // too, and `POST /modes {"name":"Only"}` names the missing fields
        // instead of answering the generic decode failure.
        //
        // `nil` means the body is not a JSON object at all — that is the
        // protocol failure the `badRequest` arm below was written for, and it
        // stays one.
        let presentKeys = Self.topLevelKeys(in: body)
        let dto: ModeDTO
        do { dto = try LocalAPIResponder.decoder.decode(ModeDTO.self, from: body) } catch {
            // A body that is well-formed JSON and merely INCOMPLETE is a
            // validation failure on every head, so ask the shared rule before
            // falling back. A body that is malformed, not an object, or carries
            // a wrong-typed value still gets this head's decode answer — the
            // shared rule finds nothing missing and returns nil.
            if let presentKeys,
               let failure = localApiValidateMode(input: HwLocalApiModeValidationInput(
                   operation: .create,
                   presentKeys: presentKeys,
                   name: nil,
                   language: nil,
                   preset: nil,
                   postProcessingMode: nil,
                   sortOrder: nil,
                   userSystemPrompt: nil,
                   geminiCustomPrompt: nil,
                   customVocabulary: nil
               )) {
                return LocalAPIResponder.response(for: failure)
            }
            return LocalAPIResponder.badRequest(
                message: "Invalid JSON body",
                hint: "Required: name, preset, language, model, punctuation, capitalization, profanityFilter. See /modes GET for the full shape."
            )
        }

        // `ModeNamePolicy.normalized` stays as the macOS PRE-STEP (issue #356
        // Decision D): NFC plus a general-category boundary trim, neither of
        // which `hw-localapi` can do without a Unicode dependency it refuses to
        // take in front of a loopback socket. It runs in front of the shared
        // comparison key, not instead of it.
        guard let normalizedName = Self.normalizedName(dto.name) else {
            return LocalAPIResponder.failure(code: .invalidRequest, message: "Mode 'name' cannot be empty")
        }
        // ONE MODE CONTRACT (issue #356 items 2 and 5). The bounds on `name`,
        // `language`, `preset`, `postProcessingMode`, `sortOrder` and the two
        // prompts are the shared ones now, and so is the required-key set, so
        // all three heads refuse the same bodies with the same messages.
        //
        // `presentKeys` cannot be nil here: `decode` succeeded, so the body was
        // a JSON object. The `?? []` is the total answer, and it refuses rather
        // than accepts.
        if let failure = localApiValidateMode(input: HwLocalApiModeValidationInput(
            operation: .create,
            presentKeys: presentKeys ?? [],
            name: normalizedName,
            language: dto.language,
            preset: dto.preset,
            postProcessingMode: dto.postProcessingMode.map(Int64.init),
            sortOrder: dto.sortOrder.map(Int64.init),
            userSystemPrompt: dto.userSystemPrompt,
            geminiCustomPrompt: dto.geminiCustomPrompt,
            customVocabulary: nil
        )) {
            return LocalAPIResponder.response(for: failure)
        }
        guard let postProcessingMode = Self.postProcessingModeValue(dto.postProcessingMode ?? 1) else {
            return LocalAPIResponder.failure(
                code: .invalidRequest,
                message: "Mode 'postProcessingMode' must be 0, 1, or 2"
            )
        }
        let persistence = PersistenceController.shared
        let context = Self.mutationContext(for: persistence)
        if Self.modeNameIsTaken(normalizedName, excluding: nil, in: context) {
            return LocalAPIResponder.response(for: localApiModeNameTakenFailure(
                name: normalizedName,
                operation: .create
            ))
        }

        let normalized = CloudSTTCatalog.shared.normalizeCloudProvider(dto.cloudProvider)
        let mode = persistence.createOrUpdateMode(
            id: nil,
            name: normalizedName,
            preset: dto.preset,
            language: dto.language,
            model: dto.model,
            punctuation: dto.punctuation,
            capitalization: dto.capitalization,
            profanityFilter: dto.profanityFilter,
            customInstructions: dto.customInstructions,
            languageModel: dto.languageModel,
            cloudProvider: normalized.provider,
            cloudTranscriptionModel: dto.cloudTranscriptionModel,
            postProcessingMode: postProcessingMode,
            postProcessingProvider: dto.postProcessingProvider,
            englishSpelling: dto.englishSpelling,
            userSystemPrompt: dto.userSystemPrompt,
            useStreamingTranscription: dto.useStreamingTranscription ?? false,
            cloudAccuracyTier: normalized.accuracyTier ?? dto.cloudAccuracyTier,
            removeTrailingPeriod: dto.removeTrailingPeriod ?? false,
            enableScreenOCR: dto.enableScreenOCR ?? false,
            geminiCustomPrompt: dto.geminiCustomPrompt,
            cloudPostProcessingModel: dto.cloudPostProcessingModel,
            cloudTranscriptionDomain: dto.cloudTranscriptionDomain,
            persist: false,
            in: context
        )

        do {
            try context.save()
        } catch {
            context.rollback()
            AppLogger.coreData.error("LocalAPI POST /modes: save failed · \(error.localizedDescription, privacy: .public)")
            return LocalAPIResponder.failure(code: .transcriptionFailed, message: "Failed to save mode")
        }

        return LocalAPIResponder.ok(ModeResponse(ok: true, mode: Self.toDTO(mode)))
    }

    // MARK: - Patch

    /// Still takes the `HTTPRequest`, for its `:id` path parameter only. The
    /// `body` has already been read and bounded at the shared cap by
    /// `LocalAPIServer.bodied` (issue #375), so this is the one body-reading
    /// endpoint that keeps a request, and it does not read from it.
    @MainActor
    static func patch(request: HTTPRequest, body: Data) async -> HTTPResponse {
        guard let id = idParameter(from: request) else {
            return LocalAPIResponder.failure(code: .invalidRequest, message: "Missing :id path parameter")
        }
        // Precedence, and nothing else: an unknown id is answered as
        // MODE_NOT_FOUND even when the body is *also* unparseable, which is the
        // order `GET` and `DELETE` on this resource give and the order this
        // endpoint gave before #375. Without it a client PATCHing a mode
        // somebody deleted, with a body that is slightly off, is told to fix the
        // body and chases the wrong fault.
        //
        // Its original justification is gone and this comment used to still
        // claim it. The read moved up to the router in this change, so `patch`
        // now has no `await` in it at all: there is no suspension point to keep
        // a view-context `Mode` off the far side of, and no DELETE can race one.
        // What is left is a duplicate store round-trip bought for that ordering
        // — a count fetch with `fetchLimit = 1` and no property values — and the
        // authoritative answer is still the isolated re-fetch below, which is
        // what runs in the context this handler mutates.
        guard Self.modeExists(withId: id, in: PersistenceController.shared.container.viewContext) else {
            return LocalAPIResponder.failure(code: .modeNotFound, message: "No mode with id '\(id)'")
        }
        let patch: ModePatchDTO
        do { patch = try LocalAPIResponder.decoder.decode(ModePatchDTO.self, from: body) } catch {
            return LocalAPIResponder.badRequest(message: "Invalid JSON body")
        }

        // The same bounds as create, minus the required-key rule: `ModePatch`
        // has no `required:` list, because "any field omitted is left
        // untouched" is what a patch means (issue #356). The `sortOrder` range
        // is unchanged — `Int16(exactly:)` is what this head always applied and
        // what `openapi.yaml` published — it is now the range the other two
        // heads apply as well, and the message comes from the same place.
        if let failure = localApiValidateMode(input: HwLocalApiModeValidationInput(
            operation: .patch,
            presentKeys: [],
            name: patch.name.flatMap(Self.normalizedName),
            language: patch.language,
            preset: patch.preset,
            postProcessingMode: patch.postProcessingMode.map(Int64.init),
            sortOrder: patch.sortOrder.map(Int64.init),
            userSystemPrompt: patch.userSystemPrompt,
            geminiCustomPrompt: patch.geminiCustomPrompt,
            customVocabulary: nil
        )) {
            return LocalAPIResponder.response(for: failure)
        }

        let sortOrder: Int16?
        if let value = patch.sortOrder {
            guard let converted = Self.int16Value(value) else {
                return LocalAPIResponder.failure(
                    code: .invalidRequest,
                    message: "Mode 'sortOrder' must be between \(Int16.min) and \(Int16.max)"
                )
            }
            sortOrder = converted
        } else {
            sortOrder = nil
        }

        let postProcessingMode: Int16?
        if let value = patch.postProcessingMode {
            guard let converted = Self.postProcessingModeValue(value) else {
                return LocalAPIResponder.failure(
                    code: .invalidRequest,
                    message: "Mode 'postProcessingMode' must be 0, 1, or 2"
                )
            }
            postProcessingMode = converted
        } else {
            postProcessingMode = nil
        }

        let normalizedName: String?
        if let name = patch.name {
            guard let name = Self.normalizedName(name) else {
                return LocalAPIResponder.failure(code: .invalidRequest, message: "Mode 'name' cannot be empty")
            }
            normalizedName = name
        } else {
            normalizedName = nil
        }

        // Fetch into a context that owns only this API mutation. Its save or
        // rollback cannot commit or discard unrelated pending UI edits in the
        // shared view context — and because this is a different context from
        // the existence check above, this fetch, not that one, is the answer.
        let context = Self.mutationContext(for: PersistenceController.shared)
        guard let mode = Self.fetchMode(withId: id, in: context) else {
            return LocalAPIResponder.failure(code: .modeNotFound, message: "No mode with id '\(id)'")
        }

        // Name uniqueness check — only when the caller is actually renaming.
        if let newName = normalizedName,
           newName != mode.name,
           Self.modeNameIsTaken(newName, excluding: mode.id, in: context) {
            return LocalAPIResponder.response(for: localApiModeNameTakenFailure(
                name: newName,
                operation: .patch
            ))
        }

        applyPatch(
            patch,
            normalizedName: normalizedName,
            sortOrder: sortOrder,
            postProcessingMode: postProcessingMode,
            to: mode
        )
        mode.modifiedDate = Date()

        do {
            try context.save()
        } catch {
            context.rollback()
            AppLogger.coreData.error("LocalAPI PATCH /modes: save failed · \(error.localizedDescription, privacy: .public)")
            return LocalAPIResponder.failure(code: .transcriptionFailed, message: "Failed to save mode")
        }

        return LocalAPIResponder.ok(ModeResponse(ok: true, mode: Self.toDTO(mode)))
    }

    // MARK: - Delete

    @MainActor
    static func delete(request: HTTPRequest) async -> HTTPResponse {
        guard let id = idParameter(from: request) else {
            return LocalAPIResponder.failure(code: .invalidRequest, message: "Missing :id path parameter")
        }
        guard let mode = PersistenceController.shared.fetchMode(withId: id) else {
            return LocalAPIResponder.failure(code: .modeNotFound, message: "No mode with id '\(id)'")
        }

        let all = PersistenceController.shared.fetchAllModes()
        if all.count <= 1 {
            return LocalAPIResponder.failure(
                code: .invalidRequest,
                message: "Cannot delete the last remaining mode",
                hint: "Create a replacement mode first, then delete this one."
            )
        }

        PersistenceController.shared.deleteMode(mode)
        return LocalAPIResponder.ok(OKResponse(ok: true))
    }

    // MARK: - Helpers

    private static func idParameter(from request: HTTPRequest) -> String? {
        request.routeParameters["id"]
    }

    /// The top-level key names a request body carried, or `nil` when the body is
    /// not a JSON object.
    ///
    /// This is the raw-key hook `hw-localapi`'s `validate_mode` needs to run its
    /// required-key rule (issue #356 item 2). `ModeDTO`'s decoder enforces the
    /// same seven today, but "today" is the whole problem: the crate owns that
    /// list, and a head that hands it a fabricated set can never disagree with
    /// it. The parse is one pass over bytes `LocalAPIServer.bodied` has already
    /// read and bounded at the shared upload cap, and it feeds validation only —
    /// decoding is still `ModeDTO`'s job.
    static func topLevelKeys(in body: Data) -> [String]? {
        guard let parsed = try? JSONSerialization.jsonObject(with: body),
              let object = parsed as? [String: Any] else { return nil }
        return Array(object.keys)
    }

    static func normalizedName(_ name: String) -> String? {
        ModeNamePolicy.normalized(name)
    }

    static func int16Value(_ value: Int) -> Int16? {
        Int16(exactly: value)
    }

    static func postProcessingModeValue(_ value: Int) -> Int16? {
        guard let rawValue = Int16(exactly: value),
              PostProcessingMode(rawValue: rawValue) != nil else {
            return nil
        }
        return rawValue
    }

    @MainActor
    static func mutationContext(for persistence: PersistenceController) -> NSManagedObjectContext {
        let context = NSManagedObjectContext(concurrencyType: .mainQueueConcurrencyType)
        context.persistentStoreCoordinator = persistence.container.persistentStoreCoordinator
        context.mergePolicy = NSMergeByPropertyObjectTrumpMergePolicy
        context.undoManager = nil
        return context
    }

    @MainActor
    static func modeExists(withId id: String, in context: NSManagedObjectContext) -> Bool {
        guard let uuid = UUID(uuidString: id) else { return false }
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.predicate = NSPredicate(format: "id == %@", uuid as CVarArg)
        request.fetchLimit = 1
        request.includesPropertyValues = false
        return (try? context.count(for: request)) == 1
    }

    @MainActor
    private static func fetchMode(withId id: String, in context: NSManagedObjectContext) -> Mode? {
        guard let uuid = UUID(uuidString: id) else { return nil }
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.predicate = NSPredicate(format: "id == %@", uuid as CVarArg)
        request.fetchLimit = 1
        return try? context.fetch(request).first
    }

    /// Whether `candidate` collides with a stored mode other than
    /// `excludedId`, under the SHARED comparison key (issue #356 item 5).
    ///
    /// This used to be a store-side `name ==[c] %@` fetch with `fetchLimit = 1`.
    /// Core Data's `==[c]` is one of three different answers to "the same name"
    /// the three heads gave — Windows and Linux used .NET's
    /// `OrdinalIgnoreCase`, which is simple case mapping and not the same
    /// function — so the predicate had to come back into Swift for all three to
    /// agree. `mode_name_comparison_key` (trim, then `to_lowercase`) is that one
    /// definition now.
    ///
    /// The cost is a full mode fetch instead of a limited one. This endpoint
    /// already does full mode fetches to list and to delete, and a Local API
    /// user has a handful of modes, not thousands.
    ///
    /// `ModeNamePolicy.normalized` still runs in front of this on the candidate
    /// — NFC and the general-category boundary trim are macOS's and stay
    /// macOS's (Decision D). The stored names go in as they were stored.
    @MainActor
    private static func modeNameIsTaken(
        _ candidate: String,
        excluding excludedId: UUID?,
        in context: NSManagedObjectContext
    ) -> Bool {
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        let stored = ((try? context.fetch(request)) ?? [])
            .filter { mode in
                guard let excludedId else { return true }
                return mode.id != excludedId
            }
            .compactMap(\.name)
        return localApiModeNameConflict(candidate: candidate, otherNames: stored)
    }

    /// Apply only the present keys of a `ModePatchDTO` onto an existing Mode.
    /// Absent (nil) keys are left untouched; the GUI doesn't validate combinations
    /// either, so we trust the caller.
    @MainActor
    private static func applyPatch(
        _ patch: ModePatchDTO,
        normalizedName: String?,
        sortOrder: Int16?,
        postProcessingMode: Int16?,
        to mode: Mode
    ) {
        if let normalizedName { mode.name = normalizedName }
        if let v = patch.preset { mode.preset = v }
        if let v = patch.language { mode.language = LanguageData.canonicalLanguageCode(v) }
        if let v = patch.model { mode.model = v }
        if let v = patch.punctuation { mode.punctuation = v }
        if let v = patch.capitalization { mode.capitalization = v }
        if let v = patch.profanityFilter { mode.profanityFilter = v }
        if case .value(let value) = patch.$customInstructions { mode.customInstructions = value }
        if case .value(let value) = patch.$userSystemPrompt {
            mode.userSystemPrompt = value.flatMap { $0.isEmpty ? nil : $0 }
        }
        if let v = patch.isDefault { mode.isDefault = v }
        if let sortOrder { mode.sortOrder = sortOrder }
        if case .value(let value) = patch.$languageModel { mode.languageModel = value }
        if case .value(let value) = patch.$cloudTranscriptionModel { mode.cloudTranscriptionModel = value }
        if case .value(let value) = patch.$cloudTranscriptionDomain { mode.cloudTranscriptionDomain = value }
        var inferredAccuracyTier: String? = nil
        if case .value(let value) = patch.$cloudProvider {
            if let value {
                let normalized = CloudSTTCatalog.shared.normalizeCloudProvider(value)
                mode.cloudProvider = normalized.provider
                inferredAccuracyTier = normalized.accuracyTier
            } else {
                mode.cloudProvider = nil
            }
        }
        if let postProcessingMode { mode.postProcessingMode = postProcessingMode }
        if case .value(let value) = patch.$postProcessingProvider { mode.postProcessingProvider = value }
        if case .value(let value) = patch.$englishSpelling { mode.englishSpelling = value }
        if let v = patch.useStreamingTranscription { mode.useStreamingTranscription = v }
        // Prefer an explicit patch over the migration's inferred tier so a
        // same-PATCH cloudProvider+cloudAccuracyTier pair lands as the caller
        // wrote it.
        switch patch.$cloudAccuracyTier {
        case .value(let value):
            mode.cloudAccuracyTier = value
        case .omitted:
            if let inferredAccuracyTier { mode.cloudAccuracyTier = inferredAccuracyTier }
        }
        if let v = patch.removeTrailingPeriod { mode.removeTrailingPeriod = v }
        if let v = patch.enableScreenOCR { mode.enableScreenOCR = v }
        if case .value(let value) = patch.$geminiCustomPrompt {
            mode.geminiCustomPrompt = value.flatMap { $0.isEmpty ? nil : $0 }
        }
        if case .value(let value) = patch.$cloudPostProcessingModel {
            mode.cloudPostProcessingModel = value
        }
    }

    @MainActor
    static func toDTO(_ mode: Mode) -> ModeDTO {
        ModeDTO(
            id: mode.id?.uuidString,
            name: mode.name ?? "",
            preset: mode.preset ?? "hyper",
            language: mode.language ?? "en",
            model: mode.model ?? "base",
            punctuation: mode.punctuation,
            capitalization: mode.capitalization,
            profanityFilter: mode.profanityFilter,
            customInstructions: mode.customInstructions,
            userSystemPrompt: mode.userSystemPrompt,
            isDefault: mode.isDefault,
            isSystemProvided: mode.isSystemProvided,
            sortOrder: Int(mode.sortOrder),
            createdDate: mode.createdDate,
            modifiedDate: mode.modifiedDate,
            languageModel: mode.languageModel,
            cloudTranscriptionModel: mode.cloudTranscriptionModel,
            cloudTranscriptionDomain: mode.cloudTranscriptionDomain,
            cloudProvider: mode.cloudProvider,
            postProcessingMode: Int(mode.postProcessingMode),
            postProcessingProvider: mode.postProcessingProvider,
            englishSpelling: mode.englishSpelling,
            useStreamingTranscription: mode.useStreamingTranscription,
            cloudAccuracyTier: mode.cloudAccuracyTier,
            removeTrailingPeriod: mode.removeTrailingPeriod,
            enableScreenOCR: mode.enableScreenOCR,
            geminiCustomPrompt: mode.geminiCustomPrompt,
            cloudPostProcessingModel: mode.cloudPostProcessingModel
        )
    }
}
