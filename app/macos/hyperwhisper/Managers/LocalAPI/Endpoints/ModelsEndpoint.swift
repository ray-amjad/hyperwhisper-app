//
//  ModelsEndpoint.swift
//  hyperwhisper
//
//  Implements `GET /models?kind=voice|text|all&installed_only=true|false`.
//  Flattens ModelLibraryManager's aggregated list into the public API shape.
//

import Foundation
import FlyingFox

enum ModelsEndpoint {

    @MainActor
    static func handle(request: HTTPRequest, modelLibrary: ModelLibraryManager?) async -> HTTPResponse {
        let queryParams = Self.queryItems(from: request)
        let kindFilter = queryParams["kind"]?.lowercased() ?? "all"
        let installedOnlyRaw = queryParams["installed_only"]?.lowercased() ?? "false"
        let installedOnly = installedOnlyRaw == "true" || installedOnlyRaw == "1" || installedOnlyRaw == "yes"

        guard let library = modelLibrary else {
            return LocalAPIResponder.ok(ModelsListResponse(ok: true, models: []))
        }

        let mapped = library.models.compactMap { libModel -> ModelEntry? in
            let kind = libModel.kind.rawValue
            if kindFilter != "all", kindFilter != kind {
                return nil
            }
            let (installed, sizeMB) = Self.installedAndSize(from: libModel.location)
            if installedOnly, !installed {
                return nil
            }
            return ModelEntry(
                id: libModel.id,
                kind: kind,
                provider: Self.providerString(for: libModel.providerKey),
                displayName: libModel.displayName,
                installed: installed,
                size_mb: sizeMB
            )
        }

        return LocalAPIResponder.ok(ModelsListResponse(ok: true, models: mapped))
    }

    // MARK: - Helpers

    @MainActor
    private static func providerString(for key: LibraryProviderKey) -> String {
        switch key {
        case .cloud(let provider): return provider.rawValue
        case .postProcessing(let provider): return provider.rawValue
        case .appleSpeech: return "apple"
        case .localWhisper: return "local"
        case .parakeet: return "local"
        case .qwen3ASR: return "local"
        case .nemotron: return "local"
        }
    }

    @MainActor
    private static func installedAndSize(from location: LibraryModelLocation) -> (Bool, Double?) {
        switch location {
        case .cloud:
            // Cloud models are "installed" iff the provider is reachable; the
            // library list itself only includes available cloud models. We
            // surface installed=true here so cloud entries always pass the
            // `installed_only=true` filter — that filter is mainly for users
            // wanting to know what local files exist.
            return (true, nil)
        case .offline(let sizeDescription, let installed, _):
            return (installed, Self.parseSizeMB(sizeDescription))
        }
    }

    private static func parseSizeMB(_ description: String?) -> Double? {
        guard let s = description?.lowercased() else { return nil }
        // Accept "474 MB", "2.96 GB", "1.5 gb", "Built-in" (returns nil) etc.
        if s.contains("built-in") { return nil }
        let scanner = Scanner(string: s)
        scanner.charactersToBeSkipped = .whitespaces
        var value: Double = 0
        guard scanner.scanDouble(&value) else { return nil }
        if s.contains("gb") { return value * 1024 }
        if s.contains("mb") { return value }
        if s.contains("kb") { return value / 1024 }
        return value
    }

    /// FlyingFox exposes the query string in different shapes across versions.
    /// This helper coalesces them into a plain dictionary so the endpoint
    /// code doesn't have to care.
    static func queryItems(from request: HTTPRequest) -> [String: String] {
        var result: [String: String] = [:]
        // FlyingFox 0.21+ surfaces query items on the request as
        // `[HTTPRequest.QueryItem]`. Each item has a `.name` and `.value`.
        for item in request.query {
            result[item.name] = item.value
        }
        return result
    }
}
