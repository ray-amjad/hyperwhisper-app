//
//  InlineErrorToastLayoutTests.swift
//  hyperwhisperTests
//
//  Issue #1563: the inline error toast held its message to one line, so every
//  toast that says "your raw transcript was kept" lost that half: "OpenAI
//  rejected your API key — yo...". The toast now gives the message two lines
//  inside the same 360x40 panel.
//
//  These tests lay the message out with SwiftUI itself (`NSHostingView`), in
//  the width the toast's row really leaves it: the panel width less the
//  padding, the warning icon, the countdown, the gaps and, when the error
//  offers one, the Open Settings button. Each of the five English "raw
//  transcript was kept" toasts, for every post-processing provider name, must
//  wrap into at most `messageLineLimit` lines, and those lines must fit the
//  panel's height. A screenshot of the real toast on a Mac is the end-to-end
//  proof; this pins the sizes so a longer string or a tighter row is caught.
//

import AppKit
import SwiftUI
import Testing
@testable import HyperWhisper

@MainActor
@Suite("Inline error toast shows the whole kept-transcript message (#1563)")
struct InlineErrorToastLayoutTests {

    /// The English Base table, read from the repo so the test does not depend
    /// on the language the test process runs in.
    private static func englishTable() throws -> [String: String] {
        let url = ProductionSource.url("app/macos/hyperwhisper/Localizations/Base.lproj/Localizable.strings")
        return try #require(NSDictionary(contentsOf: url) as? [String: String], "Base Localizable.strings must parse")
    }

    /// One hosting view, reused for every measurement: a fresh `NSHostingView`
    /// per string made the suite hold the main actor for ~30 s on CI.
    private static let host = NSHostingView(rootView: AnyView(EmptyView()))

    /// The SwiftUI fitting size of a view.
    private static func fittingSize<V: View>(_ view: V) -> CGSize {
        host.rootView = AnyView(view)
        host.layout()
        return host.fittingSize
    }

    /// Height SwiftUI gives `text` in the toast's message font at `width`,
    /// with no line limit, so a message that needs three lines shows it.
    private static func messageHeight(_ text: String, width: CGFloat) -> CGFloat {
        fittingSize(
            Text(text)
                .font(InlineErrorToast.messageFont)
                .multilineTextAlignment(.center)
                .lineLimit(nil)
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: width)
        ).height
    }

    /// Width the message column keeps once the row's fixed parts are laid out,
    /// without and with the Open Settings button.
    private static let messageColumnWidths: (plain: CGFloat, withSettings: CGFloat) = {
        let icon = fittingSize(InlineErrorToast.warningIcon).width
        let button = fittingSize(InlineErrorToast.settingsButtonLabel).width
        let plain = InlineErrorToast.toastWidth
            - 2 * InlineErrorToast.horizontalPadding
            - icon
            - InlineErrorToast.countdownWidth
            - 2 * InlineErrorToast.itemSpacing
        return (plain, plain - button - InlineErrorToast.itemSpacing)
    }()

    private static func messageColumnWidth(showSettingsButton: Bool) -> CGFloat {
        showSettingsButton ? messageColumnWidths.withSettings : messageColumnWidths.plain
    }

    /// Height of one line of the message font.
    private static let oneLineHeight = messageHeight("Ag", width: 1000)

    /// The providers whose pre-flight can fail with the provider-naming toast.
    /// `TranscriptionProviderRouter.checkPostProcessingProviderHealth` skips the
    /// check for HyperWhisper Cloud and for providers that need none, and sends
    /// Local LLM to `.localRuntimeUnavailable` (its own toast, checked below).
    private static let preflightToastProviders = PostProcessingProvider.allCases.filter {
        $0 != .hyperwhisper && $0 != .localLLM && $0.requiresHealthCheck
    }

    /// Every "raw transcript was kept" toast, with whether it shows Open Settings.
    private static func keptTranscriptToasts() throws -> [(message: String, showSettingsButton: Bool)] {
        let table = try englishTable()
        let english: (String) -> String = { table[$0] ?? $0 }

        let local = TranscriptionError.localRuntimeUnavailable(reason: "test")
        var toasts: [(String, Bool)] = [
            (try #require(table["transcription.error.localRuntimeUnavailable"]), local.showSettingsButton)
        ]
        let preflightErrors: [TranscriptionError] = [
            .unauthorized(provider: "provider"),
            .transientNetwork(details: "Provider unreachable"),
            .modelNotDownloaded(),
            .providerNotAvailable(provider: "provider", reason: "Provider health check failed"),
        ]
        for provider in preflightToastProviders {
            for error in preflightErrors {
                let message = try #require(TranscriptionPipeline.postProcessingPreflightToastMessage(
                    for: error,
                    providerDisplayName: provider.displayName,
                    localize: english
                ))
                toasts.append((message, error.showSettingsButton))
            }
        }
        return toasts
    }

    /// The toasts under test are the kept-transcript ones, and the toast the
    /// issue captured ("OpenAI rejected your API key — yo...") really needs a
    /// second line, so the old one-line layout did cut it. Shorter ones (no
    /// Settings button, a short provider name) may fit one line; that is fine.
    @Test func theIssuesToastNeedsMoreThanOneLine() throws {
        let toasts = try Self.keptTranscriptToasts()
        #expect(toasts.count == 1 + 4 * Self.preflightToastProviders.count)
        for toast in toasts {
            #expect(toast.message.contains("your raw transcript was kept"), "\(toast.message)")
        }
        let issueToast = try #require(toasts.first {
            $0.message == "OpenAI rejected your API key — your raw transcript was kept."
        })
        #expect(issueToast.showSettingsButton)
        let oneLine = Self.oneLineHeight
        let width = Self.messageColumnWidth(showSettingsButton: true)
        #expect(Self.messageHeight(issueToast.message, width: width) > oneLine * 1.5)
    }

    /// The acceptance check of #1563: each English toast shows its whole text,
    /// in at most two lines that fit the 40 pt panel.
    @Test func everyKeptTranscriptToastFitsTwoLinesInsideTheToast() throws {
        #expect(InlineErrorToast.messageLineLimit == 2)
        let oneLine = Self.oneLineHeight
        let allowedLines = CGFloat(InlineErrorToast.messageLineLimit)
        let innerHeight = InlineErrorToast.toastHeight - 2 * InlineErrorToast.verticalPadding
        #expect(oneLine * allowedLines <= innerHeight,
                "two lines (\(oneLine * allowedLines) pt) must fit the toast's \(innerHeight) pt")

        for toast in try Self.keptTranscriptToasts() {
            let width = Self.messageColumnWidth(showSettingsButton: toast.showSettingsButton)
            let height = Self.messageHeight(toast.message, width: width)
            #expect(height <= oneLine * allowedLines + 0.5,
                    "'\(toast.message)' needs \(height) pt at \(width) pt wide; \(Int(allowedLines)) lines are \(oneLine * allowedLines) pt")
        }
    }
}
