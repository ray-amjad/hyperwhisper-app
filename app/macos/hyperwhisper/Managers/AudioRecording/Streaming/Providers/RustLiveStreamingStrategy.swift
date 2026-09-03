//
//  RustLiveStreamingStrategy.swift
//  hyperwhisper
//
//  THE ONE STREAMING STRATEGY. Every live provider this app speaks, spoken by
//  the shared Rust core (issue #326).
//
//  This is the macOS half of the move Windows made in issue #281: everything
//  that decides what goes on the wire — the query strings, the start frames,
//  the audio framing, the parsers, Deepgram's keepalive, OpenAI's commit gate
//  and every stop sequence — lives in `hw_net::live` and reaches this head
//  through `HwLiveSession`. What is left here is a translation layer and
//  nothing else: this app's `StreamingSessionConfig` into `HwLiveConfig`, and
//  `HwLiveEvent` / `HwLiveStopStep` back into `StreamingProviderEvent` /
//  `StreamingStopStep`.
//
//  `StreamingProviderStrategy` is unchanged, deliberately: keeping the seam is
//  what lets the 2000-line `StreamingTranscriptionClient` stay as it is.
//
//  AUDIO NEVER CROSSES THE FFI. The core answers a framing descriptor once, at
//  connect time, and `encodeAudioChunk` does the base64 and the concatenation
//  here, on bytes this process already holds; the core is told a byte COUNT and
//  never the samples.
//
//  Precedent, member for member:
//  `app/windows/HyperWhisper/Services/Streaming/LiveProtocolStreamingStrategy.cs`.
//  Unlike the C# head this needs no `IDisposable` analogue — `HwLiveSession` is
//  reference-counted under ARC and its `deinit` frees the Rust handle.
//

import Foundation
import os

// MARK: - Rust Live Streaming Strategy

/// Streaming strategy that speaks every remote provider through
/// `hw_net::live`, via the generated `HwLiveSession` binding.
///
/// WHY ONE CLASS FOR SIX PROVIDERS:
/// The six hand-rolled `*StreamingStrategy` files were a line-by-line re-write
/// of the same six wire protocols that `shared-dotnet`, Windows and Linux each
/// implemented separately. Six protocols × four heads is where the divergences
/// in issue #326 came from — Deepgram's missing `filler_words` /
/// `utterance_end_ms` / `vad_events`, its missing `detect_language=true`, and
/// four different custom-vocabulary policies. One implementation, four
/// consumers, and the wire contract is tested once in Rust.
final class RustLiveStreamingStrategy: StreamingProviderStrategy {

    // MARK: - Stored State

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "RustLiveStreaming")

    /// The shared core's name for the provider this session speaks.
    let provider: HwLiveProvider

    /// HyperWhisper Cloud only: the backend this session's socket is opened
    /// against. `#if DEBUG` builds point at staging, and a hardcoded production
    /// host here would bill a developer's key against production.
    /// `HwLiveConfig.baseUrl` exists for exactly this. Every other provider
    /// ignores it — Gemini's endpoint is Google's, not ours to move.
    private let baseURL: String?

    /// HyperWhisper Cloud only: the `cloud-stt-catalog.json` entry id the relay
    /// route is derived from. A constructor argument rather than a new field on
    /// `StreamingSessionConfig`, exactly as the deleted `HyperWhisperCloudStrategy`
    /// took it — widening the config struct would drag
    /// `RecordingTranscriptionFlow+Streaming` and the client into the diff.
    private let cloudTier: String?

    /// A monotonic millisecond clock. Injectable so the tests can drive
    /// OpenAI's 1.2 s commit interval and Deepgram's 3 s keepalive without
    /// sleeping — the core reads no clock of its own, which is the property
    /// that makes those gates testable at all.
    /// Precedent: `LiveProtocolStreamingStrategy.cs:90-118`.
    private let nowMs: () -> UInt64

    /// Serializes the `session` + `connect` PAIR: the swap on the
    /// connect/reconnect path against the reads on the capture thread and the
    /// socket loop.
    ///
    /// It is NOT held across an FFI call. `snapshot()` copies both out under
    /// the lock and every caller then invokes the Rust object unlocked, so two
    /// threads can be inside it at once. That is deliberate and safe — the Rust
    /// object holds its own (non-reentrant) `Mutex` — and the reasoning is
    /// written out at `LiveProtocolStreamingStrategy.cs:66-84`.
    private let stateLock = NSLock()
    private var session: HwLiveSession
    private var connect: HwLiveConnect?

    /// True when this object was built for a provider the shared core has no
    /// live protocol for — today, only the two on-device engines, which route
    /// to their own clients and never reach this class.
    ///
    /// It exists so that arriving here anyway is a refusal rather than a
    /// silently substituted provider. `buildWebSocketURL` reads it and answers
    /// `nil`, which `startSession` turns into `StreamingError.invalidURL`
    /// before any socket, any credential and any billable session exists.
    /// A trap would be louder still, but it would be a crash on a path a user
    /// could reach by picking an on-device engine.
    private let hasNoLiveProtocol: Bool

    // MARK: - Init

    /// - Parameters:
    ///   - provider: the app's streaming provider id, mapped onto the core's.
    ///   - baseURL: HyperWhisper Cloud's backend origin (`https://…`). The core
    ///     maps `https→wss` / `http→ws` itself. `nil` means the core's
    ///     production default, which is NOT what a DEBUG build wants — pass
    ///     `NetworkConfig.hyperwhisperCloudURL`.
    ///   - cloudTier: HyperWhisper Cloud's relay tier id.
    ///   - nowMs: monotonic milliseconds; defaults to a counter started here.
    init(
        provider: StreamingTranscriptionProvider,
        baseURL: String? = nil,
        cloudTier: String? = nil,
        nowMs: (() -> UInt64)? = nil
    ) {
        let core = RustLiveStreamingStrategy.coreProvider(provider)
        // The substitution is for the STORED PROPERTY only, so the capability
        // reads have something to answer with; `hasNoLiveProtocol` is what stops
        // it ever reaching a socket. See `coreProvider`.
        let resolved = core ?? .hyperWhisperCloud
        self.provider = resolved
        self.hasNoLiveProtocol = core == nil
        self.baseURL = baseURL
        self.cloudTier = cloudTier
        if let nowMs {
            self.nowMs = nowMs
        } else {
            // One reading per client, not per connection: a reconnect reuses
            // this object, and the core's own per-connection state is reset by
            // `buildWebSocketURL` below rather than by rewinding the clock.
            let start = DispatchTime.now().uptimeNanoseconds
            self.nowMs = {
                (DispatchTime.now().uptimeNanoseconds &- start) / 1_000_000
            }
        }
        // A placeholder session so the property is never optional. Every path
        // that matters replaces it in `buildWebSocketURL`; this one exists so
        // the parsers and the capability reads work with no socket at all,
        // which is what the test suite exercises.
        self.session = HwLiveSession(
            config: RustLiveStreamingStrategy.liveConfig(
                provider: resolved,
                config: nil,
                baseURL: baseURL,
                cloudTier: cloudTier
            )
        )
    }

    // MARK: - Provider Mapping

    /// This app's provider enum onto the shared core's, or `nil` for a provider
    /// the core has no live protocol for.
    ///
    /// The two enums differ in one name: macOS spells the vendor `.xai`, the
    /// core and the batch contract spell it `.grok` (the same trap as
    /// `LiveProtocolStreamingStrategy.cs:439-447`). macOS's raw value for
    /// Gemini is already `geminiTranscribe`, so that one is a straight rename.
    ///
    /// EXHAUSTIVE on purpose, where the C# precedent takes a `_ =>` default. A
    /// remote provider added later must fail this build rather than silently
    /// open a HyperWhisper Cloud socket carrying the user's BYOK key.
    ///
    /// AND THE TWO ON-DEVICE ENGINES ANSWER `nil` RATHER THAN CLOUD. They have
    /// no `HwLiveProvider`, no websocket protocol and no business here — they
    /// route to `LocalParakeetStreamingClient` / `LocalNemotronStreamingClient`,
    /// so neither arm is reachable today. Grouping them with
    /// `.hyperwhisperCloud` was the one shape that contradicted the paragraph
    /// above it: a later edit that routed either through this class would have
    /// opened a HyperWhisper Cloud session against the user's licence key and
    /// billed cloud credits for the transcription they chose *because* it runs
    /// offline. `nil` makes that edit fail at the first connect, loudly and with
    /// no socket, instead of quietly.
    static func coreProvider(_ provider: StreamingTranscriptionProvider) -> HwLiveProvider? {
        switch provider {
        case .deepgram: return .deepgram
        case .elevenLabs: return .elevenLabs
        case .openAI: return .openAi
        case .xai: return .grok
        case .gemini: return .geminiTranscribe
        case .hyperwhisperCloud: return .hyperWhisperCloud
        case .parakeetLocal, .nemotronLocal: return nil
        }
    }

    /// This app's per-session settings onto the shared core's live config.
    ///
    /// `config == nil` builds the credential-less shape used before the first
    /// `buildWebSocketURL`; `connect()` on it fails with
    /// `HwLiveError.MissingCredential`, which is the right answer.
    private static func liveConfig(
        provider: HwLiveProvider,
        config: StreamingSessionConfig?,
        baseURL: String?,
        cloudTier: String?
    ) -> HwLiveConfig {
        HwLiveConfig(
            provider: provider,
            apiKey: config?.apiKey,
            licenseKey: config?.licenseKey,
            deviceId: config?.deviceId,
            // Straight through. `"auto"` is the core's business
            // (`live::language_selection`), and re-normalizing here would
            // double-apply: it is how `zh-TW` got truncated to `zh` in PR
            // #320's first review round. Deepgram and HyperWhisper Cloud need
            // the region subtag; ElevenLabs, OpenAI and xAI do not, and the
            // core knows which is which.
            language: config?.language,
            vocabulary: Self.vocabularyTerms(config?.vocabulary),
            model: config?.model,
            fastFormatting: config?.fastFormatting ?? false,
            baseUrl: baseURL,
            cloudTier: cloudTier
        )
    }

    /// `StreamingSessionConfig.vocabulary` is a comma-joined string; the core
    /// takes a list.
    ///
    /// Split and trim, and NOTHING else. The cap, the case-insensitive
    /// de-duplication, the `<`/`>` strip and the 80-character truncation are
    /// `hw_net::helpers::keyword_boost_terms`' job now, and re-implementing any
    /// of them here would re-create exactly the four-way divergence this change
    /// deletes.
    private static func vocabularyTerms(_ vocabulary: String?) -> [String] {
        guard let vocabulary, !vocabulary.isEmpty else { return [] }
        return vocabulary
            .split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
    }

    // MARK: - Locked State Access

    /// The session and its connect descriptor, read together.
    ///
    /// Together matters: pairing a freshly swapped-in session with the previous
    /// connection's framing descriptor would send raw PCM to a JSON-only
    /// socket.
    private func snapshot() -> (session: HwLiveSession, connect: HwLiveConnect?) {
        stateLock.lock()
        defer { stateLock.unlock() }
        return (session, connect)
    }

    private var currentConnect: HwLiveConnect? {
        stateLock.lock()
        defer { stateLock.unlock() }
        return connect
    }

    // MARK: - StreamingProviderStrategy: Connection

    /// The connect URL, and the point at which a fresh core session is built.
    ///
    /// The client calls this once before each socket it opens — at the top of
    /// `startSession` and again on the reconnect path — so it is the natural
    /// place for the core's `connect()`, which also resets the per-connection
    /// state (OpenAI's pending-byte counter and commit clock, xAI's and
    /// OpenAI's committed transcripts, Deepgram's keepalive mark). The six
    /// deleted strategies reset that state in `startMessages` instead; this one
    /// also covers the providers that send no start message at all.
    ///
    /// THE CALLER OWES THIS METHOD A DETACHED AUDIO CALLBACK. The reset is what
    /// makes the ordering load-bearing: a capture callback that reaches
    /// `encodeAudioChunk` while this call is in flight reports its bytes to the
    /// session being installed here while sending them on the socket being
    /// replaced, and OpenAI's next commit then claims audio the new server never
    /// received. `startSession` wires the callback only after this returns, and
    /// `handleUnexpectedDisconnect` clears it before calling this — see the
    /// ordering rule written out at that call site.
    ///
    /// The caller's config is re-read rather than the constructor's being
    /// reused: the protocol says the caller supplies the config, and a strategy
    /// that ignored it would pin the language and the vocabulary at
    /// construction.
    ///
    /// `nil` on a missing credential, matching every strategy this replaces —
    /// `startSession` reads it as `StreamingError.invalidURL` and never opens a
    /// socket.
    func buildWebSocketURL(config: StreamingSessionConfig) -> URL? {
        // The refusal from `coreProvider`. Unreachable today — the two
        // on-device engines route to their own clients — and it stays a refusal
        // rather than a trap precisely because the provider behind it is one a
        // user can pick in Settings.
        guard !hasNoLiveProtocol else {
            logger.error(
                "Refusing to open a live socket: this provider transcribes on-device and has no wire protocol in the shared core"
            )
            return nil
        }

        let fresh = HwLiveSession(
            config: Self.liveConfig(
                provider: provider,
                config: config,
                baseURL: baseURL,
                cloudTier: cloudTier
            )
        )

        let descriptor: HwLiveConnect
        do {
            descriptor = try fresh.connect()
        } catch {
            // The one arm `HwLiveError` has. Nothing of the credential is
            // logged; the message names the provider only.
            logger.error(
                "Cannot open a \(self.transcriptionProviderLabel, privacy: .public) session: \(error.localizedDescription, privacy: .public)"
            )
            stateLock.lock()
            session = fresh
            connect = nil
            stateLock.unlock()
            return nil
        }

        stateLock.lock()
        session = fresh
        connect = descriptor
        stateLock.unlock()

        // The core percent-encodes query values strictly (RFC 3986 unreserved
        // only), so this parse cannot fail on anything the app can put in a
        // vocabulary term, a license key or a language tag.
        guard let url = URL(string: descriptor.url) else {
            logger.error("Core returned a connect URL that URL(string:) refused")
            return nil
        }
        return url
    }

    /// Carry the core's handshake headers onto the upgrade request, plus this
    /// platform's client-identity headers for HyperWhisper Cloud.
    ///
    /// The client-identity headers are deliberately NOT part of the core's
    /// descriptor — a shared core does not know which platform it is linked
    /// into or what the host app's version is — so this head adds them, for the
    /// one provider that has ever carried them.
    ///
    /// RETURNS `nil` WHEN THE PROVIDER AUTHENTICATES BY SUBPROTOCOL.
    /// `StreamingTranscriptionClient.makeWebSocketTask` prefers a `URLRequest`
    /// over subprotocols and never applies both, so handing it a request for
    /// Deepgram would silently drop `Sec-WebSocket-Protocol: token, <key>` and
    /// every session would fail the handshake. Windows has no such precedence —
    /// it sets headers and subprotocols on one `ClientWebSocket` — so the C#
    /// adapter offers no guidance here. Deepgram is the only provider with
    /// subprotocols and it carries no headers, so nothing is lost today.
    func buildWebSocketRequest(url: URL, config: StreamingSessionConfig) -> URLRequest? {
        guard let descriptor = currentConnect else { return nil }

        guard descriptor.subprotocols.isEmpty else {
            if !descriptor.headers.isEmpty {
                logger.error(
                    "Provider needs both handshake headers and subprotocols; the client can only carry one. Headers dropped."
                )
            }
            return nil
        }

        let isCloud = provider == .hyperWhisperCloud
        guard !descriptor.headers.isEmpty || isCloud else { return nil }

        var request = URLRequest(url: url)
        for header in descriptor.headers {
            request.setValue(header.value, forHTTPHeaderField: header.name)
        }
        if isCloud {
            HyperWhisperClientInfo.apply(to: &request)
        }
        return request
    }

    /// Deepgram only. Its API key travels as the second subprotocol because
    /// browsers cannot set handshake headers, and Deepgram documents that
    /// route.
    func webSocketSubprotocols(config: StreamingSessionConfig) -> [String]? {
        let subprotocols = currentConnect?.subprotocols ?? []
        return subprotocols.isEmpty ? nil : subprotocols
    }

    /// The frames to send the moment the socket opens: OpenAI's
    /// `session.update` and Gemini's `setup`. Empty for the four providers that
    /// configure themselves in the query string.
    func startMessages(config: StreamingSessionConfig) -> [URLSessionWebSocketTask.Message] {
        (currentConnect?.startFrames ?? []).map(Self.message(for:))
    }

    // MARK: - StreamingProviderStrategy: Audio

    /// Wrap one PCM chunk, and tell the core how much audio went out.
    ///
    /// The byte count is the only thing the core ever learns about audio, and
    /// only OpenAI's commit gate reads it — the call is free for the other
    /// five, so it is unconditional.
    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message {
        let (session, descriptor) = snapshot()
        session.noteAudio(byteCount: UInt64(pcmData.count))

        let framing: HwAudioFraming = descriptor?.framing ?? .binary
        switch framing {
        case .binary:
            return .data(pcmData)
        case let .base64Json(prefix, suffix):
            return .string(prefix + pcmData.base64EncodedString() + suffix)
        }
    }

    /// Runs before the chunk that triggered it is encoded and sent, which is
    /// the order this head has always used (`StreamingTranscriptionClient`
    /// `:514` then `:524`, and again at `:1770`/`:1773`) and the order Windows
    /// uses. It matters for one provider: OpenAI's periodic commit then covers
    /// everything appended up to the previous chunk and leaves this one for the
    /// next commit. The core cannot tell the two orders apart — it is told a
    /// byte count and a clock reading — and a commit always follows the appends
    /// it claims either way. Do not "fix" it.
    func onAudioSendOpportunity(webSocketSend: @escaping (URLSessionWebSocketTask.Message) -> Void) {
        for frame in snapshot().session.controlFrames(nowMs: nowMs()) {
            webSocketSend(Self.message(for: frame))
        }
    }

    /// `HwLiveFrame.data` is always a string because every frame the core
    /// produces is JSON text; `binary` keeps this mapping total rather than
    /// hardcoding `.string` here.
    private static func message(for frame: HwLiveFrame) -> URLSessionWebSocketTask.Message {
        if frame.binary {
            return .data(Data(frame.data.utf8))
        }
        return .string(frame.data)
    }

    // MARK: - StreamingProviderStrategy: Messages

    /// Read one text message off the socket.
    ///
    /// `nil` means "nothing happened": the client's own contract for a frame it
    /// should not act on, which every deleted strategy also returned for an
    /// unrecognised message. The core spells that `HwLiveEvent.ignore` and
    /// reaches it for anything unrecognised INCLUDING text that is not JSON, so
    /// a provider adding a frame shape can never end a recording in progress.
    func parseMessage(_ text: String) -> StreamingProviderEvent? {
        Self.event(snapshot().session.parse(text: text))
    }

    /// The core's event type onto this app's.
    ///
    /// `StreamingProviderEvent` stays as it is: `HwLiveEvent` was modelled as
    /// its superset precisely so this mapping is a rename. The one field that
    /// does not survive the trip is `error(kind:)` — macOS classifies from the
    /// wording through `liveClassifyErrorMessage`
    /// (`StreamingProviderErrorPolicy.swift:124`), and the core's own error
    /// strings carry the markers that classifier reads, so nothing downstream
    /// loses an answer. Windows drops it the same way
    /// (`LiveProtocolStreamingStrategy.cs:418-419`).
    private static func event(_ value: HwLiveEvent) -> StreamingProviderEvent? {
        switch value {
        case let .sessionStarted(sessionId):
            return .sessionStarted(sessionId: sessionId)
        case let .partialTranscript(text):
            return .partialTranscript(text: text)
        case let .finalTranscript(text):
            return .finalTranscript(text: text)
        case let .finalTranscriptAndSessionComplete(text, durationSeconds, creditsUsed):
            return .finalTranscriptAndSessionComplete(
                text: text,
                durationSeconds: durationSeconds,
                creditsUsed: creditsUsed
            )
        case let .sessionComplete(durationSeconds, creditsUsed):
            return .sessionComplete(durationSeconds: durationSeconds, creditsUsed: creditsUsed)
        case let .error(message, _):
            return .error(message: message)
        case let .warning(message):
            return .warning(message: message)
        case let .metadata(raw):
            return .metadata(raw: raw)
        case .ignore:
            return nil
        }
    }

    // MARK: - StreamingProviderStrategy: Shutdown

    /// The ordered stop path. Run the steps in order; the client does, and it
    /// must not reorder them or collapse the waits.
    func stopSequence() -> [StreamingStopStep] {
        snapshot().session.stopSequence(nowMs: nowMs()).map { (step: HwLiveStopStep) -> StreamingStopStep in
            switch step {
            case let .sendText(text):
                return .sendText(text)
            case let .wait(ms):
                return .wait(Double(ms) / 1000.0)
            case let .waitForSessionComplete(timeoutMs):
                return .waitForSessionComplete(timeout: Double(timeoutMs) / 1000.0)
            case .close:
                return .closeWebSocket
            }
        }
    }

    // MARK: - StreamingProviderStrategy: Capabilities

    /// From the shared capability table, so this head cannot drift from the two
    /// .NET heads. Needs no credential and no session, which is what lets the
    /// settings page read `supportsVocabulary` with neither.
    var transcriptionProviderLabel: String { liveProviderLabel(provider: provider) }

    var supportsVocabulary: Bool { liveSupportsVocabulary(provider: provider) }

    /// The capture graph is configured from this before a session opens, so it
    /// is a hard requirement rather than a preference: sending 16 kHz audio to
    /// OpenAI's 24 kHz endpoint produces a transcript at the wrong speed, not
    /// an error.
    var audioSampleRate: Double { Double(liveRequiredSampleRate(provider: provider)) }

    /// Deepgram only, and it comes off the core's connect descriptor rather
    /// than a second "is this Deepgram" list here.
    ///
    /// `false` before any successful `connect()`. That is the conservative
    /// answer — it makes the client wait for an explicit session-started event
    /// — and it is unreachable in the app: the client reads this from the
    /// socket's `didOpenWithProtocol` delegate callback, which only fires on a
    /// socket `buildWebSocketURL` produced a URL for.
    var sessionStartsOnWebSocketOpen: Bool { currentConnect?.sessionStartsOnOpen ?? false }

    /// `false` for Gemini alone, straight off the shared capability table.
    ///
    /// The core deliberately does NOT model the turn boundary itself: it answers
    /// `SessionComplete` for every `serverContent.generationComplete`, because
    /// that is a faithful reading of the frame (`live/gemini.rs:41-51`), and it
    /// pushes the "is the session over?" decision to the head. That decision now
    /// lives in `StreamingTranscriptionClient`, which owns the only honest
    /// answer — it is the object that knows whether the user has let go of the
    /// key yet. Same split as Windows (`StreamingTranscriptionClient.cs:648-678`)
    /// and the backend proxy's `complete` arm.
    var completeEndsSessionBeforeStop: Bool {
        liveCompleteEndsSessionBeforeStop(provider: provider)
    }
}
