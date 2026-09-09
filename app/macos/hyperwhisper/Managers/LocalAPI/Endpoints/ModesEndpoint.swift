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
        let dto: ModeDTO
        do { dto = try LocalAPIResponder.decoder.decode(ModeDTO.self, from: body) } catch {
            return LocalAPIResponder.badRequest(
                message: "Invalid JSON body",
                hint: "Required: name, preset, language, model, punctuation, capitalization, profanityFilter. See /modes GET for the full shape."
            )
        }

        guard let normalizedName = Self.normalizedName(dto.name) else {
            return LocalAPIResponder.failure(code: .invalidRequest, message: "Mode 'name' cannot be empty")
        }
        guard let postProcessingMode = Self.postProcessingModeValue(dto.postProcessingMode ?? 1) else {
            return LocalAPIResponder.failure(
                code: .invalidRequest,
                message: "Mode 'postProcessingMode' must be 0, 1, or 2"
            )
        }
        let persistence = PersistenceController.shared
        let context = Self.mutationContext(for: persistence)
        if Self.fetchMode(byName: normalizedName, in: context) != nil {
            return LocalAPIResponder.failure(
                code: .modeNameTaken,
                message: "A mode named '\(normalizedName)' already exists",
                hint: "Choose a different name or PATCH the existing mode instead."
            )
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

        // The first mode in an empty store is the default (issue #536). Any
        // other new mode is not, and this is a no-op.
        DefaultModePolicy.apply(to: Self.fetchAllModes(in: context))

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

        // The default mode's name is fixed (issue #536). The editor disables the
        // field and says so (PR #535); accepting the rename here made that
        // caption a lie, on a field the user cannot then use to put the name
        // back.
        if let newName = normalizedName, !DefaultModePolicy.canRename(mode, to: newName) {
            return LocalAPIResponder.failure(
                code: .invalidRequest,
                message: "The default mode's name cannot be changed",
                hint: "Rename a different mode, or make a different mode the default first."
            )
        }

        // Exactly one mode is the default. Setting the flag is always allowed —
        // it moves, clearing whichever mode held it — but clearing the only one
        // is refused, because it left the app with no default and merely ACTING
        // as if the first mode were one.
        // Read the flags out of the context this handler saves. The view
        // context can still hold the pre-save copies of a sibling API write, so
        // a guard answered from there can refuse a safe clear — or allow one
        // that empties the store.
        let allModes = Self.fetchAllModes(in: context)
        if let requested = patch.isDefault,
           let modeId = mode.id,
           !DefaultModePolicy.canWriteDefaultFlag(allModes, id: modeId, requested: requested) {
            return LocalAPIResponder.failure(
                code: .invalidRequest,
                message: "At least one mode must be the default",
                hint: "Set 'isDefault' on another mode instead — that clears this one."
            )
        }

        // Name uniqueness check — only when the caller is actually renaming.
        if let newName = normalizedName,
           newName != mode.name,
           let clash = Self.fetchMode(byName: newName, in: context),
           clash.id != mode.id {
            return LocalAPIResponder.failure(
                code: .modeNameTaken,
                message: "A mode named '\(newName)' already exists"
            )
        }

        applyPatch(
            patch,
            normalizedName: normalizedName,
            sortOrder: sortOrder,
            postProcessingMode: postProcessingMode,
            to: mode
        )
        mode.modifiedDate = Date()

        // Making this mode the default clears the flag on every other one
        // (issue #536). Applied in THIS context — the one this handler saves —
        // because a repair made against the view context's copies would not be
        // part of this save.
        //
        // `preferred` is the mode the caller ASKED for, so it is only this mode
        // when the request set the flag. Passing it unconditionally made
        // `{"isDefault": false}` on an ordinary mode promote that mode instead
        // of leaving it alone — `plan_default` honours `preferred` first, so it
        // put the flag straight back and cleared the real default. Windows and
        // the portable head both spell this `mode.IsDefault ? mode.Id : null`;
        // after `applyPatch`, `mode.isDefault` is the requested value.
        if patch.isDefault != nil {
            DefaultModePolicy.apply(
                to: Self.fetchAllModes(in: context),
                preferred: mode.isDefault ? mode.id : nil
            )
        }

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

    /// Every mode, in display order, out of the context this handler mutates —
    /// not out of the view context, whose copies this handler never saves.
    @MainActor
    private static func fetchAllModes(in context: NSManagedObjectContext) -> [Mode] {
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Mode.sortOrder, ascending: true)]
        return (try? context.fetch(request)) ?? []
    }

    @MainActor
    private static func fetchMode(withId id: String, in context: NSManagedObjectContext) -> Mode? {
        guard let uuid = UUID(uuidString: id) else { return nil }
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.predicate = NSPredicate(format: "id == %@", uuid as CVarArg)
        request.fetchLimit = 1
        return try? context.fetch(request).first
    }

    @MainActor
    private static func fetchMode(byName name: String, in context: NSManagedObjectContext) -> Mode? {
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.predicate = NSPredicate(format: "name ==[c] %@", name)
        request.fetchLimit = 1
        return try? context.fetch(request).first
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
