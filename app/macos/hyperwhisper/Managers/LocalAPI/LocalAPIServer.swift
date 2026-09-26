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

/// What a finished token regeneration does to the server (issue #641).
/// File scope so it stays nonisolated, like `regenerationOutcome`.
enum LocalAPIRegenerationOutcome: Equatable {
    /// Listening: rewrite local-api.json only. Same socket, same port.
    case republishPortFile
    /// Nothing bound: the regeneration superseded a pending start(). Bind.
    case bind
    /// A bind is in flight: its waiter writes the port file with the new token.
    case awaitBindInFlight
}

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
    /// Monotonic id of the token operation allowed to publish `bearerToken`.
    ///
    /// Both `start()` and `regenerateBearerToken()` end in a Keychain call that
    /// can take arbitrarily long — indefinitely, behind the consent panel that
    /// is issue #655 — and both want to publish what comes back. Whoever claims
    /// LAST wins, and claims synchronously, on the click, before any await:
    /// `claimTokenOwnership()`. Every continuation re-reads this before it
    /// touches `bearerToken`, so a superseded operation publishes nothing and
    /// binds nothing.
    ///
    /// This is what makes `regenerateBearerToken()`'s synchronous
    /// `bearerToken = ""` stick. Round 1 of the review ordered the two
    /// operations on one serial chain instead, and ordering is not ownership: a
    /// Regenerate click that landed while a `start()` token read was in flight
    /// was queued BEHIND that read, so the start's continuation resumed first
    /// and re-published the very credential the user had just asked to
    /// invalidate — which `bindAndRun()` then wrote into local-api.json.
    private var tokenOwner: UInt64 = 0
    /// Identifies the operation that still owes this server a bind, so `stop()`
    /// (or a newer operation) can orphan it before it binds a socket.
    ///
    /// An identity rather than a Bool because `restart()` is `stop(); start()`:
    /// a flag would let the OLDER start see the NEWER start's flag and bind.
    /// It carries a `tokenOwner` id rather than a UUID of its own so that "who
    /// may publish the token" and "who may bind with it" are one number. Two
    /// independent identities is precisely what let a regeneration retire the
    /// token while a stale start still held the right to publish the old one.
    ///
    /// Invariant: `nil`, or equal to `tokenOwner`. `stop()` clears it without
    /// disturbing `tokenOwner`, because switching the server off does not
    /// invalidate the token the Settings pane is showing.
    private var pendingBindOwner: UInt64?
    /// Tail of the serial chain REGENERATIONS run on — and only regenerations.
    ///
    /// `LocalAPIAuth.regenerateToken()` is a Keychain delete followed by a
    /// create and a write over ONE item, and `offMainActor` explicitly offers
    /// no serialization. Two overlapping runs race: the loser's `SecItemAdd`
    /// comes back `errSecDuplicateItem`, which `loadOrCreateToken` logs and
    /// swallows — it still returns the token it generated, so the caller would
    /// publish a token the Keychain does not hold. Chaining is what makes the
    /// overlap impossible.
    ///
    /// `start()` is deliberately NOT on this chain; see `enqueueRegeneration`.
    private var regenerationWork: Task<Void, Never>?

    private init() {}

    // MARK: - Lifecycle predicate

    /// Whether the server is live, or on its way up.
    ///
    /// Before the token load moved off the main actor, `start()` assigned
    /// `server` synchronously and so `server == nil` implied "not starting".
    /// It no longer does: between the click and the bind there is now a window
    /// in which the server has no socket, is not `isRunning`, and is still
    /// very much on its way up. Every lifecycle hook that used to ask one of
    /// those two questions has to ask this one instead, or it acts on a
    /// pending start as though nothing were happening (issue #655).
    ///
    /// Pure and `static` so it can be tested by calling it rather than by
    /// scraping this file — the rule `ProductionSource` states. `nonisolated`
    /// because it touches no state at all; the caller supplies the three
    /// facts, and this decides what they mean.
    nonisolated static func serverIsLiveOrStarting(
        isRunning: Bool,
        hasServer: Bool,
        hasPendingStart: Bool
    ) -> Bool {
        isRunning || hasServer || hasPendingStart
    }

    /// `serverIsLiveOrStarting` applied to this instance's own state.
    private var isLiveOrStarting: Bool {
        Self.serverIsLiveOrStarting(
            isRunning: isRunning,
            hasServer: server != nil,
            hasPendingStart: pendingBindOwner != nil
        )
    }

    /// What a regeneration owes the server once its new token has landed.
    ///
    /// Never a stop-and-rebind: `authorized()` and `writePortFile(port:)` read
    /// `bearerToken` live, and `stop()` returns before the old socket closes,
    /// so rebinding the same port fails (issue #641). Only reached once the
    /// `pendingBindOwner == owner` guard has passed, so a `stop()` during the
    /// Keychain wait never gets here. Pure, like `serverIsLiveOrStarting`.
    nonisolated static func regenerationOutcome(
        isRunning: Bool,
        hasServer: Bool
    ) -> LocalAPIRegenerationOutcome {
        if isRunning { return .republishPortFile }
        return hasServer ? .awaitBindInFlight : .bind
    }

    // MARK: - Token ownership

    /// The next owner id after `current`.
    ///
    /// Pure and `static` so it can be tested by calling it rather than by
    /// scraping this file — the rule `ProductionSource` states, and the same
    /// shape as `serverIsLiveOrStarting`. Wrapping addition, because trapping
    /// would be a crash eighteen quintillion clicks in; the only property that
    /// matters is that the id CHANGES, so no operation already in flight can be
    /// mistaken for the one that has just claimed.
    nonisolated static func nextTokenOwner(after current: UInt64) -> UInt64 {
        current &+ 1
    }

    /// Take ownership of `bearerToken` for the operation about to run.
    ///
    /// Synchronous, and before any await, always. Whoever held ownership is
    /// superseded from this line on: their continuation may no longer publish
    /// and may no longer bind.
    private func claimTokenOwnership() -> UInt64 {
        tokenOwner = Self.nextTokenOwner(after: tokenOwner)
        return tokenOwner
    }

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

    /// Run `work` after every regeneration already queued, and become the one
    /// the next regeneration waits for.
    ///
    /// The await is a suspension, never a block: a Keychain call stuck behind
    /// the consent panel holds up the next regeneration and nothing else, which
    /// is the whole point of issue #655. Enqueuing never awaits, so calling this
    /// from inside queued work appends to the chain instead of deadlocking on it.
    ///
    /// `start()` does NOT come through here, and that is the round-2 change.
    /// Round 1 put both token operations on one chain, which cost more than it
    /// bought:
    ///
    /// - It bought nothing. `loadOrCreateToken()` writes only when the Keychain
    ///   item is ABSENT, and an absent item is the one case that raises no
    ///   consent panel and can mint no `errSecDuplicateItem`. On every other
    ///   path a start is a pure read, and a pure read corrupts nothing.
    /// - It cost recovery. A start that waits here inherits every earlier
    ///   operation's wait, so one Keychain call that never returns would hold up
    ///   every later start for the process lifetime — the user could not even
    ///   toggle the Local API off and on to try again.
    /// - And ordering a start against a regeneration was the wrong tool for the
    ///   job anyway: it decides who goes FIRST, when the question is who
    ///   publishes LAST. `tokenOwner` answers that one, synchronously, on the
    ///   click, with no wait at all.
    private func enqueueRegeneration(_ work: @escaping @Sendable @MainActor () async -> Void) {
        let previous = regenerationWork
        regenerationWork = Task { @MainActor in
            await previous?.value
            await work()
        }
    }

    /// Starts the server if it isn't already running. Idempotent.
    func start() {
        guard !isLiveOrStarting else {
            AppLogger.network.debug("LocalAPIServer.start() called while already running")
            return
        }

        lastError = nil
        // The bearer token is still set before any socket is bound — it is
        // just no longer read on the main actor. `SecItemCopyMatching` can
        // block forever behind a Keychain consent panel, and on the main
        // actor that took the whole bootstrap with it (issue #655).
        //
        // The claim is synchronous, on this line, before the read is issued.
        // Anything that claims after it — a Regenerate click, a later start —
        // takes the right to publish away from the continuation below, which
        // then returns having touched nothing.
        let owner = claimTokenOwnership()
        pendingBindOwner = owner
        // Its own task, never the regeneration chain: a start must not inherit
        // an earlier Keychain call's wait. See `enqueueRegeneration`.
        Task { [weak self] in
            let token = await LocalAPIAuth.loadOrCreateTokenOffMainActor()
            guard let self, self.tokenOwner == owner else { return }
            self.bearerToken = token
            guard self.pendingBindOwner == owner else { return }
            self.pendingBindOwner = nil
            self.bindAndRun()
        }
    }

    /// Everything from address construction to the discovery-file write.
    /// Split out of `start()` so the EADDRINUSE retry can re-enter the BIND
    /// step with the token already loaded (issue #655).
    private func bindAndRun() {
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
                    if port > 0 {
                        // A bind that succeeded retires whatever the last one
                        // failed with. `start()` clears `lastError` on the
                        // click, but the EADDRINUSE fallback re-enters
                        // bindAndRun() directly and never passes through
                        // start() again — so without this the retry's healthy
                        // "Running" row sits next to a stale "Address already
                        // in use". Clearing it here rather than at the top of
                        // bindAndRun() also settles the race with
                        // handleRunFailure(), which sets lastError from the
                        // run task and can land after the retry has begun.
                        self.lastError = nil
                    }
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
                        // Reset state then re-enter the bind step so the next
                        // bind uses port 0. Deliberately NOT start(): the token
                        // is already in hand, so a second Keychain read would be
                        // wasted — and that read is the one issue #655 is about.
                        //
                        // Not because start() would refuse. It might not: the
                        // line below clears `server`, and a stop() that landed
                        // in this failed-bind window has already cleared the
                        // rest, so `isLiveOrStarting` can be false by the time
                        // start() asks. That this retry rebinds after such a
                        // stop() is a pre-existing hole, recorded in the PR body
                        // and out of scope here — do not read this comment as a
                        // claim that the in-flight guard closes it.
                        self.server = nil
                        self.runTask?.cancel()
                        self.runTask = nil
                        self.bindAndRun()
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
        // The early return asks `isLiveOrStarting`, not `server != nil`. A
        // start() still waiting on its token has no `server` yet, so the old
        // guard returned before everything below it: the orphan never
        // happened and the pending bind landed after the user switched the
        // toggle off, and `deletePortFile()` was never reached either, so a
        // quit in that window left a stale local-api.json advertising the
        // previous launch's port and token (issue #655).
        guard isLiveOrStarting else { return }
        // AFTER the guard has read it, never before: clearing first would put
        // `isLiveOrStarting` back to false for a pending-only stop and send us
        // out of the early return again, losing deletePortFile().
        //
        // The bind duty is withdrawn here; ownership of the token is NOT.
        // Switching the server off does not invalidate the token, and the
        // Settings pane still shows it, so a read already in flight may still
        // publish what it finds — it simply has nothing left to bind.
        pendingBindOwner = nil

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

    /// Stop and start again, so dependency changes take effect.
    ///
    /// Nothing in the app calls this today: the Settings switch calls `start()`
    /// and `stop()` directly, and `regenerateBearerToken()` deliberately does
    /// not use it — it already holds the fresh token, and coming back through
    /// `start()` would read the same Keychain item a second time. Kept as the
    /// honest spelling of "stop then start" for a caller that has no token in
    /// hand; anything that does have one should call `stop()` and re-enter the
    /// bind step instead (issue #655).
    func restart() {
        stop()
        start()
    }

    /// Wipe and regenerate the bearer token, then rewrite local-api.json with
    /// it — without stopping a live server (issue #641). Used by Settings →
    /// "Regenerate token".
    func regenerateBearerToken() {
        // Same Keychain hazard as start(): a delete followed by the same
        // blocking read, here on the main actor from a Settings button. The
        // published value is refreshed on every path so the UI keeps showing
        // the latest token whether or not the server is up (issue #655).
        //
        // Claim ownership FIRST. A start whose token read is still in flight is
        // superseded by this line, so it can no longer re-publish the very
        // credential the next line retires — which is what it did while the two
        // operations were merely ordered on a queue rather than owned.
        let owner = claimTokenOwnership()
        // Retire the old credential HERE, synchronously on the click, before
        // any await. The button's own help text promises the current token is
        // invalidated immediately, and `hw_localapi::authorize` denies every
        // request when the expected token is empty. Without this line a
        // running server keeps honouring the old bearer for the whole Keychain
        // wait — indefinitely, if that wait is the consent panel — which is
        // the one case where "regenerate" has to be believed.
        bearerToken = ""
        // If anything was up, or on its way up, when the click landed then this
        // regeneration now owes it something: a running server a rewritten
        // local-api.json (see `regenerationOutcome`); a start still waiting on its own
        // token read was superseded two lines ago and will not bind itself, so
        // without this the server the user switched on a moment ago would never
        // come up at all. Recorded under THIS owner id, never the superseded one.
        pendingBindOwner = isLiveOrStarting ? owner : nil
        // Queued, not fired: two rapid clicks would otherwise run two
        // delete+read+write sequences over the same Keychain item at once.
        enqueueRegeneration { [weak self] in
            // Checked before the Keychain call, not only after it. A
            // regeneration superseded while it sat in the queue must not spend a
            // second delete-and-read on a token nobody may publish — behind the
            // consent panel that is a second panel, for nothing.
            guard let self, self.tokenOwner == owner else { return }
            let token = await LocalAPIAuth.regenerateTokenOffMainActor()
            guard self.tokenOwner == owner else { return }
            self.bearerToken = token
            guard self.pendingBindOwner == owner else { return }
            self.pendingBindOwner = nil
            switch Self.regenerationOutcome(isRunning: self.isRunning, hasServer: self.server != nil) {
            case .republishPortFile:
                self.writePortFile(port: self.listeningPort)
            case .bind:
                // The BIND step, not restart(): the fresh token is in hand, and
                // start() would read the Keychain item again (issue #655).
                self.bindAndRun()
            case .awaitBindInFlight:
                break
            }
        }
    }

    // MARK: - Sleep / wake hooks (called from AppDelegate observers)

    func handleSystemWillSleep() {
        // Not `isRunning`: a start that is still waiting on its Keychain read
        // would otherwise sail through sleep and bind a socket on the far
        // side, which is the one thing this hook exists to prevent (#655).
        guard isLiveOrStarting else { return }
        AppLogger.network.info("LocalAPI server stopping for system sleep")
        stop()
    }

    func handleSystemDidWake() {
        let enabled = UserDefaults.standard.bool(forKey: LocalAPIServerEnabledKey)
        guard enabled, !isLiveOrStarting else { return }
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
            await self?.guarded(request) { await $0.authorized(request) { await $0.bodied(request) { await $0.handleModeCreate(body: $1) } } } ?? Self.shuttingDown
        }

        await server.appendRoute("GET /modes/:id") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleModeGet(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("PATCH /modes/:id") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.bodied(request) { await $0.handleModePatch(request: request, body: $1) } } } ?? Self.shuttingDown
        }

        await server.appendRoute("DELETE /modes/:id") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.handleModeDelete(request: request) } } ?? Self.shuttingDown
        }

        await server.appendRoute("POST /transcribe") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.bodied(request) { await $0.handleTranscribe(body: $1) } } } ?? Self.shuttingDown
        }

        await server.appendRoute("POST /post-process") { [weak self] request in
            await self?.guarded(request) { await $0.authorized(request) { await $0.bodied(request) { await $0.handlePostProcess(body: $1) } } } ?? Self.shuttingDown
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
        // The `port > 0` case is inside the shared guard now, as
        // `DeniedPortUnknown`, so all three platforms inherit it (#289).
        let decision = LocalAPIOriginGuard.decision(request, port: port)
        guard localApiOriginDecisionIsAllowed(decision: decision) else {
            AppLogger.network.error("LocalAPI server: rejected request with disallowed Host/Origin (possible DNS-rebinding) · \(String(describing: decision), privacy: .public)")
            return LocalAPIResponder.response(for: localApiForbiddenOriginFailure())
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
            // The hint names this platform's discovery file, which is the one
            // part of the 401 that legitimately differs per head (#289).
            return LocalAPIResponder.response(
                for: localApiUnauthorizedFailure(
                    hint: "Send Authorization: Bearer <token>; the token lives in ~/Library/Application Support/HyperWhisper/local-api.json."
                ),
                extraHeaders: [HTTPHeader("WWW-Authenticate"): "Bearer realm=\"hyperwhisper\""]
            )
        }
        return await body(self)
    }

    /// Read the request body once, bounded at the shared cap, and hand the
    /// bytes to `body` — or answer with the refusal instead (issue #375).
    ///
    /// The third cross-cutting wrapper, and here for the same reason as the
    /// other two. The origin guard and the bearer check are applied in the route
    /// table, one line per route, so a new route that forgets one is visible in
    /// the diff next to eleven that do not. The body cap used to be applied
    /// inside four endpoint bodies instead, four copies of the same five lines,
    /// where a fifth body-reading endpoint would simply have written
    /// `try await request.bodyData` — the idiom every one of those files used
    /// before #375 — and been silently uncapped again.
    ///
    /// Two consequences worth naming, because they are the point rather than
    /// side effects:
    ///
    /// - `TranscribeEndpoint.handle`, `PostProcessEndpoint.handle` and
    ///   `ModesEndpoint.create` no longer take an `HTTPRequest` at all. They
    ///   take `Data`. They cannot read an unbounded body because they are not
    ///   handed anything that has one. `ModesEndpoint.patch` still takes the
    ///   request, for its `:id` path parameter only.
    /// - `PATCH /modes/:id` now reads its body *before* the mode-existence
    ///   check rather than after it. The check was only ordered first to keep a
    ///   view-context `Mode` from being retained across the body suspension
    ///   point, and it never retained one — it is a count fetch returning
    ///   `Bool`. The single observable change is that a PATCH carrying an
    ///   over-cap body to an id that does not exist now answers
    ///   "Request exceeds the configured limit." instead of "No mode with id";
    ///   both are HTTP 200 business failures, and refusing the oversized body
    ///   first is the more useful of the two.
    ///
    /// `LocalAPIBodyLimitTests.theOnlyBodyReadInTheLocalApiIsTheBoundedOne` is
    /// the mechanical guard that keeps this the only body read in the tree.
    private func bodied(_ request: HTTPRequest, _ body: (LocalAPIServer, Data) async -> HTTPResponse) async -> HTTPResponse {
        switch await LocalAPIBodyLimit.read(request) {
        case .body(let data):
            return await body(self, data)
        case .rejected(let response):
            return response
        }
    }

    // MARK: - Endpoint trampolines (real impls live in Endpoints/)

    private func handleHealth() async -> HTTPResponse {
        await HealthEndpoint.handle(
            port: listeningPort,
            cloudHealth: cloudHealth,
            whisperModelManager: whisperModelManager,
            parakeetModelManager: parakeetModelManager,
            nemotronModelManager: nemotronModelManager,
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

    private func handleModeCreate(body: Data) async -> HTTPResponse {
        await ModesEndpoint.create(body: body)
    }

    private func handleModeGet(request: HTTPRequest) async -> HTTPResponse {
        await ModesEndpoint.get(request: request)
    }

    private func handleModePatch(request: HTTPRequest, body: Data) async -> HTTPResponse {
        await ModesEndpoint.patch(request: request, body: body)
    }

    private func handleModeDelete(request: HTTPRequest) async -> HTTPResponse {
        await ModesEndpoint.delete(request: request)
    }

    private func handleTranscribe(body: Data) async -> HTTPResponse {
        await TranscribeEndpoint.handle(body: body, transcriptionPipeline: transcriptionPipeline)
    }

    private func handlePostProcess(body: Data) async -> HTTPResponse {
        await PostProcessEndpoint.handle(body: body, transcriptionPipeline: transcriptionPipeline)
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
