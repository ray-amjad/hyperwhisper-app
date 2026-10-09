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
//  #1669 adds two server error bodies that can echo the transcript: a custom
//  post-processing endpoint's, and the Cloud post-process error message.
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

    // #1669: a custom post-processing endpoint can echo the request, which holds
    // the transcript, in its error body.
    @Test func aCustomEndpointErrorBodyIsNotPublic() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/PostProcessing/AIPostProcessor.swift",
            from: "AppLogger.transcription.info(\"Custom endpoint post-processing completed successfully\")",
            to: "message: \"Custom endpoint server error\")"
        )
        #expect(body.contains("Custom endpoint HTTP error"))
        #expect(!body.contains("prefix(200), privacy: .public"), "the error body can echo the transcript")
        #expect(!body.contains("preview, privacy: .public"), "the error body can echo the transcript")
        #expect(body.contains("httpResponse.statusCode, privacy: .public"), "the status code stays")
        #expect(body.contains("responseString.count, privacy: .public"))
    }

    // #1669 sibling: the Cloud post-process 500 carries the upstream LLM's
    // error text in `message`.
    @Test func aCloudPostProcessErrorMessageIsNotPublic() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/PostProcessing/AIPostProcessor.swift",
            from: "private func handleHyperWhisperCloudError(statusCode: Int, data: Data) throws {",
            to: "let preview = responseString.prefix(200)"
        )
        #expect(body.contains("HyperWhisper Cloud API error"))
        #expect(!body.contains("errorMessage, privacy: .public"), "the message can carry an upstream error body")
        #expect(body.contains("statusCode, privacy: .public"), "the status code stays")
    }

    // #1669 sibling: that 5xx message is thrown as `serverError(message:)`, and
    // the Cloud post-process catch logs the error's description, which holds it.
    @Test func aCloudPostProcessFailureDescriptionIsNotPublic() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/PostProcessing/AIPostProcessor.swift",
            from: "try self.handleHyperWhisperCloudError(statusCode: httpResponse.statusCode, data: data)",
            to: "private func handleHyperWhisperCloudError(statusCode: Int, data: Data) throws {"
        )
        #expect(body.contains("HyperWhisper Cloud post-processing failed"))
        #expect(!body.contains("localizedDescription, privacy: .public"), "a serverError description carries the server message")
        #expect(body.contains("localizedDescription, privacy: .private"))
        #expect(body.contains("serverStatus, privacy: .public"), "the status code stays")
    }
}
