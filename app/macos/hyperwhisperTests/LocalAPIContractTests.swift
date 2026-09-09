//
//  LocalAPIContractTests.swift
//  hyperwhisperTests
//
//  Issue #289 observed that `find app/macos app/windows -ipath '*test*'
//  -iname '*localapi*'` was empty — only the most-drifted of the three Local
//  API implementations had any tests at all. This file is the macOS half of
//  closing that.
//
//  It tests the SEAM, not the logic. The origin guard's decision table, the
//  base64url encoder and the constant-time compare are pinned by the Rust
//  crate's own suite (`shared-core-rs/crates/hw-localapi`), where they can be
//  fuzzed. What can still go wrong on this side is the bridge: a `Codable`
//  enum that has drifted from the shared closed set, a status code the
//  responder maps wrongly, a token the store would reject.
//
//  FlyingFox types are deliberately absent. `HTTPRequest` construction would
//  couple this file to the server package for no extra coverage — the adapter
//  in `LocalAPIOriginGuard` is three header lookups, and everything it feeds
//  is exercised below through the shared functions directly.
//

import Foundation
import Testing
@testable import HyperWhisper

struct LocalAPIContractTests {

    // MARK: - The closed error-code enum

    /// The Swift `Codable` enum and the shared closed set must be the same 14
    /// codes, with the same wire strings.
    ///
    /// This is the property the whole envelope half of #289 rests on. A
    /// `Codable` enum is closed: a client sharing this decoder fails to decode
    /// the ENTIRE envelope when it meets a fifteenth code — it does not get an
    /// unknown code, it gets nothing. Linux emitted four codes outside the set
    /// and every one of those responses was undecodable here.
    @Test func theErrorCodeEnumMatchesTheSharedClosedSet() {
        let shared = localApiAllErrorCodes().map { localApiErrorCodeWireValue(code: $0) }
        #expect(shared.count == 14)

        // Every shared code decodes into the Swift enum...
        for wire in shared {
            #expect(LocalAPIErrorCode(rawValue: wire) != nil, "Swift cannot decode \(wire)")
        }
        // ...and every Swift case is in the shared set, so neither side has a
        // code the other does not.
        let swiftCodes = Set([
            LocalAPIErrorCode.modelNotInstalled, .modelNotFound, .engineUnavailable,
            .missingAPIKey, .fileNotFound, .fileAccessDenied, .fileNotAllowed,
            .audioDecodeFailed, .transcriptionFailed, .modeNotFound, .modeNameTaken,
            .invalidRequest, .rateLimited, .timeout
        ].map(\.rawValue))
        #expect(swiftCodes == Set(shared))
    }

    /// The bridge round-trips every code, so `LocalAPIResponder.response(for:)`
    /// never silently downgrades one to `.invalidRequest`.
    @Test func everySharedCodeBridgesIntoItself() {
        for code in localApiAllErrorCodes() {
            let bridged = LocalAPIErrorCode(shared: code)
            #expect(bridged.rawValue == localApiErrorCodeWireValue(code: code))
        }
    }

    /// The four codes Linux emitted outside the enum, and the one that is
    /// declared and never used. None of them may become decodable here.
    @Test func theOutOfEnumCodesStayOutOfTheEnum() {
        for wire in ["PAYLOAD_TOO_LARGE", "CANCELLED", "UNAUTHORIZED", "RECORDING_NOT_FOUND", "INTERNAL_ERROR"] {
            #expect(LocalAPIErrorCode(rawValue: wire) == nil, "\(wire) must not decode")
            #expect(localApiErrorCodeFromWireValue(value: wire) == nil, "\(wire) must not decode")
        }
    }

    // MARK: - The HTTP-200 rule

    /// A business failure is HTTP 200 whatever its code says. The three
    /// protocol cases are the only exceptions, and they are 400/401/403.
    @Test func businessFailuresAreHttpTwoHundred() {
        for code in localApiAllErrorCodes() {
            let failure = localApiBusinessFailure(code: code, message: "x", hint: nil)
            #expect(failure.httpStatus == 200)
        }
        #expect(localApiBadRequestFailure(message: "x", hint: nil).httpStatus == 400)
        #expect(localApiUnauthorizedFailure(hint: nil).httpStatus == 401)
        #expect(localApiForbiddenOriginFailure().httpStatus == 403)
    }

    /// The guard's 403 is the response this platform already sent, verbatim.
    /// These three strings are the wire on the only platform that shipped the
    /// guard before #289, so changing any of them is a contract change.
    @Test func theForbiddenResponseIsUnchanged() {
        let failure = localApiForbiddenOriginFailure()
        #expect(failure.httpStatus == 403)
        #expect(LocalAPIErrorCode(shared: failure.code) == .invalidRequest)
        #expect(failure.message == "Request rejected: Host/Origin not permitted.")
        #expect(failure.hint == "The Local API only serves loopback clients on 127.0.0.1/localhost.")
    }

    /// And the 401 keeps its message, with the hint still naming this
    /// platform's own discovery file.
    @Test func theUnauthorizedResponseIsUnchanged() {
        let hint = "Send Authorization: Bearer <token>; the token lives in ~/Library/Application Support/HyperWhisper/local-api.json."
        let failure = localApiUnauthorizedFailure(hint: hint)
        #expect(failure.httpStatus == 401)
        #expect(LocalAPIErrorCode(shared: failure.code) == .invalidRequest)
        #expect(failure.message == "Missing or invalid bearer token")
        #expect(failure.hint == hint)
    }

    // MARK: - The request-size caps (issue #375)

    /// The two caps are one pair of numbers shared by every head, not three
    /// copies that drift.
    ///
    /// They are `PortableLocalApiOptions.MaxRequestBytes` / `.MaxUploadBytes`
    /// (`PortableLocalApi.cs:18-19`) moved into Rust. A head that enforced a
    /// different ceiling would accept a body its sibling refuses, for the same
    /// caller and the same documented API — which is the failure #289 spent a
    /// whole issue undoing for the origin guard.
    ///
    /// `maxUpload <= maxRequest` is the invariant `PortableLocalApiOptions.Validate()`
    /// throws on: an upload cap above the request cap is unreachable, and every
    /// oversized upload would be reported as an oversized *request* instead.
    @Test func theSizeCapsAreTheSharedNumbers() {
        #expect(localApiMaxRequestBytes() == 52_428_800)
        #expect(localApiMaxUploadBytes() == 50_331_648)
        #expect(localApiMaxUploadBytes() <= localApiMaxRequestBytes())
    }

    /// The base64 ceiling is the .NET expansion, evaluated the way C# evaluates
    /// it — integer division before the multiply.
    ///
    /// `TranscribeEndpoint` checks the encoded string against this *before*
    /// `Data(base64Encoded:)` allocates anything. Derived from the upload cap and
    /// not the request cap, matching `PortableLocalApi.cs:244`; deriving it from
    /// the other cap would make the two heads reject different payloads.
    @Test func theBase64CeilingIsTheDotnetExpansion() {
        #expect(localApiMaxBase64LengthForUpload() == (50_331_648 + 2) / 3 * 4)
        #expect(localApiMaxBase64LengthForUpload() == 67_108_864)
    }

    /// Both size failures are HTTP 200 carrying `INVALID_REQUEST`, with the
    /// messages the .NET head already sends, verbatim.
    ///
    /// #375 asks for a 413. A 413 wants `PAYLOAD_TOO_LARGE`, which
    /// `theOutOfEnumCodesStayOutOfTheEnum` above requires to stay undecodable —
    /// and because `LocalAPIErrorCode` is a closed `Codable` enum, a client
    /// sharing this decoder would fail to decode the ENTIRE envelope, not just
    /// the code. So the rejection is a business failure, and this test is the
    /// guard on that decision. The strings are wire contract: rewording either
    /// one changes what every head says.
    @Test func theOversizeFailuresAreBusinessFailures() {
        let request = localApiRequestTooLargeFailure()
        #expect(request.httpStatus == 200)
        #expect(LocalAPIErrorCode(shared: request.code) == .invalidRequest)
        #expect(request.message == "Request exceeds the configured limit.")
        #expect(request.hint == nil)

        let upload = localApiUploadTooLargeFailure()
        #expect(upload.httpStatus == 200)
        #expect(LocalAPIErrorCode(shared: upload.code) == .invalidRequest)
        #expect(upload.message == "Audio exceeds the configured upload limit.")
        #expect(upload.hint == nil)
    }

    // MARK: - The origin guard

    /// The vectors that matter most on this side: a loopback client is
    /// dispatched, and the DNS-rebinding attack the guard exists for is not.
    ///
    /// The exhaustive table lives in `hw-localapi/src/origin.rs`; this asserts
    /// that macOS reads its answer, including on the port-0 bind race that
    /// `LocalAPIServer.guarded` used to check for itself.
    @Test func theGuardAllowsLoopbackAndDeniesRebinding() {
        func decide(host: String?, origin: String? = nil, fetchSite: String? = nil, port: UInt16 = 51671) -> Bool {
            localApiOriginDecisionIsAllowed(
                decision: localApiCheckOrigin(
                    headers: HwLocalApiOriginHeaders(host: host, origin: origin, secFetchSite: fetchSite),
                    port: port
                )
            )
        }

        #expect(decide(host: "127.0.0.1:51671"))
        #expect(decide(host: "localhost:51671"))
        #expect(decide(host: "LocalHost:51671"))
        #expect(decide(host: "127.0.0.1:51671", origin: "http://127.0.0.1:51671", fetchSite: "same-origin"))

        #expect(!decide(host: "attacker.com:51671"))
        #expect(!decide(host: "127.0.0.1:51671", origin: "http://attacker.com:51671"))
        #expect(!decide(host: "127.0.0.1:51671", fetchSite: "cross-site"))
        #expect(!decide(host: nil))
        #expect(!decide(host: "127.0.0.1:51671", port: 0))
        // The wrong port is the rebinding case a second HyperWhisper-shaped
        // listener would produce.
        #expect(!decide(host: "127.0.0.1:51672"))
    }

    // MARK: - The token

    /// A generated token has the shape the credential store and the discovery
    /// file expect: 43 base64url characters, no padding, nothing that would
    /// need escaping in a URL or a header.
    @Test func generatedTokensHaveTheStoredShape() {
        let token = LocalAPIAuth.generateToken()
        #expect(token.count == 43)
        #expect(localApiIsWellFormedToken(token: token))
        #expect(!token.contains("="))
        #expect(!token.contains("+"))
        #expect(!token.contains("/"))
        // Two calls must not agree — a constant token would be a credential in
        // name only.
        #expect(LocalAPIAuth.generateToken() != token)
    }

    /// The encoder is the one all three platforms already produced, pinned by
    /// a fixed input rather than by a shape check.
    @Test func theEncodingMatchesTheOtherPlatforms() throws {
        let entropy = Data((0..<32).map { UInt8($0) })
        let encoded = try localApiGenerateToken(entropy: entropy)
        #expect(encoded == "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")
        #expect(localApiTokenEntropyBytes() == 32)
    }

    // MARK: - The bearer check

    /// The parse and the compare, at the seam `LocalAPIAuth.authorize` feeds.
    /// The scheme is case-insensitive, the token is not, and surrounding
    /// whitespace is trimmed on both.
    @Test func theBearerCheckAcceptsOnlyTheRealToken() {
        let token = LocalAPIAuth.generateToken()

        #expect(localApiAuthorize(authorizationHeader: "Bearer \(token)", expectedToken: token))
        #expect(localApiAuthorize(authorizationHeader: "bearer \(token)", expectedToken: token))
        #expect(localApiAuthorize(authorizationHeader: "  Bearer \(token)  ", expectedToken: token))

        #expect(!localApiAuthorize(authorizationHeader: nil, expectedToken: token))
        #expect(!localApiAuthorize(authorizationHeader: "Bearer \(token)x", expectedToken: token))
        #expect(!localApiAuthorize(authorizationHeader: "Basic \(token)", expectedToken: token))
        #expect(!localApiAuthorize(authorizationHeader: token, expectedToken: token))
        #expect(!localApiAuthorize(authorizationHeader: "Bearer \(token.uppercased())", expectedToken: token))
        // A prefix of the real token never authorizes, whatever its length.
        for length in 0..<token.count {
            let prefix = String(token.prefix(length))
            #expect(!localApiAuthorize(authorizationHeader: "Bearer \(prefix)", expectedToken: token))
        }
        // And an empty stored credential authorizes nothing — the gap #289
        // closed on every platform at once.
        #expect(!localApiAuthorize(authorizationHeader: "Bearer \(token)", expectedToken: ""))
    }

    // MARK: - The deterministic text passes on /transcribe

    /// `/transcribe` declines the AI rewrite, so it is permanently on the
    /// pipeline's no-post-processing branch — and that branch's three
    /// deterministic passes belong to this route's `text` too. This head used
    /// to return `resolution.provider.transcribe(...)` unchanged, so none of
    /// them ran (issue #530).
    ///
    /// Drives the shared Rust core through the real `TranscriptionTextProcessing`
    /// shims, so this is the same filler list and the same break-command regex
    /// that dictation uses. The vocabulary pass reads the app's own store and
    /// is a no-op for this text; the live `/transcribe` check on a real Mac is
    /// what covers that third pass end to end.
    @MainActor
    @Test func transcribeAppliesTheDeterministicTextPasses() {
        let raw = "um the build is fine new line thanks"

        let processed = TranscribeEndpoint.applyDeterministicTextPasses(
            to: raw,
            language: "en",
            mode: nil,
            removeFillerWords: true,
            vocabularyProcessor: VocabularyProcessor()
        )
        #expect(!processed.hasPrefix("um "), "filler words survived with the setting on")
        #expect(processed.contains("\n"), "the dictated break command was not honoured")
        #expect(!processed.lowercased().contains("new line"), "the break command was left as words")
    }

    /// Filler removal is the ONLY pass gated on "Remove filler words". Turning
    /// that setting off must not silently disable the break commands or the
    /// vocabulary rules — the failure the portable head had in the opposite
    /// direction, where fillers were stripped whatever the setting said.
    @MainActor
    @Test func onlyFillerRemovalIsGatedOnItsSetting() {
        let raw = "um the build is fine new line thanks"

        let processed = TranscribeEndpoint.applyDeterministicTextPasses(
            to: raw,
            language: "en",
            mode: nil,
            removeFillerWords: false,
            vocabularyProcessor: VocabularyProcessor()
        )
        #expect(processed.hasPrefix("um "), "filler words were removed with the setting off")
        #expect(processed.contains("\n"), "turning filler removal off also disabled break commands")
    }

    /// The shared core strips fillers for English only — "er" and "um" are real
    /// words elsewhere (issue #278). The route passes the language the provider
    /// DETECTED rather than the requested one, so a `"language": "auto"`
    /// request still gets the pass when the audio turns out to be English; this
    /// pins the other direction, that a non-English transcript keeps its words.
    @MainActor
    @Test func fillerRemovalStaysLanguageAware() {
        let german = "um die Sache ist gut"

        let processed = TranscribeEndpoint.applyDeterministicTextPasses(
            to: german,
            language: "de",
            mode: nil,
            removeFillerWords: true,
            vocabularyProcessor: VocabularyProcessor()
        )
        #expect(processed.hasPrefix("um "), "a German transcript lost a real German word")
    }

    // MARK: - The reported cloud model

    /// `model` must name the model that ACTUALLY ran (issue #533).
    ///
    /// The cloud arm of `modelLabel(forMode:)` used to be a bare
    /// `mode.cloudTranscriptionModel ?? ""`. For HyperWhisper Cloud that field
    /// is legitimately unset — the provider dispatches on the accuracy tier,
    /// and `CloudTranscriptionModels.defaultModel(for: .hyperwhisper)` is
    /// literally `""` — so a run that really did transcribe with Scribe v2
    /// answered `model: ""`.
    ///
    /// Each case is asserted against `HyperWhisperCloudProvider.resolvedSTTModelId`,
    /// which is the SEND path's own resolver, not against a hand-copied model
    /// id. That is the property being fixed: the label is the dispatched model
    /// by construction, so a catalog change moves both at once.
    @MainActor
    @Test func transcribeReportsTheDispatchedHyperWhisperCloudModel() {
        let persistence = PersistenceController(inMemory: true)

        func label(tier: String?, storedModel: String?) -> String {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = "cloud"
            mode.cloudProvider = CloudProvider.hyperwhisper.rawValue
            mode.cloudAccuracyTier = tier
            mode.cloudTranscriptionModel = storedModel
            return TranscribeEndpoint.modelLabel(forMode: mode)
        }
        func dispatched(tier: String?, storedModel: String?) -> String {
            HyperWhisperCloudProvider.resolvedSTTModelId(
                tier: CloudAccuracyTier.fromStorageValue(tier), storedModelId: storedModel)
        }

        // The reported bug: a mode on the seeded tier with no model of its own.
        let seeded = label(tier: "elevenLabsScribeV2", storedModel: nil)
        #expect(!seeded.isEmpty, "/transcribe still reports an empty model for HyperWhisper Cloud")
        #expect(seeded == dispatched(tier: "elevenLabsScribeV2", storedModel: nil))

        // A nil tier is the transient-mode state, and resolves to the same
        // default `fromStorageValue` gives the send path.
        #expect(label(tier: nil, storedModel: nil) == dispatched(tier: nil, storedModel: nil))
        #expect(!label(tier: nil, storedModel: nil).isEmpty)

        // A legacy accuracy-tier spelling still resolves. An exact-id lookup
        // would answer "" here; `fromStorageValue` migrates it via the catalog.
        #expect(label(tier: "highest", storedModel: nil) == seeded)

        // Ids the tier cannot serve heal to the tier default in both places: a
        // live-only id is an HTTP 400 if forwarded, and `whisper-1` is the Core
        // Data default left behind by the shared BYOK field.
        for stale in ["gemini-3.5-transcribe-live", "whisper-1", ""] {
            #expect(label(tier: "elevenLabsScribeV2", storedModel: stale) == seeded,
                    "a stored model of '\(stale)' was reported instead of the dispatched one")
        }

        // A model that really is in the tier survives.
        #expect(label(tier: "elevenLabsScribeV2", storedModel: seeded) == seeded)
    }

    /// A BYOK provider sends `cloudTranscriptionModel` verbatim, so the label is
    /// that field — except when it is unset, where the provider's own default is
    /// what runs and `""` was equally wrong.
    @MainActor
    @Test func transcribeReportsTheProviderDefaultForBYOK() {
        let persistence = PersistenceController(inMemory: true)

        func label(provider: String?, storedModel: String?) -> String {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = "cloud"
            mode.cloudProvider = provider
            mode.cloudTranscriptionModel = storedModel
            return TranscribeEndpoint.modelLabel(forMode: mode)
        }

        let openAIDefault = CloudTranscriptionModels.defaultModel(for: .openai)
        #expect(label(provider: CloudProvider.openai.rawValue, storedModel: nil) == openAIDefault)
        #expect(label(provider: CloudProvider.openai.rawValue, storedModel: "gpt-4o-transcribe")
                == "gpt-4o-transcribe")

        // An unrecognised provider string can reach the column through a Local
        // API mode write or a backup restore. There is no send path to consult,
        // so the stored id stands rather than being forced onto another
        // vendor's default.
        #expect(label(provider: "not-a-provider", storedModel: "some-model") == "some-model")
        #expect(label(provider: "not-a-provider", storedModel: nil).isEmpty)

        // Azure MAI is the exception among the BYOK-NAMED providers: it routes
        // through our proxy and validates the id against its tier first, so a
        // stale value in the shared column RUNS as `mai-transcribe-2` and must
        // be reported that way rather than echoed back.
        let azure = CloudProvider.microsoftAzureSpeech.rawValue
        #expect(label(provider: azure, storedModel: "whisper-1")
                == AzureMAIProvider.routedModelId(storedModelId: "whisper-1"))
        #expect(label(provider: azure, storedModel: "whisper-1") != "whisper-1")
        #expect(label(provider: azure, storedModel: "mai-transcribe-1.5") == "mai-transcribe-1.5")
        #expect(!label(provider: azure, storedModel: nil).isEmpty)
    }

    /// `applyEngineModel`'s inferred-tier arm used to overwrite
    /// `cloudTranscriptionModel` with the tier default unconditionally, which
    /// dropped a sub-model the caller had pinned inside the SAME tier. Azure MAI
    /// has two models at different rates, so `engine: "microsoftazurespeech"`
    /// and `engine: "cloud"` gave opposite outcomes for one Mode — a different
    /// model ran, and a different price (issue #533, mirroring #528).
    @MainActor
    @Test func assertingAnEngineKeepsASubModelPinnedInTheSameTier() {
        let persistence = PersistenceController(inMemory: true)
        let mode = Mode(context: persistence.container.viewContext)
        mode.model = "cloud"
        mode.cloudProvider = CloudProvider.hyperwhisper.rawValue
        mode.cloudAccuracyTier = "azureMaiTranscribe"
        mode.cloudTranscriptionModel = "mai-transcribe-1.5"

        TranscribeEndpoint.applyEngineModel(to: mode, engine: "microsoftazurespeech", model: nil)

        #expect(mode.cloudAccuracyTier == "azureMaiTranscribe")
        #expect(mode.cloudTranscriptionModel == "mai-transcribe-1.5",
                "re-asserting the engine dropped a sub-model pinned in the same tier")
        #expect(TranscribeEndpoint.modelLabel(forMode: mode) == "mai-transcribe-1.5")

        // An explicit model still wins outright.
        TranscribeEndpoint.applyEngineModel(
            to: mode, engine: "microsoftazurespeech", model: "mai-transcribe-2")
        #expect(mode.cloudTranscriptionModel == "mai-transcribe-2")
    }
}
