//
//  LocalModelFetcher.swift
//  hyperwhisper
//
//  LOCAL AI MODEL FETCHER SERVICE
//  Centralized service for fetching available models from local AI providers
//  like Ollama and LMStudio.
//
//  Supported Providers:
//  - Ollama: GET /api/tags
//  - LMStudio: GET /v1/models (OpenAI-compatible)
//

import Foundation

/// Service for fetching available models from local AI providers
class LocalModelFetcher {

    // MARK: - Ollama Response Types

    /// Response from Ollama's /api/tags endpoint
    struct OllamaModelsResponse: Decodable {
        let models: [OllamaModel]
    }

    /// Individual model from Ollama
    struct OllamaModel: Decodable {
        let name: String
        let modified_at: String?
        let size: Int64?
    }

    // MARK: - LMStudio Response Types

    /// Response from LMStudio's /v1/models endpoint (OpenAI-compatible)
    struct LMStudioModelsResponse: Decodable {
        let data: [LMStudioModel]
    }

    /// Individual model from LMStudio
    struct LMStudioModel: Decodable {
        let id: String
    }

    // MARK: - Error Types

    enum FetchError: LocalizedError {
        case invalidURL
        case serverNotRunning
        case invalidResponse(statusCode: Int)
        case decodingFailed
        case networkError(String)

        var errorDescription: String? {
            switch self {
            case .invalidURL:
                return "Invalid URL format"
            case .serverNotRunning:
                return "Server is not running or unreachable"
            case .invalidResponse(let statusCode):
                return "Server returned error (HTTP \(statusCode))"
            case .decodingFailed:
                return "Failed to parse server response"
            case .networkError(let message):
                return message
            }
        }
    }

    // MARK: - Fetch Methods

    /// Fetch available models from Ollama
    /// - Parameter baseURL: Ollama base URL (default: http://localhost:11434)
    /// - Returns: Array of model names
    func fetchOllamaModels(baseURL: String = "http://localhost:11434") async throws -> [String] {
        try await fetchModels(baseURL: baseURL, path: "/api/tags", as: OllamaModelsResponse.self) {
            $0.models.map(\.name)
        }
    }

    /// Fetch available models from LMStudio
    /// - Parameter baseURL: LMStudio base URL (default: http://localhost:1234/v1)
    /// - Returns: Array of model IDs
    func fetchLMStudioModels(baseURL: String = "http://localhost:1234/v1") async throws -> [String] {
        try await fetchModels(baseURL: baseURL, path: "/models", as: LMStudioModelsResponse.self) {
            $0.data.map(\.id)
        }
    }

    /// GET `baseURL + path`, decode the body as `R` and map it to model names.
    private func fetchModels<R: Decodable>(
        baseURL: String,
        path: String,
        as responseType: R.Type,
        names: (R) -> [String]
    ) async throws -> [String] {
        // Normalize URL - remove trailing slash if present
        let normalizedBase = baseURL.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let endpoint = "\(normalizedBase)\(path)"

        guard let url = URL(string: endpoint) else {
            throw FetchError.invalidURL
        }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.timeoutInterval = 10

        do {
            let (data, response) = try await URLSession.shared.data(for: request)

            guard let httpResponse = response as? HTTPURLResponse else {
                throw FetchError.serverNotRunning
            }

            guard httpResponse.statusCode == 200 else {
                throw FetchError.invalidResponse(statusCode: httpResponse.statusCode)
            }

            do {
                let decoded = try JSONDecoder().decode(responseType, from: data)
                return names(decoded)
            } catch {
                throw FetchError.decodingFailed
            }

        } catch let error as FetchError {
            throw error
        } catch let error as URLError {
            if error.code == .cannotConnectToHost || error.code == .timedOut {
                throw FetchError.serverNotRunning
            }
            throw FetchError.networkError(error.localizedDescription)
        } catch {
            throw FetchError.networkError(error.localizedDescription)
        }
    }
}
