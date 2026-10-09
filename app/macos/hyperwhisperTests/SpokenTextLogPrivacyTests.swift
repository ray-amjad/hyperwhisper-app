//
//  SpokenTextLogPrivacyTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1647.
//
//  The phonetic vocabulary pass logged each matched token and its replacement
//  with `privacy: .public`, so a Release build wrote the words the user said to
//  the unified log, where `log show` and the diagnostics export read them. It
//  now logs a count. Two sibling lines did the same with other user text: the
//  local-LLM rejection logged a 200-character preview of the model's rewrite,
//  and a CGEvent failure logged the transcript character being typed.
//
//  A log line cannot be observed from a unit test, so these read the source
//  (`ProductionSource`), comments stripped, and pin the shape of each line.
//

import Testing

@Suite("Spoken text stays out of the log (#1647)")
struct SpokenTextLogPrivacyTests {

    @Test func thePhoneticPassLogsACountNotTheWords() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Support/VocabularyProcessor.swift",
            from: "static func applyPhoneticVocabulary(",
            to: "func applyVocabularyReplacements(_ text: String, mode: Mode?)"
        )
        #expect(!body.contains("match.token"), "the matched token is what the user said")
        #expect(!body.contains("match.replacement"), "the replacement names the matched word")
        #expect(body.contains("result.matches.count"))
        #expect(body.contains("token(s) corrected"))
        // The entry-count line stays.
        #expect(body.contains("result.entryCount"))
    }

    @Test func theLocalLLMRejectionLogsNoBufferText() throws {
        let code = try ProductionSource.code(
            of: "app/macos/hyperwhisper/Managers/Transcription/PostProcessing/AIPostProcessor.swift"
        )
        #expect(!code.contains("bufferPreview"), "the buffer is the model's rewrite of the transcript")
        #expect(code.contains("bufferLen=\\(buffer.count)"))
    }

    @Test func aFailedKeystrokeLogsNoCharacter() throws {
        let code = try ProductionSource.code(
            of: "app/macos/hyperwhisper/Utilities/TextInputService.swift"
        )
        #expect(!code.contains("String(char), privacy: .public"), "each typed character is the transcript")
    }
}
