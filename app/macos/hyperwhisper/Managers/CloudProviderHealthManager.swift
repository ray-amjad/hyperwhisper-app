import Foundation
import Combine

// PROVIDER HEALTH DATA FLOW (REQUIRED):
// 1. UI surfaces such as SettingsView invoke registerAPIKeyChange(...) whenever the user edits
//    an API key. This invalidates cached results and schedules a debounced refresh.
// 2. Once the 500 ms debounce elapses without further edits, refresh(provider, force: true)
//    executes on the main actor.
// 3. refresh(...) marks the provider as .checking (without discarding the last cached value) and
//    hands the actual HTTP probe off to scheduleHealthCheck(...).
// 4. scheduleHealthCheck coalesces duplicate requests, runs the asynchronous network call on a
//    background executor, applies retry logic for transient failures, and then republishes the
//    resulting status back on the main actor.
// 5. Published dictionaries drive SwiftUI badges and the recording guardrails, ensuring the UI
//    reacts instantly to health changes without hammering provider APIs.
//
// This verbose documentation is mandated by CLAUDE.md so future contributors can walk the full
// lifecycle without reverse-engineering the control flow.

/// Protocol that decouples HTTP transport so unit tests can inject deterministic stubs.
protocol HealthCheckHTTPClient {
    func send(_ request: URLRequest) async throws -> (Data, URLResponse)
}

/// Default implementation that wraps URLSession while conforming to HealthCheckHTTPClient.
struct URLSessionHealthCheckClient: HealthCheckHTTPClient {
    private let session: URLSession

    init(configuration: URLSessionConfiguration = .ephemeral) {
        self.session = URLSession(configuration: configuration)
    }

    func send(_ request: URLRequest) async throws -> (Data, URLResponse) {
        try await session.data(for: request)
    }
}

/// Represents the current health of a cloud provider API integration.
enum ProviderHealth: Equatable {
    case unknown
    case checking
    case healthy
    case unauthorized
    case unreachable
    case notInstalled

    /// Whether this status represents a state that allows cloud operations.
    var isHealthy: Bool {
        if case .healthy = self { return true }
        return false
    }

    /// Human-readable text for UI badges.
    var statusText: String {
        switch self {
        case .unknown:
            return "provider.status.unknown".localized
        case .checking:
            return "provider.status.checking".localized
        case .healthy:
            return "provider.status.healthy".localized
        case .unauthorized:
            return "provider.status.unauthorized".localized
        case .unreachable:
            return "provider.status.unreachable".localized
        case .notInstalled:
            return "provider.status.notInstalled".localized
        }
    }

    /// Whether this status should block starting a transcription.
    var shouldBlockTranscription: Bool {
        switch self {
        case .healthy:
            return false
        case .checking:
            // UX IMPROVEMENT: permit users to initiate recording while we finish probing. The
            // actual transcription pipeline still awaits ensureHealthy(_:) so we never send
            // audio to a backend that ultimately fails.
            return false
        case .unknown, .unauthorized, .unreachable, .notInstalled:
            return true
        }
    }
}

/// Abstraction that lets CloudProviderHealthManager ask for API keys without hard coupling to
/// SettingsManager. The project already uses this protocol to keep dependencies testable.
protocol CloudProviderAPIKeyProviding: AnyObject {
    func apiKey(for provider: CloudProvider) -> String
    func postProcessingAPIKey(for provider: PostProcessingProvider) -> String
}

/// Sendable value-type capture of the manager's published state at a single point in time.
/// HTTP handlers and other off-actor consumers should read this instead of touching the
/// `@Published` dictionaries directly, which can be mutated mid-refresh.
struct HealthSnapshot: Sendable {
    let cloud: [String: String]
    let postProcessing: [String: String]
    let timestamp: Date
}

/// Central registry that tracks the live health of every configured cloud provider.
@MainActor
final class CloudProviderHealthManager: ObservableObject {
    // MARK: - Published State

    /// Latest status for each provider, updated whenever checks succeed or fail.
    @Published private(set) var statuses: [CloudProvider: ProviderHealth]
    @Published private(set) var postProcessingStatuses: [PostProcessingProvider: ProviderHealth]

    // MARK: - Dependencies & Injectables

    private weak var apiKeyProvider: CloudProviderAPIKeyProviding?
    // STT health probes now go through the Rust shared core (M3-B.4), so the
    // per-vendor provider instances are no longer needed here. `sharedCloudProvider`
    // and `mistralProvider` are retained because the POST-PROCESSING health switch
    // still delegates OpenAI/Groq/Mistral to them.
    private let sharedCloudProvider: CloudWhisperProvider
    private let mistralProvider: MistralProvider
    private let httpClient: HealthCheckHTTPClient

    /// Dedicated ephemeral session for the Rust-core STT health probes
    /// (`performRustHealthCheck`). Kept separate from the injectable
    /// `httpClient` (still used by the post-processing probes) so health checks
    /// never persist cookies/credentials and carry their own short timeout.
    private lazy var rustHealthSession: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 10
        config.timeoutIntervalForResource = 15
        return URLSession(configuration: config)
    }()

    // MARK: - Runtime State

    /// In-flight tasks ensure we do not spawn duplicate network probes for the same provider.
    private var pendingChecks: [CloudProvider: Task<ProviderHealth, Never>] = [:]
    private var pendingPostProcessingChecks: [PostProcessingProvider: Task<ProviderHealth, Never>] = [:]

    /// Cached results paired with timestamps so we can enforce a TTL and avoid rate limits.
    private var cache: [CloudProvider: StatusRecord] = [:]
    private var postProcessingCache: [PostProcessingProvider: StatusRecord] = [:]

    /// Debounced refresh tasks prevent thrashing while a user pastes or types an API key.
    private var debouncedRefreshTasks: [CloudProvider: Task<Void, Never>] = [:]
    private var debouncedPostProcessingTasks: [PostProcessingProvider: Task<Void, Never>] = [:]

    private let cacheTTL: TimeInterval = 60
    private let debounceDelay: UInt64 = 500_000_000 // 500 ms expressed in nanoseconds
    private let maxRetryAttempts = 3
    private let minimumProbeLength = 16

    /// Helper structure remembering both the status and the timestamp it was confirmed.
    private struct StatusRecord {
        let status: ProviderHealth
        let timestamp: Date
    }

    init(
        sharedCloudProvider: CloudWhisperProvider = CloudWhisperProvider(),
        mistralProvider: MistralProvider = MistralProvider(),
        httpClient: HealthCheckHTTPClient = URLSessionHealthCheckClient()
    ) {
        self.sharedCloudProvider = sharedCloudProvider
        self.mistralProvider = mistralProvider
        self.httpClient = httpClient

        var initialStatuses: [CloudProvider: ProviderHealth] = [:]
        CloudProvider.allCases.forEach { initialStatuses[$0] = .unknown }
        self.statuses = initialStatuses

        var initialPost: [PostProcessingProvider: ProviderHealth] = [:]
        PostProcessingProvider.allCases.forEach { provider in
            if provider == .localLLM {
                initialPost[provider] = Self.localLLMStatus()
            } else {
                initialPost[provider] = provider.requiresHealthCheck ? .unknown : .healthy
            }
        }
        self.postProcessingStatuses = initialPost
    }

    /// Inject the object that can supply API keys (typically SettingsManager).
    func configure(apiKeyProvider: CloudProviderAPIKeyProviding) {
        self.apiKeyProvider = apiKeyProvider
    }

    /// Snapshot of the current status for a provider (defaults to .unknown).
    func status(for provider: CloudProvider) -> ProviderHealth {
        statuses[provider] ?? .unknown
    }

    /// Frozen, Sendable copy of both status dictionaries. Used by the Local API
    /// `/health` endpoint so the handler doesn't read the `@Published` dictionary
    /// while a refresh is mutating it.
    func healthSnapshot() -> HealthSnapshot {
        var cloud: [String: String] = [:]
        for (provider, health) in statuses {
            cloud[provider.rawValue] = Self.healthRawString(health)
        }
        var post: [String: String] = [:]
        for (provider, health) in postProcessingStatuses {
            post[provider.rawValue] = Self.healthRawString(health)
        }
        return HealthSnapshot(cloud: cloud, postProcessing: post, timestamp: Date())
    }

    /// Stable raw strings for `ProviderHealth`, intentionally separate from the
    /// localized `statusText` so the API contract stays in English.
    static func healthRawString(_ status: ProviderHealth) -> String {
        switch status {
        case .unknown: return "unknown"
        case .checking: return "checking"
        case .healthy: return "healthy"
        case .unauthorized: return "unauthorized"
        case .unreachable: return "unreachable"
        case .notInstalled: return "notInstalled"
        }
    }

    /// Snapshot of the current status for a post-processing provider.
    func status(for provider: PostProcessingProvider) -> ProviderHealth {
        if provider == .localLLM {
            return Self.localLLMStatus()
        }
        return postProcessingStatuses[provider] ?? .unknown
    }

    /// Trigger async health checks for all cloud providers.
    func refreshAll(force: Bool = false) {
        CloudProvider.allCases.forEach { refresh($0, force: force) }
    }

    /// Trigger async health checks for all post-processing providers.
    func refreshAllPostProcessing(force: Bool = false) {
        PostProcessingProvider.allCases.forEach { provider in
            if provider == .localLLM {
                postProcessingStatuses[provider] = Self.localLLMStatus()
            } else if provider.requiresHealthCheck {
                refresh(provider, force: force)
            } else {
                postProcessingStatuses[provider] = .healthy
            }
        }
    }

    /// Trigger a health check for a single provider. Cache prevents redundant work.
    func refresh(_ provider: CloudProvider, force: Bool = false) {
        if !force,
           let record = cache[provider],
           Date().timeIntervalSince(record.timestamp) < cacheTTL,
           record.status != .unknown {
            statuses[provider] = record.status
            return
        }

        statuses[provider] = .checking

        if pendingChecks[provider] != nil { return }

        scheduleHealthCheck(for: provider, force: force)
    }

    /// Trigger a health check for a single post-processing provider.
    func refresh(_ provider: PostProcessingProvider, force: Bool = false) {
        if provider == .localLLM {
            postProcessingStatuses[provider] = Self.localLLMStatus()
            return
        }

        guard provider.requiresHealthCheck else {
            postProcessingStatuses[provider] = .healthy
            return
        }

        if !force,
           let record = postProcessingCache[provider],
           Date().timeIntervalSince(record.timestamp) < cacheTTL,
           record.status != .unknown {
            postProcessingStatuses[provider] = record.status
            return
        }

        postProcessingStatuses[provider] = .checking

        if pendingPostProcessingChecks[provider] != nil { return }

        scheduleHealthCheck(for: provider, force: force)
    }

    /// Ensure a provider is healthy before kicking off a transcription.
    /// Runs the check immediately if we don't already have a healthy status.
    func ensureHealthy(_ provider: CloudProvider) async -> ProviderHealth {
        if let task = pendingChecks[provider] {
            return await task.value
        }
        let current = status(for: provider)
        if current.isHealthy {
            return current
        }
        statuses[provider] = .checking
        let task = scheduleHealthCheck(for: provider, force: true)
        return await task.value
    }

    /// Ensure a post-processing provider is healthy before invoking it.
    func ensureHealthy(_ provider: PostProcessingProvider) async -> ProviderHealth {
        if provider == .localLLM {
            let status = Self.localLLMStatus()
            postProcessingStatuses[provider] = status
            return status
        }

        if let task = pendingPostProcessingChecks[provider] {
            return await task.value
        }
        let current = status(for: provider)
        if current.isHealthy {
            return current
        }
        postProcessingStatuses[provider] = .checking
        let task = scheduleHealthCheck(for: provider, force: true)
        return await task.value
    }

    // MARK: - API Key Mutation Handling

    /// STEP-BY-STEP (REQUIRED):
    /// 1. The user edits or pastes an API key and SettingsView forwards the raw value here.
    /// 2. We normalize the string, clear any cached result, and cancel in-flight work.
    /// 3. If the field is now empty we reset the status to .unknown immediately.
    /// 4. Otherwise we schedule a debounced refresh so the probe runs 500 ms after the user
    ///    stops typing, avoiding network spam on every keystroke.
    func registerAPIKeyChange(for provider: CloudProvider, newValue: String) {
        let trimmed = newValue.trimmingCharacters(in: .whitespacesAndNewlines)

        cache[provider] = nil
        debouncedRefreshTasks[provider]?.cancel()
        pendingChecks[provider]?.cancel()
        pendingChecks[provider] = nil

        if trimmed.isEmpty {
            statuses[provider] = .unknown
            return
        }

        if trimmed.count < minimumProbeLength {
            // UX NOTE: Short partial keys frequently appear while typing. Treat them as
            // unknown so the UI prompts the user to finish pasting without hitting the API.
            statuses[provider] = .unknown
            return
        }

        scheduleDebouncedRefresh(for: provider)
    }

    /// Same debounce + cache-invalidating logic for post-processing providers.
    func registerAPIKeyChange(for provider: PostProcessingProvider, newValue: String) {
        let trimmed = newValue.trimmingCharacters(in: .whitespacesAndNewlines)

        postProcessingCache[provider] = nil
        debouncedPostProcessingTasks[provider]?.cancel()
        pendingPostProcessingChecks[provider]?.cancel()
        pendingPostProcessingChecks[provider] = nil

        if trimmed.isEmpty {
            postProcessingStatuses[provider] = .unknown
            return
        }

        if trimmed.count < minimumProbeLength {
            postProcessingStatuses[provider] = .unknown
            return
        }

        scheduleDebouncedRefresh(for: provider)
    }

    // MARK: - Private Helpers

    private func scheduleDebouncedRefresh(for provider: CloudProvider) {
        let task = Task { [weak self] in
            try? await Task.sleep(nanoseconds: debounceDelay)
            await MainActor.run {
                guard let self else { return }
                self.refresh(provider, force: true)
            }
        }
        debouncedRefreshTasks[provider] = task
    }

    private func scheduleDebouncedRefresh(for provider: PostProcessingProvider) {
        let task = Task { [weak self] in
            try? await Task.sleep(nanoseconds: debounceDelay)
            await MainActor.run {
                guard let self else { return }
                self.refresh(provider, force: true)
            }
        }
        debouncedPostProcessingTasks[provider] = task
    }

    @discardableResult
    private func scheduleHealthCheck(for provider: CloudProvider, force: Bool) -> Task<ProviderHealth, Never> {
        let task = Task { [weak self] () -> ProviderHealth in
            guard let self else { return .unknown }
            let result = await self.performHealthCheck(for: provider, force: force)
            await MainActor.run {
                self.statuses[provider] = result
                self.cache[provider] = StatusRecord(status: result, timestamp: Date())
                self.pendingChecks[provider] = nil
            }
            return result
        }
        pendingChecks[provider] = task
        return task
    }

    @discardableResult
    private func scheduleHealthCheck(for provider: PostProcessingProvider, force: Bool) -> Task<ProviderHealth, Never> {
        let task = Task { [weak self] () -> ProviderHealth in
            guard let self else { return .unknown }
            let result = await self.performHealthCheck(for: provider, force: force)
            await MainActor.run {
                self.postProcessingStatuses[provider] = result
                self.postProcessingCache[provider] = StatusRecord(status: result, timestamp: Date())
                self.pendingPostProcessingChecks[provider] = nil
            }
            return result
        }
        pendingPostProcessingChecks[provider] = task
        return task
    }

    /// THREADING & RETRIES (REQUIRED): Network calls run off the main actor, but all mutation of
    /// dictionaries lives on @MainActor so we avoid race conditions. runHealthCheckWithRetry
    /// retries only transient connectivity failures and only when the caller explicitly forced
    /// the probe (e.g. right before a recording), keeping UI refreshes snappy.
    private func runHealthCheckWithRetry(
        force: Bool,
        operation: @escaping () async -> ProviderHealth
    ) async -> ProviderHealth {
        var attempt = 0
        var lastStatus: ProviderHealth = .unknown

        while attempt < maxRetryAttempts {
            attempt += 1
            lastStatus = await operation()

            if lastStatus == .healthy || lastStatus == .unauthorized || lastStatus == .unknown {
                return lastStatus
            }

            if lastStatus != .unreachable || !force {
                return lastStatus
            }

            if attempt < maxRetryAttempts {
                let delaySeconds = pow(2.0, Double(attempt - 1))
                try? await Task.sleep(nanoseconds: UInt64(delaySeconds * 1_000_000_000))
            }
        }

        return lastStatus
    }

    private func performHealthCheck(for provider: CloudProvider, force: Bool) async -> ProviderHealth {
        // HyperWhisper-Cloud-only providers are always available (no API key needed)
        if provider == .hyperwhisper || provider == .microsoftAzureSpeech || provider == .googleSpeech {
            return .healthy
        }

        guard let apiKeyProvider else { return .unknown }
        let rawKey = apiKeyProvider.apiKey(for: provider)
        let apiKey = rawKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !apiKey.isEmpty else { return .unknown }

        return await runHealthCheckWithRetry(force: force) {
            // RUST SHARED CORE (Wave 3 / M3-B.4): every STT provider's probe URL,
            // auth header, and 2xx/4xx verdict now comes from the core
            // (`buildHealthRequest` + `parseHealthResponse`). The Gemini/Grok
            // 400→.unauthorized special-case lives in `mapRustHealth` below; the
            // routed/always-healthy short-circuit and the missing-key gate are
            // handled above before we ever reach here.
            return await self.performRustHealthCheck(for: provider, apiKey: apiKey)
        }
    }

    // MARK: - Rust Shared Core STT Health Probe

    /// Execute a cloud STT provider's health probe via the Rust shared core.
    ///
    /// The core (`buildHealthRequest`) constructs the exact endpoint + auth the
    /// vendor expects; `RustHTTPExecutor` performs the I/O; `parseHealthResponse`
    /// grades the status. We then fold the core's `(healthy, status)` verdict back
    /// into the app's `ProviderHealth` via `mapRustHealth`, preserving the
    /// Gemini/Grok 400→.unauthorized special-case.
    ///
    /// NOTE (behavioral diff, flagged for PR): ElevenLabs now probes
    /// `GET /v1/models` (the unified core/Windows endpoint) instead of the old
    /// macOS multipart POST to `/v1/speech-to-text`. The verdict (valid key ⇒
    /// healthy) is equivalent.
    private func performRustHealthCheck(for provider: CloudProvider, apiKey: String) async -> ProviderHealth {
        let hwProvider = RustCoreMapping.hwProvider(for: provider)
        let request = buildHealthRequest(provider: hwProvider, apiKey: apiKey)

        do {
            let response = try await RustHTTPExecutor.execute(request, session: rustHealthSession)
            let verdict = parseHealthResponse(provider: hwProvider, resp: response)
            return Self.mapRustHealth(verdict, for: provider)
        } catch is CancellationError {
            // A cancelled probe should not be cached as a hard failure.
            return .unknown
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("\(provider.rawValue, privacy: .public) health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("\(provider.rawValue, privacy: .public) health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }

    /// Fold the core's `HwProviderHealth` verdict into the app's `ProviderHealth`.
    ///
    /// - `healthy == true` → `.healthy`.
    /// - 401 / 403 → `.unauthorized`.
    /// - **Gemini & Grok only**: HTTP 400 → `.unauthorized` (both vendors return
    ///   400 for an invalid key on the models endpoint; the core leaves 400 as
    ///   `healthy=false` with the raw status and defers this auth interpretation
    ///   to the platform).
    /// - any other non-2xx → `.unreachable`.
    static func mapRustHealth(_ health: HwProviderHealth, for provider: CloudProvider) -> ProviderHealth {
        if health.healthy {
            return .healthy
        }
        switch health.status {
        case .some(401), .some(403):
            return .unauthorized
        case .some(400) where provider == .gemini || provider == .grok:
            // NOTE: Gemini/xAI return 400 (Bad Request) for invalid API keys,
            // unlike OpenAI/Anthropic which return 401/403. Preserve the native
            // special-case here.
            return .unauthorized
        default:
            return .unreachable
        }
    }

    private func performHealthCheck(for provider: PostProcessingProvider, force: Bool) async -> ProviderHealth {
        if provider == .localLLM {
            let status = Self.localLLMStatus()
            postProcessingStatuses[provider] = status
            return status
        }

        guard provider.requiresHealthCheck else { return .healthy }
        guard let apiKeyProvider else { return .unknown }
        let rawKey = apiKeyProvider.postProcessingAPIKey(for: provider)
        let apiKey = rawKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !apiKey.isEmpty else { return .unknown }

        return await runHealthCheckWithRetry(force: force) {
            switch provider {
            case .hyperwhisper:
                // HyperWhisper Cloud is always available (no API key needed)
                // Already handled by requiresHealthCheck check above
                return .healthy
            case .openai:
                return await self.performOpenAIHealthCheck(apiKey: apiKey)
            case .anthropic:
                return await self.performAnthropicHealthCheck(apiKey: apiKey)
            case .gemini:
                return await self.performGeminiHealthCheck(apiKey: apiKey)
            case .groq:
                return await self.performGroqHealthCheck(apiKey: apiKey)
            case .grok:
                return await self.performXAIHealthCheck(apiKey: apiKey)
            case .cerebras:
                return await self.performCerebrasHealthCheck(apiKey: apiKey)
            case .mistral:
                return await self.mistralProvider.healthCheck(apiKey: apiKey)
            case .localLLM:
                return Self.localLLMStatus()
            }
        }
    }

    private func performAnthropicHealthCheck(apiKey: String) async -> ProviderHealth {
        guard let url = URL(string: "https://api.anthropic.com/v1/models") else { return .unknown }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue(apiKey, forHTTPHeaderField: "x-api-key")
        request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        do {
            let (_, response) = try await httpClient.send(request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("Anthropic health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            case 401, 403:
                AppLogger.network.error("Anthropic health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("Anthropic health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("Anthropic health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("Anthropic health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }

    private func performGeminiHealthCheck(apiKey: String) async -> ProviderHealth {
        guard var components = URLComponents(string: "https://generativelanguage.googleapis.com/v1beta/models") else { return .unknown }
        components.queryItems = [URLQueryItem(name: "key", value: apiKey)]
        guard let url = components.url else { return .unknown }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        do {
            let (_, response) = try await httpClient.send(request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("Gemini health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            // NOTE: Gemini returns 400 (Bad Request) for invalid API keys, unlike OpenAI/Anthropic
            // which return 401/403. We treat 400 as unauthorized for Gemini specifically.
            case 400, 401, 403:
                AppLogger.network.error("Gemini health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("Gemini health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("Gemini health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("Gemini health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }

    private func performCerebrasHealthCheck(apiKey: String) async -> ProviderHealth {
        guard let url = URL(string: "https://api.cerebras.ai/v1/models") else { return .unknown }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        do {
            let (_, response) = try await httpClient.send(request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("Cerebras health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            case 401, 403:
                AppLogger.network.error("Cerebras health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("Cerebras health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("Cerebras health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("Cerebras health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }

    private func performXAIHealthCheck(apiKey: String) async -> ProviderHealth {
        guard let url = URL(string: "https://api.x.ai/v1/models") else { return .unknown }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        do {
            let (_, response) = try await httpClient.send(request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("Grok health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            // xAI returns 400 for some invalid API-key responses, so treat it as auth failure.
            case 400, 401, 403:
                AppLogger.network.error("Grok health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("Grok health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("Grok health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("Grok health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }

    // MARK: - OpenAI-Compatible Health Checks (delegate to sharedCloudProvider)

    private func performOpenAIHealthCheck(apiKey: String) async -> ProviderHealth {
        await sharedCloudProvider.healthCheck(apiKey: apiKey, provider: .openai)
    }

    private func performGroqHealthCheck(apiKey: String) async -> ProviderHealth {
        await sharedCloudProvider.healthCheck(apiKey: apiKey, provider: .groq)
    }

    private static func localLLMStatus() -> ProviderHealth {
        let directory = LocalModelManager.modelsDirectory
        guard let contents = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]) else {
            return .notInstalled
        }
        let hasGGUF = contents.contains { url in
            url.pathExtension.lowercased() == "gguf"
        }
        return hasGGUF ? .healthy : .notInstalled
    }
}
