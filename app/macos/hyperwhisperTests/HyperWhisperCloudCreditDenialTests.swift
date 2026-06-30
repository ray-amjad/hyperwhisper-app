//
//  HyperWhisperCloudCreditDenialTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct HyperWhisperCloudCreditDenialTests {

    @Test func parsesDecimalCreditFieldsWithoutFallingBackToZero() {
        let denial = HyperWhisperCloudCreditDenial(
            errorJson: [
                "credits_remaining": 98.6,
                "credits_required": "150.2"
            ],
            message: "Insufficient credits"
        )

        #expect(denial.remaining == 98.6)
        #expect(denial.required == 150.2)
        #expect(denial.remainingForTranscriptionError == 98)
        #expect(denial.requiredForTranscriptionError == 150)
        #expect(denial.invalidExhaustedBalanceMessage == nil)
    }

    @Test func extractsPositiveBalanceFromExhaustedTrialMessage() {
        let denial = HyperWhisperCloudCreditDenial(
            errorJson: [:],
            message: "Your device trial credits are exhausted. You have 98.6 of 150 credits remaining."
        )

        #expect(denial.remaining == 98.6)
        #expect(denial.limit == 150)
        #expect(denial.invalidExhaustedBalanceMessage == nil)
    }

    @Test func flagsExhaustedMessageWhenRequiredIsLessThanRemaining() {
        let denial = HyperWhisperCloudCreditDenial(
            errorJson: [
                "credits_remaining": 98.6,
                "credits_required": 15.0
            ],
            message: "Your device trial credits are exhausted. You have 98.6 of 150 credits remaining."
        )

        #expect(denial.invalidExhaustedBalanceMessage != nil)
    }

    @Test func preservesExpectedBillingDenialWhenRequiredExceedsRemaining() {
        let denial = HyperWhisperCloudCreditDenial(
            errorJson: [
                "credits_remaining": 4.5,
                "credits_required": 10.0
            ],
            message: "Your device trial credits are exhausted."
        )

        #expect(denial.remainingForTranscriptionError == 4)
        #expect(denial.requiredForTranscriptionError == 10)
        #expect(denial.invalidExhaustedBalanceMessage == nil)
    }

    @Test func preservesExpectedBillingDenialWhenRequiredEqualsRemaining() {
        let denial = HyperWhisperCloudCreditDenial(
            errorJson: [
                "credits_remaining": 10.0,
                "credits_required": 10.0
            ],
            message: "Your device trial credits are exhausted."
        )

        #expect(denial.invalidExhaustedBalanceMessage == nil)
    }
}
