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

    @MainActor
    static func create(request: HTTPRequest) async -> HTTPResponse {
        let body: Data
        do { body = try await request.bodyData } catch {
            return LocalAPIResponder.badRequest(message: "Could not read request body")
        }

        let dto: ModeDTO
        do { dto = try LocalAPIResponder.decoder.decode(ModeDTO.self, from: body) } catch {
            return LocalAPIResponder.badRequest(
                message: "Invalid JSON body",
                hint: "Required: name, preset, language, model, punctuation, capitalization, profanityFilter. See /modes GET for the full shape."
            )
        }

        let trimmedName = dto.name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmedName.isEmpty {
            return LocalAPIResponder.failure(code: .invalidRequest, message: "Mode 'name' cannot be empty")
        }
        if PersistenceController.shared.fetchMode(byName: trimmedName) != nil {
            return LocalAPIResponder.failure(
                code: .modeNameTaken,
                message: "A mode named '\(trimmedName)' already exists",
                hint: "Choose a different name or PATCH the existing mode instead."
            )
        }

        let normalized = CloudSTTCatalog.shared.normalizeCloudProvider(dto.cloudProvider)
        let mode = PersistenceController.shared.createOrUpdateMode(
            id: nil,
            name: trimmedName,
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
            postProcessingMode: Int16(dto.postProcessingMode ?? 1),
            postProcessingProvider: dto.postProcessingProvider,
            englishSpelling: dto.englishSpelling,
            userSystemPrompt: dto.userSystemPrompt,
            useStreamingTranscription: dto.useStreamingTranscription ?? false,
            cloudAccuracyTier: normalized.accuracyTier ?? dto.cloudAccuracyTier,
            removeTrailingPeriod: dto.removeTrailingPeriod ?? false,
            enableScreenOCR: dto.enableScreenOCR ?? false,
            geminiCustomPrompt: dto.geminiCustomPrompt,
            cloudPostProcessingModel: dto.cloudPostProcessingModel,
            cloudTranscriptionDomain: dto.cloudTranscriptionDomain
        )

        return LocalAPIResponder.ok(ModeResponse(ok: true, mode: Self.toDTO(mode)))
    }

    // MARK: - Patch

    @MainActor
    static func patch(request: HTTPRequest) async -> HTTPResponse {
        guard let id = idParameter(from: request) else {
            return LocalAPIResponder.failure(code: .invalidRequest, message: "Missing :id path parameter")
        }
        guard let mode = PersistenceController.shared.fetchMode(withId: id) else {
            return LocalAPIResponder.failure(code: .modeNotFound, message: "No mode with id '\(id)'")
        }

        let body: Data
        do { body = try await request.bodyData } catch {
            return LocalAPIResponder.badRequest(message: "Could not read request body")
        }
        let patch: ModePatchDTO
        do { patch = try LocalAPIResponder.decoder.decode(ModePatchDTO.self, from: body) } catch {
            return LocalAPIResponder.badRequest(message: "Invalid JSON body")
        }

        // Name uniqueness check — only when the caller is actually renaming.
        if let newName = patch.name?.trimmingCharacters(in: .whitespacesAndNewlines),
           !newName.isEmpty,
           newName != mode.name,
           let clash = PersistenceController.shared.fetchMode(byName: newName),
           clash.id != mode.id {
            return LocalAPIResponder.failure(
                code: .modeNameTaken,
                message: "A mode named '\(newName)' already exists"
            )
        }

        applyPatch(patch, to: mode)
        mode.modifiedDate = Date()

        do {
            try PersistenceController.shared.container.viewContext.save()
        } catch {
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

    /// Apply only the present keys of a `ModePatchDTO` onto an existing Mode.
    /// Absent (nil) keys are left untouched; the GUI doesn't validate combinations
    /// either, so we trust the caller.
    @MainActor
    private static func applyPatch(_ patch: ModePatchDTO, to mode: Mode) {
        if let v = patch.name { mode.name = v }
        if let v = patch.preset { mode.preset = v }
        if let v = patch.language { mode.language = LanguageData.canonicalLanguageCode(v) }
        if let v = patch.model { mode.model = v }
        if let v = patch.punctuation { mode.punctuation = v }
        if let v = patch.capitalization { mode.capitalization = v }
        if let v = patch.profanityFilter { mode.profanityFilter = v }
        if let v = patch.customInstructions { mode.customInstructions = v }
        if let v = patch.userSystemPrompt { mode.userSystemPrompt = v.isEmpty ? nil : v }
        if let v = patch.isDefault { mode.isDefault = v }
        if let v = patch.sortOrder { mode.sortOrder = Int16(v) }
        if let v = patch.languageModel { mode.languageModel = v }
        if let v = patch.cloudTranscriptionModel { mode.cloudTranscriptionModel = v }
        if let v = patch.cloudTranscriptionDomain { mode.cloudTranscriptionDomain = v }
        var inferredAccuracyTier: String? = nil
        if let v = patch.cloudProvider {
            let normalized = CloudSTTCatalog.shared.normalizeCloudProvider(v)
            mode.cloudProvider = normalized.provider
            inferredAccuracyTier = normalized.accuracyTier
        }
        if let v = patch.postProcessingMode { mode.postProcessingMode = Int16(v) }
        if let v = patch.postProcessingProvider { mode.postProcessingProvider = v }
        if let v = patch.englishSpelling { mode.englishSpelling = v }
        if let v = patch.useStreamingTranscription { mode.useStreamingTranscription = v }
        // Prefer an explicit patch over the migration's inferred tier so a
        // same-PATCH cloudProvider+cloudAccuracyTier pair lands as the caller
        // wrote it.
        if let v = patch.cloudAccuracyTier ?? inferredAccuracyTier {
            mode.cloudAccuracyTier = v
        }
        if let v = patch.removeTrailingPeriod { mode.removeTrailingPeriod = v }
        if let v = patch.enableScreenOCR { mode.enableScreenOCR = v }
        if let v = patch.geminiCustomPrompt { mode.geminiCustomPrompt = v.isEmpty ? nil : v }
        if let v = patch.cloudPostProcessingModel { mode.cloudPostProcessingModel = v }
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
