import Foundation
import Testing
@testable import HyperWhisper

struct HyperWhisperCloudNoSpeechDiagnosticsTests {

    @Test func explicitNoSpeechResponseProducesMetadataOnlyDiagnostics() {
        let response = HttpResponse(
            status: 200,
            headers: [
                Header(name: "X-STT-Provider", value: "deepgram"),
                Header(name: "x-stt-model", value: "nova-3"),
                Header(name: "X-Request-ID", value: "request-123")
            ],
            body: Data(#"{"no_speech_detected":true,"text":"private speech"}"#.utf8)
        )

        let diagnostics = HyperWhisperCloudProvider.responseDiagnostics(response: response, elapsedMs: 275)

        #expect(diagnostics.attemptSource == "cloud_instrumented")
        #expect(diagnostics.providerDisplayName == "HyperWhisper Cloud")
        #expect(diagnostics.backendRequestId == "request-123")
        #expect(diagnostics.backendSTTProvider == "deepgram")
        #expect(diagnostics.backendSTTModel == "nova-3")
        #expect(diagnostics.backendNoSpeechDetected == true)
        #expect(diagnostics.httpStatusCode == 200)
        #expect(diagnostics.responseLatencyMs == 275)
        #expect(diagnostics.providerAttemptMs == nil)

        let propertyNames = Set(Mirror(reflecting: diagnostics).children.compactMap(\.label))
        #expect(propertyNames.isDisjoint(with: ["body", "responseBody", "result", "text"]))
    }

    @Test func absentNoSpeechFlagRemainsUnknown() {
        let response = HttpResponse(status: 200, headers: [], body: Data(#"{"text":""}"#.utf8))
        let diagnostics = HyperWhisperCloudProvider.responseDiagnostics(response: response, elapsedMs: 10)
        #expect(diagnostics.backendNoSpeechDetected == nil)
    }
}
