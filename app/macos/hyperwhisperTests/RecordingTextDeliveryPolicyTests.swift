//
//  RecordingTextDeliveryPolicyTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct RecordingTextDeliveryPolicyTests {
    @Test func keepsDeliverySuppressedAfterOnboardingCloses() {
        let result = RecordingTextDeliveryPolicy.shouldSuppress(
            sessionStartedSuppressed: true,
            currentlySuppressed: false,
            trigger: .shortcut
        )

        #expect(result)
    }

    @Test func onboardingTriggerAlwaysSuppressesDelivery() {
        let result = RecordingTextDeliveryPolicy.shouldSuppress(
            sessionStartedSuppressed: false,
            currentlySuppressed: false,
            trigger: .onboarding
        )

        #expect(result)
    }

    @Test func currentGateSuppressesSessionStartedElsewhere() {
        let result = RecordingTextDeliveryPolicy.shouldSuppress(
            sessionStartedSuppressed: false,
            currentlySuppressed: true,
            trigger: .shortcut
        )

        #expect(result)
    }

    @Test func ordinarySessionCanDeliverWhenNoGateApplies() {
        let result = RecordingTextDeliveryPolicy.shouldSuppress(
            sessionStartedSuppressed: false,
            currentlySuppressed: false,
            trigger: .shortcut
        )

        #expect(!result)
    }
}
