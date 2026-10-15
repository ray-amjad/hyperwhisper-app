//
//  HistoryPlayerAndSelectionTests.swift
//  hyperwhisperTests
//
//  #1460, three History defects from one fuzz session:
//  1. The player had no playhead, no elapsed time and no seek. The waveform now
//     maps a click to a time and draws a playhead; the label reads
//     "elapsed / total".
//  2. A retry rebuilt the List and left its selection grey (inactive) with no
//     keyboard focus. Focus is now handed to the rebuilt List, only when the
//     old one held it.
//  3. The multi-delete confirm button said "Delete All" for a partial
//     selection. It now names the count in every locale.
//
//  The views need a real window, so these tests pin the pure decisions the
//  views make, plus the strings every locale ships.
//

import Foundation
import Testing

@testable import HyperWhisper

struct WaveformScrubTests {

    @Test func aClickMapsToItsFractionOfTheWidth() {
        #expect(WaveformScrub.fraction(atX: 0, width: 200) == 0)
        #expect(WaveformScrub.fraction(atX: 100, width: 200) == 0.5)
        #expect(WaveformScrub.fraction(atX: 200, width: 200) == 1)
    }

    @Test func aClickPastEitherEndClamps() {
        #expect(WaveformScrub.fraction(atX: -15, width: 200) == 0)
        #expect(WaveformScrub.fraction(atX: 260, width: 200) == 1)
    }

    @Test func aViewWithNoWidthYetSeeksToTheStart() {
        #expect(WaveformScrub.fraction(atX: 50, width: 0) == 0)
        #expect(WaveformScrub.fraction(atX: .nan, width: 200) == 0)
    }

    @Test func aFractionSeeksToThatShareOfTheFile() {
        // The 97-minute file from the report: the middle is 48m 38s.
        let duration: TimeInterval = 97 * 60 + 16
        #expect(WaveformScrub.time(atFraction: 0.5, duration: duration) == duration / 2)
        #expect(WaveformScrub.time(atFraction: 0, duration: duration) == 0)
        #expect(WaveformScrub.time(atFraction: 1, duration: duration) == duration)
        #expect(WaveformScrub.time(atFraction: 1.4, duration: duration) == duration)
        #expect(WaveformScrub.time(atFraction: -0.2, duration: duration) == 0)
    }

    @Test func anUnknownDurationSeeksNowhere() {
        #expect(WaveformScrub.time(atFraction: 0.5, duration: 0) == 0)
        #expect(WaveformScrub.time(atFraction: 0.5, duration: .infinity) == 0)
        #expect(WaveformScrub.time(atFraction: .nan, duration: 16) == 0)
    }

    @Test func thePlayheadSitsAtTheTimesShareOfTheFile() {
        #expect(WaveformScrub.fraction(atTime: 4, duration: 16) == 0.25)
        #expect(WaveformScrub.fraction(atTime: 0, duration: 16) == 0)
        #expect(WaveformScrub.fraction(atTime: 20, duration: 16) == 1)
    }

    @Test func noPlayheadWithoutADuration() {
        #expect(WaveformScrub.fraction(atTime: 4, duration: 0) == nil)
        #expect(WaveformScrub.fraction(atTime: 4, duration: -1) == nil)
        #expect(WaveformScrub.fraction(atTime: .nan, duration: 16) == nil)
    }

    @Test func aSeekAndThePlayheadRoundTrip() {
        let duration: TimeInterval = 16
        for fraction in [0.0, 0.1, 0.5, 0.9, 1.0] {
            let time = WaveformScrub.time(atFraction: fraction, duration: duration)
            let back = WaveformScrub.fraction(atTime: time, duration: duration)
            #expect(abs((back ?? -1) - fraction) < 1e-9)
        }
    }
}

struct HistoryPlaybackClockTests {

    /// A fixed formatter so the label shape is tested without the bundle.
    private func seconds(_ t: TimeInterval) -> String { "\(Int(t))s" }

    @Test func theLabelReadsElapsedOverTotal() {
        #expect(HistoryPlaybackClock.label(elapsed: 5, duration: 16, format: seconds) == "5s / 16s")
        #expect(HistoryPlaybackClock.label(elapsed: 0, duration: 16, format: seconds) == "0s / 16s")
    }

    @Test func elapsedNeverReadsPastTheFile() {
        #expect(HistoryPlaybackClock.label(elapsed: 16.4, duration: 16, format: seconds) == "16s / 16s")
        #expect(HistoryPlaybackClock.label(elapsed: -1, duration: 16, format: seconds) == "0s / 16s")
        #expect(HistoryPlaybackClock.label(elapsed: .nan, duration: 16, format: seconds) == "0s / 16s")
    }

    @Test func theDefaultLabelUsesTheHistoryDurationFormat() {
        let label = HistoryPlaybackClock.label(elapsed: 65, duration: 97 * 60 + 16)
        #expect(label == "\(formatDuration(65)) / \(formatDuration(97 * 60 + 16))")
    }

    @Test func playResumesFromAStoredPosition() {
        #expect(HistoryPlaybackClock.startTime(position: 7.5, duration: 16) == 7.5)
    }

    @Test func playFromTheEndOrNowhereStartsOver() {
        #expect(HistoryPlaybackClock.startTime(position: 0, duration: 16) == 0)
        #expect(HistoryPlaybackClock.startTime(position: 16, duration: 16) == 0)
        #expect(HistoryPlaybackClock.startTime(position: 30, duration: 16) == 0)
        #expect(HistoryPlaybackClock.startTime(position: -2, duration: 16) == 0)
        #expect(HistoryPlaybackClock.startTime(position: .nan, duration: 16) == 0)
    }
}

struct HistoryListFocusTests {

    @Test func aRebuildOfTheFocusedListHandsFocusToTheNewList() {
        #expect(HistoryListFocus.identityToRefocus(focused: "old", old: "old", new: "new") == "new")
    }

    @Test func aRebuildNeverStealsFocusFromElsewhere() {
        // nil: the search field or the detail pane holds focus.
        #expect(HistoryListFocus.identityToRefocus(focused: nil as String?, old: "old", new: "new") == nil)
    }

    @Test func noRebuildNoHandOff() {
        #expect(HistoryListFocus.identityToRefocus(focused: "same", old: "same", new: "same") == nil)
    }

    @Test func aFocusLossFromAReplacedListIsHandedOn() {
        #expect(HistoryListFocus.identityToRefocusAfterFocusLoss(
            previous: "old", now: nil, current: "new"
        ) == "new")
    }

    @Test func aFocusLossFromTheCurrentListIsTheUsers() {
        // The user clicked the search field: the current list lost focus.
        #expect(HistoryListFocus.identityToRefocusAfterFocusLoss(
            previous: "current", now: nil, current: "current"
        ) == nil)
    }

    @Test func gainingFocusIsNotALoss() {
        #expect(HistoryListFocus.identityToRefocusAfterFocusLoss(
            previous: nil, now: "current", current: "current"
        ) == nil)
        #expect(HistoryListFocus.identityToRefocusAfterFocusLoss(
            previous: "old", now: "current", current: "current"
        ) == nil)
        #expect(HistoryListFocus.identityToRefocusAfterFocusLoss(
            previous: nil as String?, now: nil, current: "current"
        ) == nil)
    }

    /// Source guard: the List is a focus target keyed by its identity. A plain
    /// Bool focus state would make the rebuilt List the same target as the
    /// one it replaced, and the hand-off would be a no-op.
    @Test func theListIsFocusedByItsIdentity() throws {
        let screen = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Views/HistoryView.swift",
            from: "private struct HistoryScreen",
            to: "private func contextMenuItems"
        )
        #expect(screen.contains("@FocusState private var focusedList: HistoryViewModel.ListIdentity?"))
        #expect(screen.contains(".focused($focusedList, equals: viewModel.listIdentity)"))
        #expect(screen.contains("HistoryListFocus.identityToRefocus("))
        #expect(screen.contains("HistoryListFocus.identityToRefocusAfterFocusLoss("))
    }
}

struct MultiDeleteConfirmButtonTests {

    private static let key = "transcripts.delete.multiple.confirm"

    @Test func everyLocaleNamesTheCountWithOneIntegerSpecifier() throws {
        let localizations = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(
            at: localizations,
            includingPropertiesForKeys: nil
        ).filter { $0.pathExtension == "lproj" }
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try ProductionSource.text(of: locale.appendingPathComponent("Localizable.strings"))
                .components(separatedBy: .newlines)
            let matches = lines.filter { $0.hasPrefix("\"\(Self.key)\" = \"") }
            #expect(matches.count == 1, "\(locale.lastPathComponent) needs exactly one \(Self.key)")
            guard let line = matches.first else { continue }
            let specifiers = line.components(separatedBy: "%").count - 1
            #expect(specifiers == 1, "\(locale.lastPathComponent): \(line)")
            #expect(line.contains("%d"), "\(locale.lastPathComponent): \(line)")
        }
    }

    @Test func theEnglishButtonReadsDeleteNRecordings() throws {
        let base = try #require(BaseLocalizationBundle.resolve(in: .main))
        let format = try #require(base.localizedValueIfPresent(forKey: Self.key))
        #expect(String(format: format, 4) == "Delete 4 Recordings")
    }

    /// Source guard: the button passes the count. Without it, "%d" shows raw.
    @Test func theDialogPassesTheCount() throws {
        let dialog = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift",
            from: "private func showDeleteConfirmation(",
            to: "alert.addButton(withTitle: \"common.cancel\""
        )
        let compact = dialog.filter { !$0.isWhitespace }
        #expect(compact.contains("\"\(Self.key)\".localized(arguments:transcripts.count)"), "\(dialog)")
    }
}
