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
//  Every test here drives the closure form of the binding factory, never a
//  `SettingsManager` — not even a private one. The backing properties are
//  `@AppStorage`, so any write reaches `UserDefaults.standard`, and this test
//  bundle is hosted BY the running app: that write invalidates every
//  `@AppStorage` in the live view tree, the home window re-lays out, and
//  `HomeStatsBar`'s `@FetchRequest` throws "A fetch request must have an
//  entity" and aborts the process. `xcodebuild` then reports dozens of
//  unrelated tests as failing in 0.000 seconds, which reads as an
//  infrastructure fault rather than as this.
//

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
        var stored = StreamingTranscriptionProvider.hyperwhisperCloud.rawValue
        var published = 0

        let binding = StreamingView.providerBinding(
            read: { stored },
            write: { stored = $0 },
            publish: { published += 1 }
        )

        binding.wrappedValue = StreamingTranscriptionProvider.gemini.rawValue

        #expect(published >= 1)
        #expect(stored == StreamingTranscriptionProvider.gemini.rawValue)
    }

    /// The publish has to land BEFORE the write, the way `willSet` would, or the
    /// body that re-evaluates on it reads the value it is being told changed.
    @Test func theProviderPublishPrecedesTheWrite() {
        var stored = StreamingTranscriptionProvider.hyperwhisperCloud.rawValue
        var valueSeenAtPublishTime: String?

        let binding = StreamingView.providerBinding(
            read: { stored },
            write: { stored = $0 },
            publish: { valueSeenAtPublishTime = stored }
        )

        binding.wrappedValue = StreamingTranscriptionProvider.gemini.rawValue

        #expect(valueSeenAtPublishTime == StreamingTranscriptionProvider.hyperwhisperCloud.rawValue)
    }

    /// The same for the HyperWhisper Cloud live tier picker, whose selection
    /// drives the vocabulary language warning next to it.
    @Test func pickingACloudLiveTierPublishes() {
        var stored = "deepgramNova3"
        var published = 0

        let binding = StreamingView.cloudTierBinding(
            read: { stored },
            write: { stored = $0 },
            publish: { published += 1 }
        )

        binding.wrappedValue = "geminiTranscribe"

        #expect(published >= 1)
        #expect(stored == "geminiTranscribe")
    }

    /// The READ half the hand-rolled binding exists for: a legacy stored id
    /// resolves to the row the session actually uses, not to a blank selection.
    @Test func aLegacyStoredProviderIdStillSelectsItsRow() {
        let binding = StreamingView.providerBinding(
            read: { "gemini" },
            write: { _ in },
            publish: {}
        )

        #expect(binding.wrappedValue == StreamingTranscriptionProvider.gemini.rawValue)
    }

    /// An id no build ever wrote must fall back to the default row rather than
    /// render blank — the same clamp the session applies.
    @Test func anUnknownStoredProviderIdFallsBackToTheDefaultRow() {
        let binding = StreamingView.providerBinding(
            read: { "not-a-provider" },
            write: { _ in },
            publish: {}
        )

        #expect(binding.wrappedValue == StreamingTranscriptionProvider.hyperwhisperCloud.rawValue)
    }
}
