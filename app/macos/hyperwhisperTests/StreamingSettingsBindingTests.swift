//
//  StreamingSettingsBindingTests.swift
//  hyperwhisperTests
//
//  The two normalizing pickers in `StreamingView` write through hand-rolled
//  `Binding`s instead of `$settingsManager.<property>`. That swap is what buys
//  the tolerant READ (a legacy stored id renders the right row instead of a
//  blank selection), and it is also how the section stopped redrawing: the
//  projected binding published `objectWillChange` on every write for free, and
//  a plain assignment to an `@AppStorage` property on an `ObservableObject`
//  publishes nothing at all — `@AppStorage` does not implement the
//  enclosing-instance subscript that `@Published` uses.
//
//  With nothing published, the body never re-evaluates, so
//  `.onChange(of: settingsManager.streamingProvider)` never fires and choosing
//  a BYOK provider never reveals its API-key field.
//

import Combine
import SwiftUI
import Testing
@testable import HyperWhisper

@MainActor
struct StreamingSettingsBindingTests {

    /// Writing through the picker's binding must publish, or the section the
    /// picker lives in never redraws.
    ///
    /// `>= 1` rather than `== 1` on purpose: the point is that at least one
    /// publish reaches the view, and the assertion must fail for the right
    /// reason if the send is ever dropped again (0 publishes), not because some
    /// future SwiftUI release starts publishing `@AppStorage` writes too.
    @Test func pickingAStreamingProviderPublishes() {
        let settings = SettingsManager.shared
        let original = settings.streamingProvider
        defer { settings.streamingProvider = original }

        var published = 0
        let token = settings.objectWillChange.sink { published += 1 }
        defer { token.cancel() }

        StreamingView.providerBinding(for: settings).wrappedValue =
            StreamingTranscriptionProvider.gemini.rawValue

        #expect(published >= 1)
        #expect(settings.streamingProvider == StreamingTranscriptionProvider.gemini.rawValue)
    }

    /// The same for the HyperWhisper Cloud live tier picker, whose selection
    /// drives the vocabulary language warning next to it.
    @Test func pickingACloudLiveTierPublishes() {
        let settings = SettingsManager.shared
        let original = settings.streamingCloudTier
        defer { settings.streamingCloudTier = original }

        var published = 0
        let token = settings.objectWillChange.sink { published += 1 }
        defer { token.cancel() }

        StreamingView.cloudTierBinding(for: settings).wrappedValue = "geminiTranscribe"

        #expect(published >= 1)
        #expect(settings.streamingCloudTier == "geminiTranscribe")
    }

    /// The READ half the hand-rolled binding exists for: a legacy stored id
    /// resolves to the row the session actually uses, not to a blank selection.
    @Test func aLegacyStoredProviderIdStillSelectsItsRow() {
        let settings = SettingsManager.shared
        let original = settings.streamingProvider
        defer { settings.streamingProvider = original }

        settings.streamingProvider = "gemini"
        #expect(
            StreamingView.providerBinding(for: settings).wrappedValue
                == StreamingTranscriptionProvider.gemini.rawValue
        )
    }
}
