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
//  Windows and Linux all read the same answer. `HomeStatsModel` (issue #1683)
//  reads the rows off the main thread, counts words, shifts each row's date
//  into the local calendar, and hands the rows to `statsCalculateHome`.
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

struct HomeStatsBar: View {

    // MARK: - Data
    //
    // Issue #1683: no `@FetchRequest` here any more. The model reads the rows on
    // a background context and keeps the four figures between Home visits, so a
    // visit with no new transcript does no fetch and no word count.

    @ObservedObject var model: HomeStatsModel = .shared

    /// User-tunable reference typing speed used by the "saved this week" calc.
    @AppStorage("homeStats.typingSpeedWPM") private var typingSpeedWPM: Int = 40

    private var averageWPM: Int { model.figures.averageWPM }
    private var monthWords: Int { model.figures.monthWords }
    private var yearWords: Int { model.figures.yearWords }
    private var savedThisWeekMinutes: Int { model.figures.savedThisWeekMinutes }

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
        .onAppear { model.homeAppeared(typingSpeed: typingSpeedWPM) }
        .onDisappear { model.homeDisappeared() }
        // The typing speed is an input to the core now, not a divisor applied
        // at render time, so changing it has to re-run the calculation.
        .onChange(of: typingSpeedWPM) { newValue in model.typingSpeedChanged(to: newValue) }
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
}

// MARK: - Preview

#if DEBUG
#Preview {
    HomeStatsBar(model: HomeStatsModel(container: PersistenceController.preview.container))
        .environment(\.managedObjectContext, PersistenceController.preview.container.viewContext)
        .padding()
        .frame(width: 700)
        .background(Color.black.opacity(0.6))
}
#endif
