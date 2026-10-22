//
//  TranscriptionErrorCodeTests.swift
//  hyperwhisperTests
//
//  `(error as NSError).code` of a TranscriptionError goes to Sentry extras as
//  `errorCode` and to logs. These pin every case to the number Swift's
//  synthesized bridging gave it before issue #1518 changed
//  `modelNotDownloaded` to carry values (which would have renumbered it and
//  every payload-less case after it).
//

import Foundation
import Testing
@testable import HyperWhisper

struct TranscriptionErrorCodeTests {

    /// One value of every case, with its historical code. Adding a case to
    /// TranscriptionError without adding it here leaves the new number unpinned.
    private static var historicalCodes: [(TranscriptionError, Int)] { [
        (.providerNotAvailable(), 0),
        (.transientNetwork(), 1),
        (.invalidResponse(), 2),
        (.apiKeyMissing(), 3),
        (.unauthorized(), 4),
        (.audioFileTooLarge(fileSize: 2, limit: 1, providerName: "P"), 5),
        (.serverError(statusCode: 500, message: "m"), 6),
        (.rateLimited(), 7),
        (.insufficientCredits(remaining: 0, required: 1), 8),
        (.quotaExceeded(provider: "P", message: nil), 9),
        (.timeout(operation: "o"), 10),
        (.localRuntimeUnavailable(reason: "r"), 11),
        (.cloudAccountRequired(), 12),
        (.localSpeechModelEvicted(), 13),
        (.audioTooShort(fileSize: 1), 14),
        (.modelNotDownloaded(), 15),
        (.modelProtected, 16),
        (.audioFileNotFound, 17),
        (.maxRetriesExceeded, 18),
        (.invalidRequest, 19),
        (.streamingInterrupted, 20),
        (.busy, 21),
        (.invalidAudioFormat, 22),
        (.audioConversionFailed, 23),
        (.noSpeechDetected, 24),
    ] }

    @Test func everyCaseKeepsItsHistoricalNSErrorCode() {
        for (error, code) in Self.historicalCodes {
            #expect((error as NSError).code == code, "\(error)")
            #expect(error.errorCode == code, "\(error)")
        }
        #expect(Self.historicalCodes.count == 25)
        #expect(Set(Self.historicalCodes.map(\.1)).count == 25)
    }

    @Test func modelNotDownloadedCodeDoesNotDependOnItsValues() {
        #expect((TranscriptionError.modelNotDownloaded() as NSError).code == 15)
        #expect((TranscriptionError.modelNotDownloaded(model: "Tiny") as NSError).code == 15)
        #expect((TranscriptionError.modelNotDownloaded(model: "Tiny", mode: "W15") as NSError).code == 15)
    }

    @Test func domainIsTheBridgedTypeName() {
        for (error, _) in Self.historicalCodes {
            #expect((error as NSError).domain == "HyperWhisper.TranscriptionError")
        }
        #expect(TranscriptionError.errorDomain == String(reflecting: TranscriptionError.self))
    }

    @Test func localizedDescriptionStillComesFromLocalizedError() throws {
        let error = TranscriptionError.modelNotDownloaded(model: "Tiny", mode: "W15")
        let description = try #require(error.errorDescription)
        #expect((error as NSError).localizedDescription == description)
        #expect((TranscriptionError.busy as NSError).localizedDescription == TranscriptionError.busy.errorDescription)
    }
}
