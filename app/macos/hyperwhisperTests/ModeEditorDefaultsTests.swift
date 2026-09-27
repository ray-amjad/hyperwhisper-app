//
//  ModeEditorDefaultsTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #873: Create New Mode opened on HyperWhisper
//  Cloud for an unlicensed user with a local model installed, and the new
//  mode was made active at once, so the next dictation needed a key the
//  profile did not have.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Mode editor create defaults")
struct ModeEditorDefaultsTests {

    @Test func unlicensedWithLocalModelOpensOnDevice() {
        #expect(ModeEditorDefaults.initialProvider(licenseActive: false, availableModelIds: ["base.en"]) == .local)
    }

    @Test func licensedKeepsCloudDefault() {
        #expect(ModeEditorDefaults.initialProvider(licenseActive: true, availableModelIds: ["base.en"]) == .cloud)
    }

    @Test func unlicensedWithNoLocalModelKeepsCloudDefault() {
        #expect(ModeEditorDefaults.initialProvider(licenseActive: false, availableModelIds: []) == .cloud)
    }
}
