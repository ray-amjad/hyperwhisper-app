//
//  TranscribeEndpoint.swift
//  hyperwhisper
//
//  Implements `POST /transcribe`. Accepts either `mode_id` (resolve a saved
//  Mode) or `engine`+`model`+`language` (Mode-less direct invocation).
//

import Foundation
import CoreData
import FlyingFox
import Darwin

enum TranscribeEndpoint {

    @MainActor
    static func handle(request: HTTPRequest, transcriptionPipeline: TranscriptionPipeline?) async -> HTTPResponse {
        let body: Data
        do { body = try await request.bodyData } catch {
            return LocalAPIResponder.badRequest(message: "Could not read request body")
        }

        let req: TranscribeRequest
        do { req = try LocalAPIResponder.decoder.decode(TranscribeRequest.self, from: body) } catch {
            return LocalAPIResponder.badRequest(
                message: "Invalid JSON body",
                hint: "Required: file (absolute path) plus either mode_id, or engine + model."
            )
        }

        // Resolve audio source: file path OR base64 (mutually exclusive).
        let audioResolution: AudioResolution
        do {
            audioResolution = try Self.resolveAudioSource(req: req)
        } catch let err as APIInputError {
            return LocalAPIResponder.failure(code: err.code, message: err.message, hint: err.hint)
        } catch {
            return LocalAPIResponder.failure(code: .invalidRequest, message: error.localizedDescription)
        }
        let fileURL = audioResolution.url
        // Per-request transient file gets cleaned up via `defer` below.
        defer { audioResolution.cleanup() }

        guard let pipeline = transcriptionPipeline else {
            return LocalAPIResponder.failure(code: .engineUnavailable, message: "Transcription pipeline not initialized")
        }

        // Determine the Mode (saved) or build a transient one from engine/model/language.
        let resolution: ProviderResolution
        do {
            resolution = try await resolve(req: req, pipeline: pipeline)
        } catch {
            let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(error)
            return LocalAPIResponder.failure(code: code, message: message, hint: hint)
        }

        let language = effectiveLanguage(for: resolution, request: req)

        // Opt-in timestamps: parse granularities and arm the provider. Providers
        // that can't produce timestamps ignore this (default no-op).
        let granularities = TimestampGranularities(wire: req.timestamp_granularities)
        if !granularities.isEmpty {
            resolution.provider.setTimestampGranularities(granularities)
        }

        let started = Date()
        let text: String
        do {
            text = try await resolution.provider.transcribe(
                audioURL: fileURL,
                language: language,
                mode: resolution.mode,
                vocabulary: resolution.vocabulary
            )
        } catch {
            cleanupTransientMode(resolution.transientMode)
            let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(error)
            return LocalAPIResponder.failure(code: code, message: message, hint: hint)
        }
        let latencyMs = Int(Date().timeIntervalSince(started) * 1000)

        // Read timestamps produced by the run (nil unless requested AND the
        // engine could produce them → graceful omission for cloud/other engines).
        let timestamps = granularities.isEmpty ? nil : resolution.provider.lastTimestamps
        let segments = timestamps.map { ts in
            ts.segments.map { TranscribeSegment(id: $0.id, start: $0.start, end: $0.end, text: $0.text) }
        }
        let words = timestamps?.words.map { ws in
            ws.map { TranscribeWord(word: $0.word, start: $0.start, end: $0.end) }
        }

        cleanupTransientMode(resolution.transientMode)

        let response = TranscribeResponse(
            ok: true,
            text: text,
            engine: resolution.engineLabel,
            model: resolution.modelLabel,
            language: language,
            timings: TranscribeTimings(load_ms: 0, decode_ms: latencyMs),
            latency_ms: latencyMs,
            raw_text: timestamps?.rawText,
            segments: segments,
            words: words
        )
        return LocalAPIResponder.ok(response)
    }

    // MARK: - Audio source resolution

    private struct AudioResolution {
        let url: URL
        let cleanup: () -> Void
    }

    private struct StagedAudioFile {
        let fileURL: URL
        let directoryURL: URL
    }

    private struct AllowListedAudioPath {
        let resolvedURL: URL
        let rootURL: URL
        let rootIdentity: FileIdentity
    }

    private struct ValidatedAudioFilePath {
        let allowListedPath: AllowListedAudioPath
        let fileIdentity: FileIdentity
    }

    private struct FileIdentity: Equatable {
        let device: UInt64
        let inode: UInt64
    }

    private struct APIInputError: Error {
        let code: LocalAPIErrorCode
        let message: String
        let hint: String?
    }

    /// Resolve `file` or `audio_base64` into a concrete file URL on disk.
    /// When base64 is used, writes a temp file and returns a cleanup closure
    /// that deletes it after the transcription run.
    private static func resolveAudioSource(req: TranscribeRequest) throws -> AudioResolution {
        let trimmedFile = req.file?.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedBase64 = req.audio_base64?.trimmingCharacters(in: .whitespacesAndNewlines)

        let hasFile = trimmedFile?.isEmpty == false
        let hasBase64 = trimmedBase64?.isEmpty == false

        if hasFile && hasBase64 {
            throw APIInputError(
                code: .invalidRequest,
                message: "Pass either 'file' or 'audio_base64', not both",
                hint: nil
            )
        }
        if !hasFile && !hasBase64 {
            throw APIInputError(
                code: .invalidRequest,
                message: "Provide 'file' (absolute path) or 'audio_base64' + 'mime_type'",
                hint: nil
            )
        }

        if let filePath = trimmedFile, hasFile {
            let url = URL(fileURLWithPath: filePath)
            // SECURITY (issue #713): the shipping app is NOT sandboxed, so the
            // process can read any user-readable file (and Full Disk Access content
            // if granted). A Local API token holder (MCP/CLI agent) is trusted to
            // transcribe the user's recordings, not to read arbitrary files — and a
            // cloud engine would upload the raw bytes off-device. Canonicalize the
            // requested path and require it to live inside the recordings folder.
            // Reject anything outside; untrusted callers should send ad-hoc audio
            // via `audio_base64`.
            guard let allowListedPath = try? Self.resolvedURLWithinAllowedRoots(url) else {
                throw APIInputError(
                    code: .fileAccessDenied,
                    message: "File path is outside the directories this API may read",
                    hint: "Use a file inside the recordings folder, or send the audio via 'audio_base64' instead of 'file'."
                )
            }
            var isDir: ObjCBool = false
            guard FileManager.default.fileExists(atPath: allowListedPath.resolvedURL.path, isDirectory: &isDir), !isDir.boolValue else {
                throw APIInputError(
                    code: .fileNotFound,
                    message: "Audio file not found: \(filePath)",
                    hint: "Pass an absolute path the running app can read."
                )
            }
            guard FileManager.default.isReadableFile(atPath: allowListedPath.resolvedURL.path) else {
                throw APIInputError(
                    code: .fileAccessDenied,
                    message: "Cannot read \(filePath)",
                    hint: "macOS may require granting Full Disk Access to HyperWhisper."
                )
            }
            let fileIdentity: FileIdentity
            do {
                fileIdentity = try Self.fileIdentity(for: allowListedPath.resolvedURL)
            } catch {
                throw APIInputError(
                    code: .fileAccessDenied,
                    message: "Cannot read \(filePath)",
                    hint: "macOS may require granting Full Disk Access to HyperWhisper."
                )
            }
            // TOCTOU hardening (issue #713): the allow-list check above runs now,
            // but providers re-open the file by PATH much later (cloud providers do
            // `Data(contentsOf: audioURL)` only after async provider resolution +
            // network setup). An attacker who can write into the recordings folder
            // could pass a real file that validates, then swap that path for a
            // symlink to a sensitive file before the provider opens it — re-resolving
            // the path here can't help because the swap happens AFTER validation.
            // Close the window by opening the validated file through a no-symlink
            // descriptor walk from the recordings root, then staging those bytes in
            // a private per-request temp directory. Providers receive a normal file
            // path for retry compatibility, never the caller-controlled path.
            let validatedPath = ValidatedAudioFilePath(
                allowListedPath: allowListedPath,
                fileIdentity: fileIdentity
            )
            let stagedFile: StagedAudioFile
            do {
                stagedFile = try Self.stageValidatedAudioFile(
                    validatedPath,
                    fileExtension: allowListedPath.resolvedURL.pathExtension.isEmpty ? "wav" : allowListedPath.resolvedURL.pathExtension
                )
            } catch {
                throw APIInputError(
                    code: .fileAccessDenied,
                    message: "Cannot read \(filePath)",
                    hint: "macOS may require granting Full Disk Access to HyperWhisper."
                )
            }
            return AudioResolution(url: stagedFile.fileURL, cleanup: {
                try? FileManager.default.removeItem(at: stagedFile.directoryURL)
            })
        }

        // base64 path
        guard let raw = trimmedBase64, let data = Data(base64Encoded: raw, options: [.ignoreUnknownCharacters]) else {
            throw APIInputError(
                code: .audioDecodeFailed,
                message: "'audio_base64' is not valid base64",
                hint: nil
            )
        }
        let ext = Self.extensionForMime(req.mime_type)
        let stagedFile: StagedAudioFile
        do {
            stagedFile = try Self.stageAudioData(data, fileExtension: ext)
        } catch {
            throw APIInputError(
                code: .audioDecodeFailed,
                message: "Failed to write decoded audio to temp file: \(error.localizedDescription)",
                hint: nil
            )
        }
        return AudioResolution(url: stagedFile.fileURL, cleanup: {
            try? FileManager.default.removeItem(at: stagedFile.directoryURL)
        })
    }

    private static func extensionForMime(_ mime: String?) -> String {
        guard let mime = mime?.lowercased() else { return "wav" }
        switch mime {
        case "audio/wav", "audio/x-wav", "audio/wave": return "wav"
        case "audio/m4a", "audio/x-m4a", "audio/mp4": return "m4a"
        case "audio/mpeg", "audio/mp3": return "mp3"
        case "audio/flac", "audio/x-flac": return "flac"
        case "audio/ogg", "audio/x-ogg", "audio/vorbis": return "ogg"
        case "audio/webm": return "webm"
        case "audio/aac": return "aac"
        default: return "wav"
        }
    }

    private static func stageValidatedAudioFile(_ source: ValidatedAudioFilePath, fileExtension ext: String) throws -> StagedAudioFile {
        let stagedFile = try Self.makePrivateStagedAudioFile(fileExtension: ext)
        do {
            let input = try Self.openValidatedAudioFile(source)
            defer { try? input.close() }

            guard FileManager.default.createFile(
                atPath: stagedFile.fileURL.path,
                contents: nil,
                attributes: [.posixPermissions: 0o600]
            ) else {
                throw POSIXError(.EEXIST)
            }

            let output = try FileHandle(forWritingTo: stagedFile.fileURL)
            defer { try? output.close() }

            while let chunk = try input.read(upToCount: 1024 * 1024), !chunk.isEmpty {
                try output.write(contentsOf: chunk)
            }
            return stagedFile
        } catch {
            try? FileManager.default.removeItem(at: stagedFile.directoryURL)
            throw error
        }
    }

    private static func stageAudioData(_ data: Data, fileExtension ext: String) throws -> StagedAudioFile {
        let stagedFile = try Self.makePrivateStagedAudioFile(fileExtension: ext)
        do {
            try data.write(to: stagedFile.fileURL, options: .atomic)
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o600],
                ofItemAtPath: stagedFile.fileURL.path
            )
            return stagedFile
        } catch {
            try? FileManager.default.removeItem(at: stagedFile.directoryURL)
            throw error
        }
    }

    private static func makePrivateStagedAudioFile(fileExtension ext: String) throws -> StagedAudioFile {
        let directoryURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hyperwhisper-local-api-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(
            at: directoryURL,
            withIntermediateDirectories: false,
            attributes: [.posixPermissions: 0o700]
        )
        let fileURL = directoryURL
            .appendingPathComponent("audio")
            .appendingPathExtension(ext)
        return StagedAudioFile(fileURL: fileURL, directoryURL: directoryURL)
    }

    /// Open `source` by walking from the validated recordings root with `openat`
    /// and `O_NOFOLLOW`, returning a descriptor-backed handle.
    ///
    /// SECURITY (issue #713 — TOCTOU): `O_NOFOLLOW` on the final file is not
    /// enough because a writable ancestor directory could be swapped for a symlink
    /// after validation. Walking every directory component with `openat(...,
    /// O_DIRECTORY | O_NOFOLLOW)` and comparing file identities prevents root,
    /// ancestor, and final-file replacement from redirecting the provider's read
    /// outside the validated recordings root.
    private static func openValidatedAudioFile(_ source: ValidatedAudioFilePath) throws -> FileHandle {
        let rootFD = try Self.openDirectoryRefusingSymlinks(at: source.allowListedPath.rootURL)
        defer { close(rootFD) }

        guard try Self.fileIdentity(forFileDescriptor: rootFD) == source.allowListedPath.rootIdentity else {
            throw POSIXError(.ESTALE)
        }

        let components = Self.relativePathComponents(
            for: source.allowListedPath.resolvedURL,
            inside: source.allowListedPath.rootURL
        )
        guard let fileName = components.last, !fileName.isEmpty else {
            throw POSIXError(.EISDIR)
        }

        var directoryFD = rootFD
        var openedDirectoryFDs: [CInt] = []
        defer {
            for fd in openedDirectoryFDs.reversed() {
                close(fd)
            }
        }

        for component in components.dropLast() {
            guard Self.isSafeRelativePathComponent(component) else {
                throw POSIXError(.EINVAL)
            }
            let nextFD = openat(directoryFD, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard nextFD >= 0 else {
                throw Self.currentPOSIXError()
            }
            openedDirectoryFDs.append(nextFD)
            directoryFD = nextFD
        }

        guard Self.isSafeRelativePathComponent(fileName) else {
            throw POSIXError(.EINVAL)
        }
        let fd = openat(directoryFD, fileName, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else {
            throw Self.currentPOSIXError()
        }
        do {
            guard try Self.fileIdentity(forFileDescriptor: fd) == source.fileIdentity else {
                close(fd)
                throw POSIXError(.ESTALE)
            }
        } catch {
            close(fd)
            throw error
        }
        return FileHandle(fileDescriptor: fd, closeOnDealloc: true)
    }

    private static func openDirectoryRefusingSymlinks(at url: URL) throws -> CInt {
        let components = url.standardizedFileURL.pathComponents
        guard components.first == "/" else {
            throw POSIXError(.EINVAL)
        }

        let rootFD = open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC)
        guard rootFD >= 0 else {
            throw Self.currentPOSIXError()
        }

        var openedFDs: [CInt] = [rootFD]
        do {
            var directoryFD = rootFD
            for component in components.dropFirst() {
                guard Self.isSafeRelativePathComponent(component) else {
                    throw POSIXError(.EINVAL)
                }
                let nextFD = openat(directoryFD, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                guard nextFD >= 0 else {
                    throw Self.currentPOSIXError()
                }
                openedFDs.append(nextFD)
                directoryFD = nextFD
            }

            let finalFD = openedFDs.removeLast()
            for fd in openedFDs.reversed() {
                close(fd)
            }
            return finalFD
        } catch {
            for fd in openedFDs.reversed() {
                close(fd)
            }
            throw error
        }
    }

    private static func currentPOSIXError() -> POSIXError {
        POSIXError(POSIXErrorCode(rawValue: errno) ?? .EACCES)
    }

    private static func fileIdentity(for url: URL) throws -> FileIdentity {
        let attrs = try FileManager.default.attributesOfItem(atPath: url.path)
        guard let device = attrs[.systemNumber] as? NSNumber,
              let inode = attrs[.systemFileNumber] as? NSNumber else {
            throw CocoaError(.fileReadUnknown)
        }
        return FileIdentity(device: device.uint64Value, inode: inode.uint64Value)
    }

    private static func fileIdentity(forFileDescriptor fd: CInt) throws -> FileIdentity {
        var statInfo = stat()
        guard fstat(fd, &statInfo) == 0 else {
            throw Self.currentPOSIXError()
        }
        return FileIdentity(device: UInt64(statInfo.st_dev), inode: UInt64(statInfo.st_ino))
    }

    private static func relativePathComponents(for url: URL, inside root: URL) -> [String] {
        Array(url.pathComponents.dropFirst(root.pathComponents.count))
    }

    private static func isSafeRelativePathComponent(_ component: String) -> Bool {
        !component.isEmpty && component != "." && component != ".." && !component.contains("/")
    }

    // MARK: - Path allow-list (issue #713)

    /// Canonicalize `candidate` and verify it is contained within the single
    /// directory the Local API is allowed to read: the user's recordings folder.
    /// Both the candidate and the root are resolved through symlinks and
    /// standardized so that `..` traversal and symlink escapes can't smuggle a
    /// path outside the root.
    ///
    /// Returns the canonicalized (symlink-resolved, standardized) URL when the
    /// candidate is inside the allowed root, or `nil` otherwise. The returned URL
    /// is only the value the caller validates and immediately copies through a
    /// descriptor-backed read; it is never handed to the provider directly.
    private static func resolvedURLWithinAllowedRoots(_ candidate: URL) throws -> AllowListedAudioPath? {
        let resolvedCandidate = candidate.resolvingSymlinksInPath().standardizedFileURL
        for root in allowedRoots() {
            if isURL(resolvedCandidate, containedIn: root) {
                return AllowListedAudioPath(
                    resolvedURL: resolvedCandidate,
                    rootURL: root,
                    rootIdentity: try Self.fileIdentity(for: root)
                )
            }
        }
        return nil
    }

    /// The single directory the `file` parameter may reference: the user's
    /// recordings folder. Read directly from the persisted default so this stays
    /// correct even if the user moved their recordings folder (the default points
    /// at the active recordings subdirectory in every fallback tier too). The root
    /// is symlink-resolved + standardized.
    ///
    /// SECURITY (issue #713): deliberately NARROW. The app data folder
    /// (~/Library/Application Support/HyperWhisper) is NOT an allowed root — it
    /// holds the Local API bearer token (`local-api.json`), logs, and models, so a
    /// token holder must not be able to read those back through `file`. The system
    /// temp dir is also NOT a root: the `audio_base64` branch returns its own
    /// app-written temp file directly without consulting this allow-list, so temp
    /// never needs to be reachable via the `file` parameter.
    private static func allowedRoots() -> [URL] {
        let defaults = UserDefaults.standard
        let recordingsPath: String
        if let recordings = defaults.string(forKey: "recordingsFolder"), !recordings.isEmpty {
            recordingsPath = recordings
        } else {
            let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
            recordingsPath = (documents?.appendingPathComponent("hyperwhisper/recordings").path) ?? ""
        }
        guard !recordingsPath.isEmpty else { return [] }
        return [URL(fileURLWithPath: recordingsPath).resolvingSymlinksInPath().standardizedFileURL]
    }

    /// True when `url` is `root` itself or a descendant of `root`, compared on
    /// path components so a sibling like `/a/recordings-evil` can't match
    /// `/a/recordings`.
    private static func isURL(_ url: URL, containedIn root: URL) -> Bool {
        let rootComponents = root.pathComponents
        let urlComponents = url.pathComponents
        guard urlComponents.count >= rootComponents.count else { return false }
        return Array(urlComponents.prefix(rootComponents.count)) == rootComponents
    }

    // MARK: - Resolution

    /// Bundle of values produced by resolving the request inputs into a
    /// concrete provider + (optional) Mode that the provider can read.
    private struct ProviderResolution {
        let provider: TranscriptionProvider
        let mode: Mode?
        let vocabulary: [Vocabulary]
        let engineLabel: String
        let modelLabel: String
        /// Non-nil when we synthesized a Mode in the viewContext just for this
        /// request — caller MUST delete it after `provider.transcribe(...)`.
        let transientMode: Mode?
    }

    @MainActor
    private static func resolve(
        req: TranscribeRequest,
        pipeline: TranscriptionPipeline
    ) async throws -> ProviderResolution {
        let router = pipeline.providerCoordinator

        let trimmedEngine = req.engine?.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedModel = req.model?.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedLanguage = req.language?.trimmingCharacters(in: .whitespacesAndNewlines)
        let hasOverride = (trimmedEngine?.isEmpty == false)
            || (trimmedModel?.isEmpty == false)
            || (trimmedLanguage?.isEmpty == false)

        if let modeId = req.mode_id?.trimmingCharacters(in: .whitespacesAndNewlines), !modeId.isEmpty {
            guard let stored = PersistenceController.shared.fetchMode(withId: modeId) else {
                throw TranscriptionError.providerNotAvailable(provider: "mode", reason: "No mode with id '\(modeId)'")
            }

            // Pure mode_id call → use saved Mode untouched.
            if !hasOverride {
                let vocab: [Vocabulary] = []
                let provider = try await router.selectProvider(for: stored, vocabulary: vocab)
                return ProviderResolution(
                    provider: provider,
                    mode: stored,
                    vocabulary: vocab,
                    engineLabel: engineLabel(forMode: stored),
                    modelLabel: modelLabel(forMode: stored),
                    transientMode: nil
                )
            }

            // Mixed: saved mode supplies defaults, request overrides specific fields.
            let transient = makeTransientMode(baseline: stored, engine: trimmedEngine, model: trimmedModel, language: trimmedLanguage)
            let provider = try await router.selectProvider(for: transient, vocabulary: [])
            return ProviderResolution(
                provider: provider,
                mode: transient,
                vocabulary: [],
                engineLabel: engineLabel(forMode: transient),
                modelLabel: modelLabel(forMode: transient),
                transientMode: transient
            )
        }

        // engine + model path: synthesize a transient Mode so cloud providers
        // can still read `cloudTranscriptionModel` etc. off it.
        guard let engine = trimmedEngine, !engine.isEmpty else {
            throw TranscriptionError.invalidRequest
        }

        let provider = try await router.resolveProvider(
            engine: engine,
            model: req.model,
            language: req.language
        )

        let transient = makeTransientMode(baseline: nil, engine: engine, model: req.model, language: req.language)

        // Derive the labels from the synthesized mode rather than echoing the
        // raw request: when the caller omits `model`, makeTransientMode applies
        // the provider's default (e.g. groq → whisper-large-v3-turbo), and the
        // response must report the model that actually ran, not "". This also
        // matches the normalized labels the mode_id paths above already return.
        return ProviderResolution(
            provider: provider,
            mode: transient,
            vocabulary: [],
            engineLabel: engineLabel(forMode: transient),
            modelLabel: modelLabel(forMode: transient),
            transientMode: transient
        )
    }

    /// Build an unsaved Mode in the main viewContext. Seeded either from
    /// scratch (`baseline == nil`) or by copying every field off `baseline`
    /// so the request can override only the bits it cares about. The caller
    /// MUST `cleanupTransientMode(...)` once transcribe finishes (success or
    /// failure) so the object doesn't linger in the context.
    @MainActor
    private static func makeTransientMode(baseline: Mode?, engine: String?, model: String?, language: String?) -> Mode {
        let context = PersistenceController.shared.container.viewContext
        let mode = Mode(context: context)
        mode.id = UUID()
        mode.name = "__local_api_transient__"
        mode.isDefault = false
        mode.isSystemProvided = false
        mode.sortOrder = Int16.max
        mode.createdDate = Date()
        mode.modifiedDate = Date()

        if let baseline {
            mode.preset = baseline.preset ?? "hyper"
            mode.language = baseline.language ?? "auto"
            mode.model = baseline.model ?? "base"
            mode.punctuation = baseline.punctuation
            mode.capitalization = baseline.capitalization
            mode.profanityFilter = baseline.profanityFilter
            mode.customInstructions = baseline.customInstructions ?? ""
            mode.userSystemPrompt = baseline.userSystemPrompt
            mode.languageModel = baseline.languageModel
            mode.cloudProvider = baseline.cloudProvider
            mode.cloudTranscriptionModel = baseline.cloudTranscriptionModel
            mode.postProcessingMode = baseline.postProcessingMode
            mode.postProcessingProvider = baseline.postProcessingProvider
            mode.englishSpelling = baseline.englishSpelling
            mode.useStreamingTranscription = baseline.useStreamingTranscription
            mode.cloudAccuracyTier = baseline.cloudAccuracyTier
            mode.removeTrailingPeriod = baseline.removeTrailingPeriod
            mode.enableScreenOCR = baseline.enableScreenOCR
            mode.geminiCustomPrompt = baseline.geminiCustomPrompt
            mode.cloudPostProcessingModel = baseline.cloudPostProcessingModel
            mode.cloudTranscriptionDomain = baseline.cloudTranscriptionDomain
        } else {
            mode.preset = "hyper"
            mode.language = "auto"
            mode.punctuation = true
            mode.capitalization = true
            mode.profanityFilter = false
            mode.customInstructions = ""
            mode.postProcessingMode = 0
        }

        if let language, !language.isEmpty {
            mode.language = language
        }

        if let engine, !engine.isEmpty {
            applyEngineModel(to: mode, engine: engine, model: model)
        } else if let model, !model.isEmpty {
            // Engine implied by baseline; just patch the model field.
            let baseModel = (mode.model ?? "").lowercased()
            if baseModel == "cloud" {
                mode.cloudTranscriptionModel = model
            } else {
                mode.model = model
            }
        }
        return mode
    }

    /// Encode an engine + model override pair onto a Mode's `model` /
    /// `cloudProvider` / `cloudTranscriptionModel` fields.
    @MainActor
    private static func applyEngineModel(to mode: Mode, engine: String, model: String?) {
        let normalizedEngine = engine.lowercased()
        let cloudType: CloudProvider?
        if normalizedEngine == "cloud" {
            cloudType = .hyperwhisper
        } else {
            cloudType = CloudProvider(rawValue: normalizedEngine)
        }

        if let cloudType {
            // Capture BEFORE overwriting cloudProvider below — to decide whether
            // the inherited model is foreign we need the provider it came from.
            let priorProvider = mode.cloudProvider
            let priorModel = mode.cloudTranscriptionModel

            mode.model = "cloud"
            mode.cloudProvider = cloudType.rawValue
            if let m = model, !m.isEmpty {
                mode.cloudTranscriptionModel = m
            } else {
                // No explicit model. Keep the inherited one only when it
                // legitimately belongs to THIS provider; otherwise fall back to
                // the provider's default. "Belongs" is true when EITHER the mode
                // was already on this provider (the caller is just (re)asserting
                // the engine — preserve their saved sub-model, including providers
                // like HyperWhisper Cloud / Grok whose sub-models aren't listed in
                // CloudTranscriptionModels.models(for:) — they live under
                // CloudAccuracyTier), OR the inherited id appears in this
                // provider's model list. This avoids two failures: a fresh
                // transient leaking the Core Data default ("whisper-1") to every
                // non-OpenAI engine, and the mixed mode_id+engine form clobbering
                // a saved HyperWhisper Cloud model with "".
                let belongsToProvider: Bool = {
                    guard let priorModel, !priorModel.isEmpty else { return false }
                    if priorProvider == cloudType.rawValue { return true }
                    return CloudTranscriptionModels.models(for: cloudType).contains { $0.id == priorModel }
                }()
                if !belongsToProvider {
                    mode.cloudTranscriptionModel = CloudTranscriptionModels.defaultModel(for: cloudType)
                }
            }
            return
        }

        switch normalizedEngine {
        case "whisperlocal", "whisper", "libwhisper":
            mode.model = model ?? "base"
        case "parakeet":
            mode.model = model ?? "parakeet-tdt-v3-multilingual"
        case "qwen3asr", "qwen3", "qwen3-asr":
            mode.model = Qwen3AsrModelManager.Constants.modelId
        case "applespeech", "apple", "apple-speech", "apple-speech-analyzer", "speech-analyzer":
            mode.model = "apple-speech-analyzer"
        default:
            if let m = model, !m.isEmpty { mode.model = m }
        }
    }

    @MainActor
    private static func cleanupTransientMode(_ mode: Mode?) {
        guard let mode else { return }
        let context = PersistenceController.shared.container.viewContext
        context.delete(mode)
        // No save() — the transient mode was never saved so nothing to flush.
        // Just rolling back any pending changes so the deletion doesn't get
        // committed accidentally by a future save() elsewhere.
        // (deleted-but-unsaved objects vanish on next refresh.)
    }

    @MainActor
    private static func effectiveLanguage(for resolution: ProviderResolution, request: TranscribeRequest) -> String? {
        let raw = (request.language ?? resolution.mode?.language)?.lowercased()
        guard let raw, raw != "auto", !raw.isEmpty else { return nil }
        return raw
    }

    @MainActor
    private static func engineLabel(forMode mode: Mode) -> String {
        let modelString = (mode.model ?? "").lowercased()
        if modelString == "cloud" || modelString.isEmpty {
            return mode.cloudProvider ?? "cloud"
        }
        if modelString.hasPrefix("parakeet-tdt-") { return "parakeet" }
        if modelString == "apple-speech-analyzer" { return "appleSpeech" }
        if modelString == Qwen3AsrModelManager.Constants.modelId { return "qwen3Asr" }
        return "whisperLocal"
    }

    @MainActor
    private static func modelLabel(forMode mode: Mode) -> String {
        let modelString = mode.model ?? ""
        if modelString.lowercased() == "cloud" {
            return mode.cloudTranscriptionModel ?? ""
        }
        return modelString
    }
}
