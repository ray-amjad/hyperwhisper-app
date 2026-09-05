//
//  CloudProviderSelectionCorrectionTests.swift
//  hyperwhisperTests
//
//  `PostProcessingProvider.correctedSelection(for:available:)` is the decision
//  `LanguageProcessingSettingsView.ensureValidCloudProvider` makes on every
//  `onAppear` of the mode editor: is the stored post-processing provider still a
//  selectable one, and if not, what should replace it?
//
//  It used to be written inline as
//
//      available.contains(where: { $0.rawValue == postProcessingProvider })
//
//  which is wrong the moment the stored token is not literally a `rawValue`.
//  Since #285 the first-run seeder writes the canonical cross-platform token
//  `"hyperwhispercloud"` (`hw-catalog::mode_seed`), and
//  `.hyperwhisper.rawValue` is `"hyperwhisper"`. The membership test failed, the
//  auto-correct fired, and simply OPENING a freshly seeded mode in the editor
//  rewrote it back to the legacy spelling and fired `.onChange` — undoing, with
//  no user action and no visible change, the one thing this branch exists to do.
//
//  The first round's fix then over-corrected in the other direction: it answered
//  "does the stored string PARSE?" and stopped there, which threw away a
//  canonicaliser the `rawValue` comparison had been doing by accident. The BYOK
//  Picker tags its rows `.tag(provider.rawValue)`, so a stored `"OpenAI"` — the
//  Local API stores caller strings verbatim by design, and a backup can restore
//  one — parses to `.openai`, is judged fine, matches no tag, and SwiftUI draws
//  an EMPTY menu button. Parseable is not the same as selectable.
//
//  So the three properties pinned here are:
//
//    * a value the tolerant parser accepts is NEVER corrected back to a
//      different provider, whichever of the three HyperWhisper Cloud spellings
//      it is;
//    * a value that genuinely must be replaced is replaced with `storageValue`,
//      because a provider the app DERIVES is persisted canonically (a
//      caller-supplied string is stored verbatim instead — see
//      `PostProcessingProviderAliasTests`); and
//    * whatever the row ends up holding is something the Picker can actually
//      select — which for HyperWhisper Cloud is every spelling (its control is
//      `Bool`-tagged) and for a BYOK provider is exactly `rawValue`.
//

import Foundation
import Testing

@testable import HyperWhisper

struct CloudProviderSelectionCorrectionTests {

    /// The picker contents for a user with no BYOK keys configured:
    /// HyperWhisper Cloud needs no key so it always survives the health filter.
    private static let cloudOnly: [PostProcessingProvider] = [.hyperwhisper]

    /// HyperWhisper Cloud plus a healthy BYOK provider.
    private static let cloudAndOpenAI: [PostProcessingProvider] = [.hyperwhisper, .openai]

    // MARK: - The bug

    @Test("the seeded provider token is left alone")
    func seededTokenIsNotCorrected() {
        // Exactly what `initializeDefaultModes()` writes, read from the same
        // shared seed rather than restated as a literal.
        let seeded = SeededModeValues.forRegion(nil).postProcessingProvider
        #expect(seeded == PostProcessingProvider.hyperwhisper.storageValue)

        #expect(PostProcessingProvider.correctedSelection(for: seeded, available: Self.cloudOnly) == nil)
        #expect(PostProcessingProvider.correctedSelection(for: seeded, available: Self.cloudAndOpenAI) == nil)
    }

    @Test("the old rawValue comparison really would have missed it")
    func theRawValueComparisonIsTheTrap() {
        // Not a tautology: this is the exact expression the validator used, and
        // it is why the seeded row was being rewritten. If a later change ever
        // makes `.hyperwhisper.rawValue` the canonical token too, this flips and
        // the reason for `correctedSelection` should be re-read.
        let seeded = SeededModeValues.forRegion(nil).postProcessingProvider
        #expect(PostProcessingProvider.allCases.contains(where: { $0.rawValue == seeded }) == false)
        #expect(PostProcessingProvider(rawValue: seeded) == .hyperwhisper)
    }

    @Test("every HyperWhisper Cloud spelling survives validation")
    func everySpellingIsLeftAlone() {
        // The same alias set `PostProcessingProviderAliasTests` and the Linux
        // suite pin. A stored backup from any head must not be "corrected".
        for spelling in ["hyperwhisper", "hyperwhispercloud", "hyperwhisper_cloud"] {
            #expect(PostProcessingProvider.correctedSelection(for: spelling, available: Self.cloudOnly) == nil)
        }
        // Casing and stray whitespace are folded by the parser too.
        #expect(PostProcessingProvider.correctedSelection(for: "HyperWhisperCloud", available: Self.cloudOnly) == nil)
    }

    @Test("a still-offered BYOK provider is left alone")
    func aValidDirectProviderIsNotCorrected() {
        #expect(PostProcessingProvider.correctedSelection(for: "openai", available: Self.cloudAndOpenAI) == nil)
    }

    // MARK: - Parseable is not selectable

    @Test("a BYOK provider stored in a spelling the Picker cannot tag is rewritten")
    func aNonCanonicalDirectSpellingIsCanonicalised() {
        // The regression the first round introduced. `"OpenAI"` reaches the row
        // through `POST /modes` (stored verbatim, by design) or a backup
        // restore. It parses, and `availableCloudProviders` keeps it via its
        // `if provider == current` arm — so if this returns nil, nothing is
        // written, the Picker's `.tag("openai")` never matches, and the menu
        // button renders EMPTY.
        for spelling in ["OpenAI", "OPENAI", " openai ", "OpEnAi"] {
            #expect(
                PostProcessingProvider.correctedSelection(for: spelling, available: Self.cloudAndOpenAI) == "openai",
                "`\(spelling)` must be rewritten to the token the BYOK Picker tags"
            )
        }
    }

    @Test("whatever survives correction is something the BYOK Picker can select")
    func aCorrectionIsAlwaysSomethingThePickerCanTag() {
        // The invariant the view actually depends on, stated once. `.tag()` in
        // `ModePostProcessingSettings` is `provider.rawValue`, so for a BYOK
        // provider the settled value must BE that rawValue.
        let available: [PostProcessingProvider] = [.hyperwhisper, .openai, .anthropic, .groq]
        let stored = ["OpenAI", "openai", "Anthropic", "ANTHROPIC", " groq ", "not-a-provider", ""]

        for value in stored {
            let settled = PostProcessingProvider.correctedSelection(for: value, available: available) ?? value
            guard let provider = PostProcessingProvider(rawValue: settled) else {
                Issue.record("`\(value)` settled on `\(settled)`, which does not parse at all")
                continue
            }
            if provider == .hyperwhisper {
                // Its Source control is `Bool`-tagged and it is filtered out of
                // the BYOK list, so its string is never matched against a tag.
                continue
            }
            #expect(
                settled == provider.rawValue,
                "`\(value)` settled on `\(settled)`, which no BYOK Picker row is tagged with"
            )
        }
    }

    @Test("the HyperWhisper Cloud spellings are exempt, and that is the point")
    func theCloudProviderIsNotDraggedIntoTheCanonicalisation() {
        // The tension between this fix and the round-1 fix, pinned. `.hyperwhisper`
        // is the ONE provider whose stored token is deliberately not its
        // `rawValue`, and rewriting it to match a tag is exactly the bug that
        // "corrected" every freshly seeded mode back to the legacy spelling.
        for spelling in ["hyperwhispercloud", "hyperwhisper", "hyperwhisper_cloud", "HyperWhisperCloud"] {
            #expect(
                PostProcessingProvider.correctedSelection(for: spelling, available: Self.cloudAndOpenAI) == nil,
                "`\(spelling)` must survive untouched"
            )
        }
    }

    // MARK: - What a real correction writes

    @Test("a correction is persisted as storageValue, not rawValue")
    func correctionUsesTheCanonicalToken() {
        // An unparseable stored value must be replaced — and the replacement is
        // a provider the app derived, so it is written canonically. Asserting
        // against the literal as well as `storageValue` so that a regression to
        // `fallback.rawValue` fails here loudly.
        let corrected = PostProcessingProvider.correctedSelection(for: "not-a-provider", available: Self.cloudOnly)
        #expect(corrected == "hyperwhispercloud")
        #expect(corrected == PostProcessingProvider.hyperwhisper.storageValue)
        #expect(corrected != PostProcessingProvider.hyperwhisper.rawValue)
    }

    @Test("a provider that is no longer offered is corrected to the first available")
    func aWithdrawnProviderIsCorrected() {
        // BYOK key removed / provider unhealthy, so it dropped out of the list.
        #expect(PostProcessingProvider.correctedSelection(for: "openai", available: Self.cloudOnly)
            == PostProcessingProvider.hyperwhisper.storageValue)
        #expect(PostProcessingProvider.correctedSelection(for: "anthropic", available: [.openai, .groq])
            == "openai")
    }

    @Test("an empty or blank stored value is corrected")
    func emptyValuesAreCorrected() {
        // `PostProcessingProvider(rawValue:)` returns nil for these by design,
        // so they are not selectable and must be replaced.
        #expect(PostProcessingProvider.correctedSelection(for: "", available: Self.cloudOnly)
            == PostProcessingProvider.hyperwhisper.storageValue)
        #expect(PostProcessingProvider.correctedSelection(for: "   ", available: Self.cloudOnly)
            == PostProcessingProvider.hyperwhisper.storageValue)
    }

    // MARK: - The edges the caller depends on

    @Test("nothing available means nothing is written")
    func anEmptyPickerNeverClobbersTheStoredValue() {
        // The view's `if let` must not blank the row when there is nothing to
        // put there — the old code had the same property via `available.first`.
        #expect(PostProcessingProvider.correctedSelection(for: "not-a-provider", available: []) == nil)
        #expect(PostProcessingProvider.correctedSelection(for: "", available: []) == nil)
    }

    @Test("correcting twice is a no-op the second time")
    func correctionIsIdempotent() {
        // The validator runs on `onAppear` AND on several `onChange` handlers,
        // so a correction that did not settle would loop.
        let pickers: [[PostProcessingProvider]] = [Self.cloudOnly, Self.cloudAndOpenAI, [.openai, .groq]]
        for available in pickers {
            guard let once = PostProcessingProvider.correctedSelection(for: "not-a-provider", available: available) else {
                Issue.record("a non-empty picker should always produce a correction")
                continue
            }
            #expect(PostProcessingProvider.correctedSelection(for: once, available: available) == nil)
        }
    }

    @Test("a custom endpoint string is reported as invalid, so callers must guard first")
    func customEndpointsMustBeHandledBeforeThisIsCalled() {
        // Documents the precondition rather than a behaviour we want: a custom
        // OpenAI-compatible endpoint is deliberately NOT a PostProcessingProvider,
        // so it looks unparseable here. `ensureValidCloudProvider` returns on
        // `isCustomEndpointSelected` before reaching this call; if that guard were
        // ever removed, every custom endpoint would be silently replaced by
        // HyperWhisper Cloud.
        let endpoint = "custom:\(UUID().uuidString)"
        #expect(CustomPostProcessingEndpoint.isCustomProviderString(endpoint))
        #expect(PostProcessingProvider.correctedSelection(for: endpoint, available: Self.cloudOnly)
            == PostProcessingProvider.hyperwhisper.storageValue)
    }
}
