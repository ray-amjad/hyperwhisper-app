import Foundation
import Testing
@testable import HyperWhisper

@Suite("Meta Muse direct BYOK", .serialized)
struct MetaMuseBYOKTests {
    @Test("Provider and secure slot stay distinct from HyperWhisper Cloud")
    func providerAndCredentialIdentity() {
        #expect(CloudProvider.parse(" META ") == .meta)
        #expect(CloudProvider.meta.requiresAPIKey)
        #expect(CloudProvider.meta != .hyperwhisper)
        #expect(KeychainManager.APIKeyType.meta.rawValue == "MetaApiKey")
        #expect(KeychainManager.APIKeyType.meta != .geminiTranscribe)
        #expect(CloudTranscriptionModels.defaultModel(for: .meta) == MetaMuseProvider.modelID)
    }

    @MainActor
    @Test("A saved Meta key is configured but not reported as validated")
    func configuredHealthIsNotValidated() async {
        let manager = CloudProviderHealthManager()
        let status = await manager.probe(.meta, apiKey: "fixture-not-a-secret")
        #expect(status == .configured)
        #expect(status.isHealthy)
        #expect(CloudProviderHealthManager.healthRawString(status) == "configured")
        #expect(status != .healthy)
    }

    @Test("Production catalog exposes Meta BYOK after all clients are ready")
    func productionGateIsOn() {
        #expect(
            CloudSTTCatalog.shared.entry(byId: CloudAccuracyTier.metaMuse.rawValue)?
                .access?.byokEligible == true
        )
        #expect(CloudTranscriptionModels.isMetaBYOKCatalogEnabled)
        #expect(CloudTranscriptionModels.models(for: .meta).map(\.id) == [MetaMuseProvider.modelID])
    }

    @Test("Test catalog override exposes exactly the batch Muse model")
    func testOverrideExposesMuse() {
        #if DEBUG
        CloudTranscriptionModels.metaBYOKCatalogOverride = true
        defer { CloudTranscriptionModels.metaBYOKCatalogOverride = nil }
        let models = CloudTranscriptionModels.models(for: .meta)
        #expect(models.map(\.id) == [MetaMuseProvider.modelID])
        #expect(models.allSatisfy { $0.provider == .meta })
        #endif
    }

    @Test("Universal key export is opt-in and maps Meta to its isolated slot")
    func universalBackupMapping() {
        let exported = BackupManager.universalAPIKeyMap { slot in
            slot == .meta ? "fixture-not-a-secret" : nil
        }
        #expect(exported == ["meta": "fixture-not-a-secret"])
        let restored = BackupManager.universalAPIKeyAssignments(from: exported)
        #expect(restored.count == 1)
        #expect(restored.first?.provider == .meta)
        #expect(restored.first?.key == "fixture-not-a-secret")
        #expect(BackupManager.universalAPIKeyMap { _ in nil }.isEmpty)
    }

    @Test("Muse target detection separates direct Meta from stale tiers")
    func museTargetForms() {
        #expect(CloudAudioFormatRecovery.isMuseTransportTarget(
            cloudProvider: "meta", accuracyTier: nil
        ))
        #expect(CloudAudioFormatRecovery.isMuseTransportTarget(
            cloudProvider: "hyperwhisper", accuracyTier: "metaMuse"
        ))
        #expect(!CloudAudioFormatRecovery.isMuseTransportTarget(
            cloudProvider: "openai", accuracyTier: "metaMuse"
        ))
    }

    @MainActor
    @Test("Local API meta resolves direct and does not reuse the paid cloud tier")
    func localAPIRouteSeparation() {
        let persistence = PersistenceController(inMemory: true)
        let mode = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(to: mode, engine: "MeTa", model: nil)
        #expect(mode.model == "cloud")
        #expect(mode.cloudProvider == CloudProvider.meta.rawValue)
        #expect(mode.cloudAccuracyTier == nil)
        #expect(mode.cloudTranscriptionModel == MetaMuseProvider.modelID)
    }

    @Test("Canonical Muse WAV inspector enforces sample rate and duration")
    func wavValidation() throws {
        let valid = temporaryWAV(sampleRate: 16_000, seconds: 1)
        defer { try? FileManager.default.removeItem(at: valid) }
        let metadata = try MetaMuseWAVInspector.inspect(url: valid)
        #expect(metadata.sampleRate == 16_000)
        #expect(metadata.channels == 1)
        #expect(metadata.bitsPerSample == 16)
        #expect(metadata.durationSeconds == 1)
        try MetaMuseWAVInspector.validateForUpload(url: valid)

        let invalid = temporaryWAV(sampleRate: 48_000, seconds: 1)
        defer { try? FileManager.default.removeItem(at: invalid) }
        #expect(throws: TranscriptionError.self) {
            _ = try MetaMuseWAVInspector.inspect(url: invalid)
        }
    }

    @Test("Final Muse WAV limits accept the boundary and reject one unit over")
    func wavUploadBoundaries() throws {
        let tenMinutes = temporaryWAV(sampleRate: 16_000, seconds: 600)
        defer { try? FileManager.default.removeItem(at: tenMinutes) }
        try MetaMuseWAVInspector.validateForUpload(url: tenMinutes)

        let tooLong = temporaryWAV(sampleRate: 16_000, seconds: 601)
        defer { try? FileManager.default.removeItem(at: tooLong) }
        #expect(throws: TranscriptionError.self) {
            try MetaMuseWAVInspector.validateForUpload(url: tooLong)
        }

        let exactSize = temporaryWAV(sampleRate: 16_000, seconds: 1)
        defer { try? FileManager.default.removeItem(at: exactSize) }
        let exactSizeHandle = try FileHandle(forWritingTo: exactSize)
        try exactSizeHandle.truncate(atOffset: UInt64(MetaMuseProvider.maxUploadBytes))
        try exactSizeHandle.close()
        try MetaMuseWAVInspector.validateForUpload(url: exactSize)

        let tooLarge = temporaryWAV(sampleRate: 16_000, seconds: 1)
        defer { try? FileManager.default.removeItem(at: tooLarge) }
        let tooLargeHandle = try FileHandle(forWritingTo: tooLarge)
        try tooLargeHandle.truncate(
            atOffset: UInt64(MetaMuseProvider.maxUploadBytes + 1)
        )
        try tooLargeHandle.close()
        #expect(throws: TranscriptionError.self) {
            try MetaMuseWAVInspector.validateForUpload(url: tooLarge)
        }
    }

    @Test("WAV inspection streams past metadata larger than 1 MiB")
    func wavInspectionStreamsLargeMetadata() throws {
        let junkBytes = 1024 * 1024 + 2
        let audioBytes: UInt32 = 32_000
        var data = Data()
        data.append(contentsOf: Array("RIFF".utf8))
        appendLE(UInt32(36 + 8 + junkBytes) + audioBytes, to: &data)
        data.append(contentsOf: Array("WAVEfmt ".utf8))
        appendLE(UInt32(16), to: &data)
        appendLE(UInt16(1), to: &data)
        appendLE(UInt16(1), to: &data)
        appendLE(UInt32(16_000), to: &data)
        appendLE(UInt32(32_000), to: &data)
        appendLE(UInt16(2), to: &data)
        appendLE(UInt16(16), to: &data)
        data.append(contentsOf: Array("JUNK".utf8))
        appendLE(UInt32(junkBytes), to: &data)
        data.append(Data(repeating: 0, count: junkBytes))
        data.append(contentsOf: Array("data".utf8))
        appendLE(audioBytes, to: &data)
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("meta-muse-large-metadata-\(UUID().uuidString).wav")
        try data.write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        let handle = try FileHandle(forWritingTo: url)
        try handle.truncate(atOffset: UInt64(data.count) + UInt64(audioBytes))
        try handle.close()

        let metadata = try MetaMuseWAVInspector.inspect(url: url)
        #expect(metadata.durationSeconds == 1)
    }

    @Test("Provider executes the shared Meta multipart request and parser")
    func providerUsesSharedWireContract() async throws {
        let wav = temporaryWAV(sampleRate: 16_000, seconds: 1)
        defer { try? FileManager.default.removeItem(at: wav) }
        let provider = MetaMuseProvider(execute: { request, _ in
            #expect(request.url == "https://api.meta.ai/v1/asr/transcribe")
            #expect(request.headers.contains { header in
                header.name.caseInsensitiveCompare("Authorization") == .orderedSame
                    && header.value.hasPrefix("Bearer ")
            })
            guard case .multipart(_, let parts) = request.body else {
                Issue.record("Meta request body must be multipart")
                return HttpResponse(status: 500, headers: [], body: Data())
            }
            #expect(parts.contains { part in
                if case .fileRef(let field, _, let mime, let filename) = part {
                    return field == "audio" && mime == "audio/wav" && filename == "audio.wav"
                }
                return false
            })
            #expect(parts.contains { part in
                if case .inlineFile(let field, let filename, let mime, _) = part {
                    return field == "request" && filename == "request.json"
                        && mime == "application/json"
                }
                return false
            })
            return HttpResponse(
                status: 200,
                headers: [],
                body: Data(#"{"transcript":" shared parser ","audioDurationMs":1}"#.utf8)
            )
        }, isOnline: { true })
        provider.configure(apiKey: "fixture-not-a-secret")
        let text = try await provider.transcribe(
            audioURL: wav,
            language: "en-US",
            mode: nil,
            vocabulary: []
        )
        #expect(text == "shared parser")
    }

    @Test("Provider rejects a missing key before transport")
    func providerRejectsMissingKey() async {
        let wav = temporaryWAV(sampleRate: 16_000, seconds: 1)
        defer { try? FileManager.default.removeItem(at: wav) }
        let provider = MetaMuseProvider(execute: { _, _ in
            Issue.record("Transport must not run without a Meta key")
            return HttpResponse(status: 500, headers: [], body: Data())
        }, isOnline: { true })
        do {
            _ = try await provider.transcribe(
                audioURL: wav,
                language: nil,
                mode: nil,
                vocabulary: []
            )
            Issue.record("Expected a missing-key error")
        } catch TranscriptionError.apiKeyMissing(let providerName) {
            #expect(providerName == "Meta Muse")
        } catch {
            Issue.record("Expected a missing-key error, got \(error)")
        }
    }

    @Test("Shared parser maps blank Meta output to no speech")
    func providerMapsNoSpeech() async {
        let wav = temporaryWAV(sampleRate: 16_000, seconds: 1)
        defer { try? FileManager.default.removeItem(at: wav) }
        let provider = MetaMuseProvider(execute: { _, _ in
            HttpResponse(
                status: 200,
                headers: [],
                body: Data(#"{"transcript":"   ","audioDurationMs":1}"#.utf8)
            )
        }, isOnline: { true })
        provider.configure(apiKey: "fixture-not-a-secret")
        do {
            _ = try await provider.transcribe(
                audioURL: wav,
                language: nil,
                mode: nil,
                vocabulary: []
            )
            Issue.record("Expected a no-speech error")
        } catch TranscriptionError.noSpeechDetected {
            // Expected shared-core mapping.
        } catch {
            Issue.record("Expected no speech, got \(error)")
        }
    }

    @Test("Provider preserves cancellation before transport")
    func providerPreservesCancellation() async {
        let wav = temporaryWAV(sampleRate: 16_000, seconds: 1)
        defer { try? FileManager.default.removeItem(at: wav) }
        let provider = MetaMuseProvider(execute: { _, _ in
            Issue.record("Transport must not run after cancellation")
            return HttpResponse(status: 500, headers: [], body: Data())
        }, isOnline: { true })
        provider.configure(apiKey: "fixture-not-a-secret")
        let task = Task {
            try await provider.transcribe(
                audioURL: wav,
                language: nil,
                mode: nil,
                vocabulary: []
            )
        }
        task.cancel()
        do {
            _ = try await task.value
            Issue.record("Expected cancellation")
        } catch is CancellationError {
            // Expected.
        } catch {
            Issue.record("Expected cancellation, got \(error)")
        }
    }

    @Test("Normalization cleans its temporary file on success")
    func normalizationCleanup() async throws {
        let source = URL(fileURLWithPath: "/tmp/meta-source.m4a")
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("meta-muse-normalization-test.wav")
        var removed: URL?
        let output = try await CloudAudioFormatRecovery.withMuseTransportNormalization(
            sourceURL: source,
            isCanonicalMuseWAV: { _ in false },
            reencode: { _, destination in
                #expect(destination == temporary)
            },
            makeTempURL: { temporary },
            removeItem: { removed = $0 },
            send: { url in
                #expect(url == temporary)
                return "transcript"
            }
        )
        #expect(output == "transcript")
        #expect(removed == temporary)
    }

    private func temporaryWAV(sampleRate: UInt32, seconds: UInt32) -> URL {
        let dataBytes = sampleRate * seconds * 2
        var data = Data()
        data.append(contentsOf: Array("RIFF".utf8))
        appendLE(dataBytes + 36, to: &data)
        data.append(contentsOf: Array("WAVEfmt ".utf8))
        appendLE(UInt32(16), to: &data)
        appendLE(UInt16(1), to: &data)
        appendLE(UInt16(1), to: &data)
        appendLE(sampleRate, to: &data)
        appendLE(sampleRate * 2, to: &data)
        appendLE(UInt16(2), to: &data)
        appendLE(UInt16(16), to: &data)
        data.append(contentsOf: Array("data".utf8))
        appendLE(dataBytes, to: &data)
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("meta-muse-\(UUID().uuidString).wav")
        try! data.write(to: url)
        let handle = try! FileHandle(forWritingTo: url)
        try! handle.truncate(atOffset: UInt64(44 + dataBytes))
        try! handle.close()
        return url
    }

    private func appendLE(_ value: UInt16, to data: inout Data) {
        data.append(UInt8(value & 0xff))
        data.append(UInt8((value >> 8) & 0xff))
    }

    private func appendLE(_ value: UInt32, to data: inout Data) {
        data.append(UInt8(value & 0xff))
        data.append(UInt8((value >> 8) & 0xff))
        data.append(UInt8((value >> 16) & 0xff))
        data.append(UInt8((value >> 24) & 0xff))
    }
}
