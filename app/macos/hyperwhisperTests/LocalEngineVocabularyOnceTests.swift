//
//  LocalEngineVocabularyOnceTests.swift
//  hyperwhisperTests
//
//  Issue #1622 (the macOS side of Windows #1596): an on-device engine applied
//  the Vocabulary swaps twice, and the first pass matched inside words.
//  Parakeet and Nemotron ran the phonetic pass and an unanchored substring
//  pass inside `transcribe(...)`, Qwen3-ASR and Apple Speech ran the substring
//  pass, and the pipeline's `\b` pass (`applyVocabularyReplacements`) then ran
//  every replacement row again. So `fox` -> `fox terrier` gave "fox terrier
//  terrier", `art` -> `ART` gave "quARTerly", and the History row's raw
//  transcript already held the swaps.
//
//  Ray's rules (2026-10-09): no inside-word step, never twice, raw text kept.
//  The behavioural tests drive the real passes, in the order the pipeline runs
//  them, over a real provider instance and in-memory Core Data rows. The
//  wiring tests read the source, because the pipeline and the endpoint need a
//  live provider router to call.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct LocalEngineVocabularyOnceTests {

    // MARK: - Fixtures

    /// The rows are never saved: both passes only read `word` and
    /// `replacement`. Same in-memory approach as `PhoneticConformanceVectorTests`.
    /// The controller is held for the suite's lifetime so its store outlives
    /// every row made in its context.
    private let persistence = PersistenceController(inMemory: true)

    private func row(_ word: String, _ replacement: String?) -> Vocabulary {
        let row = Vocabulary(context: persistence.container.viewContext)
        row.id = UUID()
        row.word = word
        row.replacement = replacement
        return row
    }

    /// Every local engine that used to run a vocabulary pass inside
    /// `transcribe(...)`. Qwen3-ASR is macOS 15+ only, Apple Speech 26+ only.
    private var localEngines: [(name: String, provider: TranscriptionProvider)] {
        var engines: [(name: String, provider: TranscriptionProvider)] = [
            ("Parakeet", ParakeetProvider()),
            ("Nemotron", NemotronProvider()),
        ]
        if #available(macOS 15.0, *) {
            engines.append(("Qwen3-ASR", Qwen3AsrProvider()))
        }
        if #available(macOS 26.0, *) {
            engines.append(("Apple Speech", AppleSpeechAnalyzerProvider()))
        }
        return engines
    }

    /// The engine's own correction, then the pipeline's `\b` pass: exactly the
    /// two calls `TranscriptionPipeline.transcribeWithDetails` makes on the
    /// no-post-processing branch (the AI and filler steps sit between them and
    /// are no-ops for these inputs).
    private func pipelineText(
        engineText: String,
        provider: TranscriptionProvider,
        vocabulary: [Vocabulary]
    ) -> String {
        let corrected = VocabularyProcessor.applyLocalEngineVocabularyCorrection(
            to: engineText,
            provider: provider,
            vocabulary: vocabulary
        )
        return VocabularyProcessor.applyVocabularyReplacements(corrected, vocabulary: vocabulary)
    }

    // MARK: - Behaviour

    @Test func aSwapWhoseOutputContainsItsWordIsAppliedOnce() {
        let vocabulary = [row("fox", "fox terrier")]
        for engine in localEngines {
            let final = pipelineText(
                engineText: "the quick brown fox jumps",
                provider: engine.provider,
                vocabulary: vocabulary
            )
            #expect(final == "the quick brown fox terrier jumps", "\(engine.name)")
        }
    }

    @Test func aSwapNeverMatchesInsideAWord() {
        let vocabulary = [row("art", "ART")]
        for engine in localEngines {
            let final = pipelineText(
                engineText: "the quarterly report",
                provider: engine.provider,
                vocabulary: vocabulary
            )
            #expect(final == "the quarterly report", "\(engine.name)")

            // The same row still swaps the whole word.
            let whole = pipelineText(
                engineText: "modern art today",
                provider: engine.provider,
                vocabulary: vocabulary
            )
            #expect(whole == "modern ART today", "\(engine.name)")
        }
    }

    /// The engine-side step never writes a replacement value: whatever it
    /// returns still holds the engine's words, so the raw transcript and the
    /// one `\b` pass are the only places a swap can come from.
    @Test func theEngineCorrectionNeverAppliesASwap() {
        let vocabulary = [row("fox", "fox terrier"), row("art", "ART")]
        for engine in localEngines {
            let raw = "the quick brown fox jumps over the quarterly art"
            let corrected = VocabularyProcessor.applyLocalEngineVocabularyCorrection(
                to: raw,
                provider: engine.provider,
                vocabulary: vocabulary
            )
            #expect(corrected == raw, "\(engine.name)")
        }
    }

    /// The phonetic pass is kept for the Parakeet family, and only for it.
    @Test func thePhoneticPassStillRunsForTheParakeetFamilyOnly() {
        let vocabulary = [row("Whisper", nil)]
        var correcting: [String] = []
        for engine in localEngines {
            let final = pipelineText(
                engineText: "hyper wisper",
                provider: engine.provider,
                vocabulary: vocabulary
            )
            let hasPhoneticPass = engine.provider is LocalVocabularyCorrecting
            if hasPhoneticPass { correcting.append(engine.name) }
            #expect(final == (hasPhoneticPass ? "hyper Whisper" : "hyper wisper"), "\(engine.name)")
        }
        #expect(correcting == ["Parakeet", "Nemotron"])
    }

    // MARK: - Wiring

    private static let providerDirectory =
        "app/macos/hyperwhisper/Managers/Transcription/Providers/Local/"
    private static let pipelineSource =
        "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+Transcription.swift"
    private static let endpointSource =
        "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/TranscribeEndpoint.swift"

    /// No local provider runs a vocabulary pass inside `transcribe(...)` any
    /// more, so the text it returns, which the pipeline keeps as the History
    /// row's raw transcript, is the engine's own.
    @Test func noLocalProviderAppliesVocabularyInsideTranscribe() throws {
        for file in ["ParakeetProvider.swift", "NemotronProvider.swift"] {
            let body = try ProductionSource.slice(
                of: Self.providerDirectory + file,
                from: "func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary])",
                to: ": LocalVocabularyCorrecting {"
            )
            #expect(!body.contains("VocabularyProcessor.apply"), "\(file)")
        }
        for file in ["Qwen3AsrProvider.swift", "AppleSpeechAnalyzerProvider.swift"] {
            let code = try ProductionSource.code(of: Self.providerDirectory + file)
            #expect(!code.contains("VocabularyProcessor.apply"), "\(file)")
        }
        let local = ProductionSource.url(Self.providerDirectory)
        for url in try ProductionSource.swiftFiles(under: local) {
            let code = try ProductionSource.text(of: url)
            #expect(!code.contains("applySubstringVocabulary"), "\(url.lastPathComponent)")
        }
    }

    /// The pipeline keeps the engine's text as `rawText`, runs the engine's
    /// correction once after that, and feeds every branch the corrected text.
    @Test func thePipelineKeepsTheRawTextAndCorrectsOnce() throws {
        let afterTranscribe = try ProductionSource.slice(
            of: Self.pipelineSource,
            from: "text = try await provider.transcribe(",
            to: "let result = TranscriptionResult("
        )
        #expect(afterTranscribe.components(separatedBy: "applyLocalEngineVocabularyCorrection(").count - 1 == 1)
        let correction = try #require(afterTranscribe.range(of: "applyLocalEngineVocabularyCorrection("))
        let branches = try #require(afterTranscribe.range(of: "let finalText: String"))
        #expect(correction.lowerBound < branches.lowerBound)

        let finalBranches = try ProductionSource.slice(
            of: Self.pipelineSource,
            from: "let finalText: String",
            to: "markStage(\"cache_result\")"
        )
        #expect(finalBranches.contains("text: correctedText,"))
        #expect(finalBranches.contains("aiProcessedText = correctedText"))
        #expect(finalBranches.contains("removeFillerWords(correctedText,"))
        #expect(finalBranches.components(separatedBy: "applyVocabularyReplacements(").count - 1 == 3)

        let result = try ProductionSource.slice(
            of: Self.pipelineSource,
            from: "let result = TranscriptionResult(",
            to: "await MainActor.run"
        )
        #expect(result.contains("rawText: text,"))
    }

    /// The Local API's `/transcribe` runs the same two passes, once each.
    @Test func theLocalAPICorrectsOnceBeforeTheDeterministicPasses() throws {
        let handle = try ProductionSource.slice(
            of: Self.endpointSource,
            from: "text = try await resolution.provider.transcribe(",
            to: "let response = TranscribeResponse("
        )
        let correction = try #require(handle.range(of: "applyLocalEngineVocabularyCorrection("))
        let passes = try #require(handle.range(of: "Self.applyDeterministicTextPasses("))
        #expect(correction.lowerBound < passes.lowerBound)
        #expect(handle.contains("to: correctedText,"))
    }
}
