//
//  HomeStatsBar.swift
//  hyperwhisper
//
//  Compact stats strip shown at the top of the home view:
//
//    [ avg WPM ] | [ words this month ] | [ words this year ] | [ minutes saved ⚙ ]
//
//  All numbers still come from existing Transcript data, but the arithmetic no
//  longer lives here: issue #285 moved it into the `hw-stats` crate, so macOS,
//  Windows and Linux all read the same answer. This view now counts words,
//  shifts each row's date into the local calendar, and hands the rows to
//  `statsCalculateHome`.
//
//  - Average WPM     : total transcript words / total speaking minutes (all time)
//  - Words / month   : sum of words for transcripts in the current calendar month
//  - Words / year    : sum of words for transcripts in the current calendar year
//  - Saved / week    : (words / typingSpeedWPM) - actualSpeakingMinutes, floored at 0
//
//  Three things a macOS user will notice, all deliberate:
//
//  1. The week now starts on MONDAY. `Calendar.current.dateInterval(of:
//     .weekOfYear:)` starts it on Sunday in en-US, so a Sunday's dictation used
//     to open a fresh "saved this week" figure; it now closes the week it
//     belongs to.
//  2. "Saved this week" is clamped to one week of minutes
//     (`statsSavedMinutesCeiling()`). A long transcript with a zero duration
//     used to be able to claim months of saved time.
//  3. A corrupt duration no longer crashes the home view. The old
//     `Int(saved.rounded())` trapped on NaN — one non-finite `duration` column
//     took the whole strip down. The core normalises non-finite and negative
//     seconds to 0.
//
//  The gear menu next to "Saved this week" lets the user tune the assumed
//  typing speed (default 40 WPM) so the savings figure means something to them.
//

import SwiftUI
import CoreData

struct HomeStatsBar: View {

    // MARK: - Data

    @FetchRequest(
        sortDescriptors: [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)],
        predicate: NSPredicate(format: "status == %@", "completed"),
        animation: .none
    )
    private var transcripts: FetchedResults<Transcript>

    /// User-tunable reference typing speed used by the "saved this week" calc.
    @AppStorage("homeStats.typingSpeedWPM") private var typingSpeedWPM: Int = 40

    // MARK: - Cached aggregates (computed off the main thread)
    //
    // Only the four displayed figures are kept. Everything they used to be
    // derived from — all-time words, all-time seconds, this week's words and
    // seconds — now stays inside the shared core.

    @State private var averageWPM: Int = 0
    @State private var monthWords: Int = 0
    @State private var yearWords: Int = 0
    @State private var savedThisWeekMinutes: Int = 0

    // MARK: - Body

    var body: some View {
        HStack(spacing: 0) {
            statColumn(
                value: "\(averageWPM) WPM",
                label: "home.stats.speed".localized
            )

            separator

            statColumn(
                value: "\(monthWords)",
                label: "home.stats.words.month".localized
            )

            separator

            statColumn(
                value: "\(yearWords)",
                label: "home.stats.words.year".localized
            )

            separator

            statColumn(
                value: savedThisWeekDisplay,
                label: "home.stats.saved.week".localized,
                valueFontSize: 15,
                trailing: { typingSpeedMenu }
            )
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 14)
        .background(
            RoundedRectangle(cornerRadius: 12)
                .fill(.thinMaterial)
                .overlay(
                    RoundedRectangle(cornerRadius: 12)
                        .stroke(Color.primary.opacity(0.06), lineWidth: 0.5)
                )
        )
        .onAppear { recomputeAsync() }
        .onChange(of: transcripts.count) { _ in recomputeAsync() }
        // The typing speed is an input to the core now, not a divisor applied
        // at render time, so changing it has to re-run the calculation.
        .onChange(of: typingSpeedWPM) { _ in recomputeAsync() }
    }

    // MARK: - Subviews

    @ViewBuilder
    private func statColumn<Trailing: View>(
        value: String,
        label: String,
        valueFontSize: CGFloat = 18,
        @ViewBuilder trailing: () -> Trailing = { EmptyView() }
    ) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(value)
                .font(.system(size: valueFontSize, weight: .semibold))
                .foregroundColor(.primary)
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.7)

            HStack(spacing: 4) {
                Text(label)
                    .font(.system(size: 11))
                    .foregroundColor(.secondary)
                    .lineLimit(1)
                trailing()
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var separator: some View {
        Rectangle()
            .fill(Color.primary.opacity(0.08))
            .frame(width: 1, height: 32)
            .padding(.horizontal, 8)
    }

    private var typingSpeedMenu: some View {
        Menu {
            ForEach([30, 40, 50, 60, 80, 100], id: \.self) { wpm in
                Button {
                    typingSpeedWPM = wpm
                } label: {
                    HStack {
                        Text("\(wpm) WPM")
                        if typingSpeedWPM == wpm {
                            Spacer()
                            Image(systemName: "checkmark")
                        }
                    }
                }
            }
        } label: {
            Image(systemName: "gearshape.fill")
                .font(.system(size: 7))
                .foregroundColor(.secondary.opacity(0.6))
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .help("home.stats.typing.speed.help".localized)
    }

    // MARK: - Derived values

    private var savedThisWeekDisplay: String {
        "home.stats.minutes.value".localized(arguments: savedThisWeekMinutes)
    }

    // MARK: - Computation

    /// Count the words off the main thread — as before, so we don't stutter the
    /// recording-dialog waveform animation — then let the shared core do the
    /// bucketing and the arithmetic.
    private func recomputeAsync() {
        let snapshot: [(date: Date?, text: String?, duration: Double, status: String?)] =
            transcripts.map {
                (date: $0.date, text: $0.text, duration: $0.duration, status: $0.status)
            }
        let typingSpeed = Int32(clamping: typingSpeedWPM)

        Task.detached(priority: .userInitiated) {
            let rows: [HwStatsTranscript] = snapshot.map { item in
                HwStatsTranscript(
                    // A row with no date can't be placed on the calendar. Stamp
                    // it at the epoch so it still counts toward the all-time
                    // average — exactly as it always did — without landing in
                    // the current week, month or year.
                    createdAtLocalEpochSeconds: item.date.map { localEpochSeconds(for: $0) } ?? 0,
                    wordCount: UInt32(clamping: countWordsInText(item.text ?? "")),
                    // Handed over raw. The core normalises a non-finite or
                    // negative value to 0 rather than trapping on it.
                    durationSeconds: item.duration,
                    status: statsStatus(for: item.status)
                )
            }

            let stats = statsCalculateHome(
                transcripts: rows,
                typingSpeedWordsPerMinute: typingSpeed,
                nowLocalEpochSeconds: localEpochSeconds(for: Date())
            )

            // Only the four displayed integers cross back to the main actor.
            let speed = Int(stats.averageWordsPerMinute)
            let month = Int(stats.thisMonth.wordCount)
            let year = Int(stats.thisYear.wordCount)
            let saved = Int(stats.savedThisWeekMinutes)

            await MainActor.run {
                averageWPM = speed
                monthWords = month
                yearWords = year
                savedThisWeekMinutes = saved
            }
        }
    }
}

// MARK: - Core hand-off (file-level so Task.detached can capture it)

private func countWordsInText(_ text: String) -> Int {
    text.components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.count
}

/// The core takes instants that are *already shifted* into the calendar time
/// zone, because the host owns the time-zone database. Doing the shift per row
/// rather than once for the whole set is what keeps DST correct.
private func localEpochSeconds(for date: Date) -> Int64 {
    Int64(
        (date.timeIntervalSince1970 + Double(TimeZone.current.secondsFromGMT(for: date)))
            .rounded(.down))
}

/// The `@FetchRequest` predicate already asks for completed rows only, and the
/// core filters again. Map the column through rather than assuming, so the two
/// filters can never disagree about what a row is.
private func statsStatus(for status: String?) -> HwTranscriptStatus {
    switch status {
    case "completed": return .completed
    case "failed": return .failed
    default: return .processing
    }
}

// MARK: - Preview

#if DEBUG
#Preview {
    HomeStatsBar()
        .environment(\.managedObjectContext, PersistenceController.preview.container.viewContext)
        .padding()
        .frame(width: 700)
        .background(Color.black.opacity(0.6))
}
#endif
