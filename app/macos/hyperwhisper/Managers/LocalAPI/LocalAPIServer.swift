//
//  LocalAPIServer.swift
//  hyperwhisper
//
//  In-app HTTP server that exposes a small set of endpoints for AI agents,
//  benchmarking, and power-user automation. Off by default; opt-in via
//  Settings → API Server. Binds 127.0.0.1 on an ephemeral port and writes
//  `~/Library/Application Support/HyperWhisper/local-api.json` so clients
//  (curl, future MCP wrapper) can discover the port.
//

import Foundation
import FlyingFox
import AppKit

/// User-defaults key controlling whether the server starts at launch /
/// stays running. Toggled by Settings → API Server.
let LocalAPIServerEnabledKey = "localAPIServerEnabled"

/// User-defaults key holding the most recent port the kernel handed us.
/// We try to re-use it on subsequent starts so curl scripts can use a
/// stable URL across launches; if the port is taken (EADDRINUSE) we fall
/// back to ephemeral binding and overwrite the preference.
let LocalAPIServerPersistedPortKey = "localAPIServerPersistedPort"

@MainActor
final class LocalAPIServer: ObservableObject {

    static let shared = LocalAPIServer()

    // MARK: - Published state

    /// Bound port — non-zero while the server is running.
    @Published private(set) var listeningPort: UInt16 = 0

    /// True once the server has bound a port and is accepting connections.
    @Published private(set) var isRunning: Bool = false

    /// Most recent start/stop error, surfaced in Settings UI.
    @Published private(set) var lastError: String?

    /// Bearer token required on every endpoint except `/health`. Surfaced
    /// to the Settings UI so the user can copy / regenerate it. Mirrored
    /// into local-api.json (chmod 600) for MCP/curl auto-discovery.
    @Published private(set) var bearerToken: String = ""

    // MARK: - Dependencies (injected by hyperwhisperApp at first window appear)

    private weak var transcriptionPipeline: TranscriptionPipeline?
    private weak var cloudHealth: CloudProviderHealthManager?
    private weak var modelLibrary: ModelLibraryManager?
    private weak var settingsManager: SettingsManager?
    private weak var whisperModelManager: WhisperModelManager?
    private weak var parakeetModelManager: ParakeetModelManager?
    private weak var qwen3AsrModelManager: Qwen3AsrModelManager?
    private weak var nemotronModelManager: NemotronModelManager?
    private weak var localModelManager: LocalModelManager?

    // MARK: - Runtime state

    private var server: HTTPServer?
    private var runTask: Task<Void, Never>?
    /// The port we *asked* the kernel for on the current start() call. Used
    /// during the fallback retry when the persisted port is taken so we know
    /// what to overwrite in UserDefaults.
    private var preferredPort: UInt16 = 0

    private init() {}

    // MARK: - Configuration

    /// Inject dependencies. Called once during `applicationDidFinishLaunching`
    /// / `handleMainWindowAppear` before the server can start serving traffic.
    func configure(
        transcriptionPipeline: TranscriptionPipeline,
        cloudHealth: CloudProviderHealthManager,
        modelLibrary: ModelLibraryManager,
        settingsManager: SettingsManager,
        whisperModelManager: WhisperModelManager,
        parakeetModelManager: ParakeetModelManager,
        qwen3AsrModelManager: Qwen3AsrModelManager,
        nemotronModelManager: NemotronModelManager?,
        localModelManager: LocalModelManager
    ) {
        self.transcriptionPipeline = transcriptionPipeline
        self.cloudHealth = cloudHealth
        self.modelLibrary = modelLibrary
        self.settingsManager = settingsManager
        self.whisperModelManager = whisperModelManager
        self.parakeetModelManager = parakeetModelManager
        self.qwen3AsrModelManager = qwen3AsrModelManager
        self.nemotronModelManager = nemotronModelManager
        self.localModelManager = localModelManager
    }

    // MARK: - Lifecycle

    /// Starts the server if it isn't already running. Idempotent.
    func start() {
        guard !isRunning, server == nil else {
            AppLogger.network.debug("LocalAPIServer.start() called while already running")
            return
        }

        lastError = nil
        // Load (or generate-and-store) the bearer token before any sockets are
        // bound — every non-/health request needs it.
        self.bearerToken = LocalAPIAuth.loadOrCreateToken()

        // Bind IPv4 127.0.0.1 explicitly. FlyingFox's `.loopback(port:)` is
        // IPv6 (`[::1]`) which means clients hitting `http://127.0.0.1:PORT`
        // get connection-refused — there's nothing on the IPv4 side. We
        // standardise on IPv4 so curl/jq/Python defaults Just Work.
        //
        // Prefer the previously-persisted port so scripts that hard-code
        // `localhost:39201` keep working across launches; fall back to a
        // kernel-assigned ephemeral port if the persisted one is taken.
        let preferredPort = UInt16(UserDefaults.standard.integer(forKey: LocalAPIServerPersistedPortKey))
        let address: sockaddr_in
        do {
            address = try .inet(ip4: "127.0.0.1", port: preferredPort)
        } catch {
            self.lastError = "Failed to construct loopback address: \(error.localizedDescription)"
            AppLogger.network.error("LocalAPI server: bind address error · \(error.localizedDescription, privacy: .public)")
            return
        }
        // Transcription/post-processing jobs can run much longer than the
        // FlyingFox default (15s) — a large-v3 pass on a 30s clip or a slow
        // cloud LLM round-trip routinely takes 30-90s. Allow up to 10 min
        // per request so long jobs don't return an empty body.
        let httpServer = HTTPServer(address: address, timeout: 600)
        self.server = httpServer
        self.preferredPort = preferredPort

        Task { [weak self] in
            guard let self else { return }
            await self.registerRoutes(on: httpServer)
        }

        // Run server on a detached task; FlyingFox blocks for the lifetime of run().
        runTask = Task { [weak self] in
            do {
                try await httpServer.run()
            } catch is CancellationError {
                // Normal shutdown
            } catch {
                await self?.handleRunFailure(error)
            }
        }

        // If the kernel rejected our preferred (persisted) port — typically
        // EADDRINUSE because another process grabbed it between launches —
        // FlyingFox's run task will throw quickly. Detect that case via a
        // short timeout on waitUntilListening, then retry with port 0.

        // Wait until the kernel has assigned a port, then write the discovery file.
        Task { [weak self] in
            guard let self else { return }
            do {
                try await httpServer.waitUntilListening()
                let port = await Self.extractPort(from: httpServer)
                await MainActor.run {
                    self.listeningPort = port
                    self.isRunning = port > 0
                    self.writePortFile(port: port)
                    UserDefaults.standard.set(Int(port), forKey: LocalAPIServerPersistedPortKey)
                    AppLogger.network.info("LocalAPI server listening on 127.0.0.1:\(port, privacy: .public)")
                }
            } catch {
                // Most common cause: persisted port is already taken on this
                // machine. Wipe the preference and let the next start() pick
                // an ephemeral port.
                let preferred = await MainActor.run { self.preferredPort }
                if preferred != 0 {
                    UserDefaults.standard.removeObject(forKey: LocalAPIServerPersistedPortKey)
                    AppLogger.network.info("LocalAPI server: persisted port \(preferred, privacy: .public) unavailable; clearing preference and retrying with ephemeral port")
                    await MainActor.run {
                        // Reset state then re-enter start() so the next bind
                        // uses port 0.
                        self.server = nil
                        self.runTask?.cancel()
                        self.runTask = nil
                        self.start()
                    }
                    return
                }
                await MainActor.run {
                    self.lastError = error.localizedDescription
                    AppLogger.network.error("LocalAPI server failed to start · \(error.localizedDescription, privacy: .public)")
                }
            }
        }
    }

    /// Stops the server if running. Idempotent.
    func stop() {
        guard server != nil else { return }

        Task { [server, runTask] in
            await server?.stop(timeout: 1.0)
            runTask?.cancel()
        }

        self.server = nil
        self.runTask = nil
        self.isRunning = false
        self.listeningPort = 0
        deletePortFile()
        AppLogger.network.info("LocalAPI server stopped")
    }

    /// Convenience used when the user toggles the Settings switch — restarts
    /// the server if needed so dependency changes take effect.
    func restart() {
        stop()
        start()
    }

    /// Wipe and regenerate the bearer token, then restart the server so the
    /// new token gets written into local-api.json. Used by Settings →
    /// "Regenerate token".
    func regenerateBearerToken() {
        LocalAPIAuth.regenerateToken()
        if isRunning {
            restart()
        } else {
            // Refresh the published value even when offline so the UI keeps
            // showing the latest token.
            self.bearerToken = LocalAPIAuth.loadOrCreateToken()
        }
    }

    // MARK: - Sleep / wake hooks (called from AppDelegate observers)

    func handleSystemWillSleep() {
        guard isRunning else { return }
        AppLogger.network.info("LocalAPI server stopping for system sleep")
        stop()
    }

    func handleSystemDidWake() {
        let enabled = UserDefaults.standard.bool(forKey: LocalAPIServerEnabledKey)
        guard enabled, !isRunning else { return }
        AppLogger.network.info("LocalAPI server resuming after system wake")
        start()
    }

    // MARK: - Routes

    private func registerRoutes(on server: HTTPServer) async {
        // /health intentionally skips bearer auth so liveness probes (and the
        // Settings UI status row) keep working even if the user clears their
        // token. It is still wrapped in `guarded(...)` so a DNS-rebinding web
        // page can't read the disclosed config fingerprint (issue #730).
        await server.appendRoute("GET /health") { [weak self] request in
            await self?.guarded(request) { await $0.handleHealth() } ?? Self.shuttingDown
        }

        await server.appendRoute("GET /models") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleModels(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("GET /modes") { [weak self] request in
            await self?.guarded(request) { server in await server.authorized(request) { await $0.handleModesList() } } ?? Self.shuttingDown
        }

        await server.appendRoute("POST /modes") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleModeCreate(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("GET /modes/:id") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleModeGet(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("PATCH /modes/:id") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleModePatch(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("DELETE /modes/:id") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleModeDelete(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("POST /transcribe") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleTranscribe(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("POST /post-process") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handlePostProcess(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("GET /recordings/search") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleRecordingsSearch(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("GET /recordings/:id") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleRecordingGet(request: request) } } ?? Self.shuttingDown
        }
    }

    private static let shuttingDown = LocalAPIResponder.failure(code: .engineUnavailable, message: "Server is shutting down")

    /// Drop any request whose `Host`/`Origin` doesn't name our loopback bind,
    /// or that carries cross-site fetch metadata — defeats DNS-rebinding info
    /// disclosure (issue #730). Applied to EVERY route, including the
    /// unauthenticated `/health`, before the bearer check and any dispatch.
    /// Returns HTTP 403; a browser that rebound `attacker.com → 127.0.0.1`
    /// still sends `Host: attacker.com`, so its requests never reach a handler.
    private func guarded(_ request: HTTPRequest, _ body: (LocalAPIServer) async -> HTTPResponse) async -> HTTPResponse {
        let port = await currentBoundPort()
        guard port > 0, LocalAPIOriginGuard.isAllowed(request, port: port) else {
            AppLogger.network.error("LocalAPI server: rejected request with disallowed Host/Origin (possible DNS-rebinding)")
            let envelope = APIFailureEnvelope(
                code: .invalidRequest,
                message: "Request rejected: Host/Origin not permitted.",
                hint: "The Local API only serves loopback clients on 127.0.0.1/localhost."
            )
            let data = (try? LocalAPIResponder.encoder.encode(envelope)) ?? Data()
            return HTTPResponse(
                statusCode: .forbidden,
                headers: [.contentType: "application/json; charset=utf-8"],
                body: data
            )
        }
        return await body(self)
    }

    /// `listeningPort` is published on the main actor after FlyingFox reports
    /// the socket as listening. A first request can arrive in that narrow gap,
    /// so fall back to FlyingFox's live socket address before validating Host.
    private func currentBoundPort() async -> UInt16 {
        if listeningPort > 0 {
            return listeningPort
        }
        guard let server else { return 0 }
        return await Self.extractPort(from: server)
    }

    /// Run `body` iff the request carries a valid bearer token; otherwise
    /// return the standard 401-shaped envelope. We return HTTP 401 (not 200)
    /// here because the request is *protocol-malformed* in the sense the
    /// design doc calls out — there's nothing useful for a wrapper to surface
    /// to the agent from a credential failure.
    private func authorized(_ request: HTTPRequest, _ body: (LocalAPIServer) async -> HTTPResponse) async -> HTTPResponse {
        if !LocalAPIAuth.authorize(request, expected: bearerToken) {
            let envelope = APIFailureEnvelope(
                code: .invalidRequest,
                message: "Missing or invalid bearer token",
                hint: "Send Authorization: Bearer <token>; the token lives in ~/Library/Application Support/HyperWhisper/local-api.json."
            )
            let data = (try? LocalAPIResponder.encoder.encode(envelope)) ?? Data()
            return HTTPResponse(
                statusCode: .unauthorized,
                headers: [.contentType: "application/json; charset=utf-8",
                          HTTPHeader("WWW-Authenticate"): "Bearer realm=\"hyperwhisper\""],
                body: data
            )
        }
        return await body(self)
    }

    // MARK: - Endpoint trampolines (real impls live in Endpoints/)

    private func handleHealth() async -> HTTPResponse {
        await HealthEndpoint.handle(
            port: listeningPort,
            cloudHealth: cloudHealth,
            whisperModelManager: whisperModelManager,
            parakeetModelManager: parakeetModelManager,
            qwen3AsrModelManager: qwen3AsrModelManager,
            localModelManager: localModelManager,
            settingsManager: settingsManager
        )
    }

    private func handleModels(request: HTTPRequest) async -> HTTPResponse {
        await ModelsEndpoint.handle(request: request, modelLibrary: modelLibrary)
    }

    private func handleModesList() async -> HTTPResponse {
        await ModesEndpoint.list()
    }

    private func handleModeCreate(request: HTTPRequest) async -> HTTPResponse {
        await ModesEndpoint.create(request: request)
    }

    private func handleModeGet(request: HTTPRequest) async -> HTTPResponse {
        await ModesEndpoint.get(request: request)
    }

    private func handleModePatch(request: HTTPRequest) async -> HTTPResponse {
        await ModesEndpoint.patch(request: request)
    }

    private func handleModeDelete(request: HTTPRequest) async -> HTTPResponse {
        await ModesEndpoint.delete(request: request)
    }

    private func handleTranscribe(request: HTTPRequest) async -> HTTPResponse {
        await TranscribeEndpoint.handle(request: request, transcriptionPipeline: transcriptionPipeline)
    }

    private func handlePostProcess(request: HTTPRequest) async -> HTTPResponse {
        await PostProcessEndpoint.handle(request: request, transcriptionPipeline: transcriptionPipeline)
    }

    private func handleRecordingsSearch(request: HTTPRequest) async -> HTTPResponse {
        await RecordingsEndpoint.search(request: request)
    }

    private func handleRecordingGet(request: HTTPRequest) async -> HTTPResponse {
        await RecordingsEndpoint.get(request: request)
    }

    // MARK: - Helpers

    private func handleRunFailure(_ error: Error) async {
        await MainActor.run {
            self.lastError = error.localizedDescription
            self.isRunning = false
            self.listeningPort = 0
            // Drop the broken HTTPServer + run-task references so the next
            // start() (e.g. user toggling off then on) can actually bind a
            // new socket. Without this, start() bails on `server != nil`
            // and the UI gets stuck in "Starting…" forever.
            self.server = nil
            self.runTask?.cancel()
            self.runTask = nil
            self.deletePortFile()
            AppLogger.network.error("LocalAPI server stopped with error · \(error.localizedDescription, privacy: .public)")
        }
    }

    /// FlyingFox exposes the bound socket address once `waitUntilListening`
    /// resolves. Returns 0 if the bound socket isn't an IP socket (e.g. Unix
    /// domain — shouldn't happen because we explicitly bind .loopback).
    private static func extractPort(from server: HTTPServer) async -> UInt16 {
        guard let address = await server.listeningAddress else { return 0 }
        switch address {
        case .ip4(_, let port): return port
        case .ip6(_, let port): return port
        case .unix: return 0
        }
    }

    // MARK: - Port discovery file

    static var portFileURL: URL {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return appSupport
            .appendingPathComponent("HyperWhisper", isDirectory: true)
            .appendingPathComponent("local-api.json")
    }

    private func writePortFile(port: UInt16) {
        let url = Self.portFileURL
        let dir = url.deletingLastPathComponent()
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        } catch {
            AppLogger.network.error("LocalAPI portfile: failed to create directory · \(error.localizedDescription, privacy: .public)")
            return
        }

        let payload = LocalAPIPortFile(
            port: port,
            pid: ProcessInfo.processInfo.processIdentifier,
            started_at: ISO8601DateFormatter().string(from: Date()),
            api_version: LocalAPIVersion.current,
            app_version: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0",
            token: bearerToken
        )

        let data: Data
        do {
            data = try LocalAPIResponder.encoder.encode(payload)
        } catch {
            AppLogger.network.error("LocalAPI portfile: failed to encode payload · \(error.localizedDescription, privacy: .public)")
            return
        }

        // Defend against a pre-existing *immutable* discovery file before the
        // atomic write (see clearImmutableFlag). A `uchg` stamp makes the
        // rename-into-place fail with EPERM, after which we'd keep publishing
        // the previous launch's now-dead port/token forever.
        _ = Self.clearImmutableFlag(at: url)
        do {
            try data.write(to: url, options: .atomic)
        } catch {
            AppLogger.network.error("LocalAPI portfile: failed to write · \(error.localizedDescription, privacy: .public)")
            deleteExistingPortFileIfStale(expectedPort: port, expectedToken: bearerToken)
            return
        }

        do {
            // chmod 600 - only the running user can read this file.
            try FileManager.default.setAttributes(
                [.posixPermissions: NSNumber(value: 0o600)],
                ofItemAtPath: url.path
            )
        } catch {
            AppLogger.network.warning("LocalAPI portfile: failed to restrict permissions · \(error.localizedDescription, privacy: .public)")
        }
    }

    /// Best-effort clear of the BSD user-immutable flag (`uchg` / `UF_IMMUTABLE`)
    /// on an existing file. We never set this flag ourselves, but external actors
    /// — Time Machine restores, backup/sync utilities, some security software —
    /// can stamp it onto files under Application Support. If the discovery file
    /// becomes immutable, both the atomic write and removeItem below fail, so the
    /// app would strand every client on the prior launch's dead socket.
    @discardableResult
    private static func clearImmutableFlag(at url: URL) -> Bool {
        guard FileManager.default.fileExists(atPath: url.path) else { return true }
        do {
            try FileManager.default.setAttributes(
                [.immutable: false],
                ofItemAtPath: url.path
            )
            return true
        } catch {
            AppLogger.network.error("LocalAPI portfile: failed to clear immutable flag · \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    private func deleteExistingPortFileIfStale(expectedPort: UInt16, expectedToken: String) {
        let url = Self.portFileURL
        guard Self.existingPortFileIsStale(at: url, expectedPort: expectedPort, expectedToken: expectedToken) else {
            AppLogger.network.warning("LocalAPI portfile: leaving existing discovery file after write failure because it still matches this server")
            return
        }

        deletePortFile()
        if FileManager.default.fileExists(atPath: url.path) {
            AppLogger.network.error("LocalAPI portfile: stale discovery file remains after cleanup attempt")
        }
    }

    private static func existingPortFileIsStale(at url: URL, expectedPort: UInt16, expectedToken: String) -> Bool {
        guard FileManager.default.fileExists(atPath: url.path) else { return false }

        do {
            let data = try Data(contentsOf: url)
            let existing = try LocalAPIResponder.decoder.decode(LocalAPIPortFile.self, from: data)
            return existing.port != expectedPort
                || existing.pid != ProcessInfo.processInfo.processIdentifier
                || existing.token != expectedToken
        } catch let decodingError as DecodingError {
            let errorDescription = String(describing: decodingError)
            AppLogger.network.warning("LocalAPI portfile: existing discovery file is invalid; treating it as stale · \(errorDescription, privacy: .public)")
            return true
        } catch {
            AppLogger.network.warning("LocalAPI portfile: could not inspect existing discovery file; leaving it in place · \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    private func deletePortFile() {
        let url = Self.portFileURL
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        // Clear `uchg` first — removeItem can't unlink an immutable file.
        _ = Self.clearImmutableFlag(at: url)
        do {
            try FileManager.default.removeItem(at: url)
        } catch {
            AppLogger.network.error("LocalAPI portfile: failed to delete · \(error.localizedDescription, privacy: .public)")
        }
    }
}
