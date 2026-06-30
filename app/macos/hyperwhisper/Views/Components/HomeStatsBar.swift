//
//  HomeStatsBar.swift
//  hyperwhisper
//
//  Compact stats strip shown at the top of the home view:
//
//    [ avg WPM ] | [ words this month ] | [ words this year ] | [ minutes saved ⚙ ]
//
//  All numbers are derived from existing Transcript data:
//  - Average WPM     : total transcript words / total speaking minutes (all time)
//  - Words / month   : sum of words for transcripts in the current calendar month
//  - Words / year    : sum of words for transcripts in the current calendar year
//  - Saved / week    : (words / typingSpeedWPM) - actualSpeakingMinutes, floored at 0
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

    @State private var allTimeWords: Int = 0
    @State private var allTimeDurationSeconds: Double = 0
    @State private var weekWords: Int = 0
    @State private var weekDurationSeconds: Double = 0
    @State private var monthWords: Int = 0
    @State private var yearWords: Int = 0

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
        .onChange(of: typingSpeedWPM) { _ in /* re-render only */ }
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

    private var averageWPM: Int {
        let minutes = allTimeDurationSeconds / 60.0
        guard minutes > 0 else { return 0 }
        return Int((Double(allTimeWords) / minutes).rounded())
    }

    /// Estimated minutes saved this week vs. typing at the configured speed.
    /// = (wordsThisWeek / typingSpeedWPM) - actualSpeakingMinutes, floored at 0.
    private var savedThisWeekMinutes: Int {
        guard typingSpeedWPM > 0 else { return 0 }
        let typingMinutes = Double(weekWords) / Double(typingSpeedWPM)
        let spokenMinutes = weekDurationSeconds / 60.0
        let saved = typingMinutes - spokenMinutes
        return max(0, Int(saved.rounded()))
    }

    private var savedThisWeekDisplay: String {
        "home.stats.minutes.value".localized(arguments: savedThisWeekMinutes)
    }

    // MARK: - Computation

    /// Crunch words/durations off the main thread so we don't stutter the
    /// recording-dialog waveform animation.
    private func recomputeAsync() {
        let snapshot: [(date: Date?, text: String?, duration: Double)] = transcripts.map {
            (date: $0.date, text: $0.text, duration: $0.duration)
        }

        Task.detached(priority: .userInitiated) {
            let calendar = Calendar.current
            let weekInterval = calendar.dateInterval(of: .weekOfYear, for: Date())
            let monthInterval = calendar.dateInterval(of: .month, for: Date())
            let yearInterval = calendar.dateInterval(of: .year, for: Date())

            var totalWords = 0
            var totalDuration: Double = 0
            var wordsWeek = 0
            var durationWeek: Double = 0
            var wordsMonth = 0
            var wordsYear = 0

            for item in snapshot {
                let words = countWordsInText(item.text ?? "")
                totalWords += words
                totalDuration += item.duration

                if let date = item.date {
                    if weekInterval?.contains(date) == true {
                        wordsWeek += words
                        durationWeek += item.duration
                    }
                    if monthInterval?.contains(date) == true {
                        wordsMonth += words
                    }
                    if yearInterval?.contains(date) == true {
                        wordsYear += words
                    }
                }
            }

            await MainActor.run {
                allTimeWords = totalWords
                allTimeDurationSeconds = totalDuration
                weekWords = wordsWeek
                weekDurationSeconds = durationWeek
                monthWords = wordsMonth
                yearWords = wordsYear
            }
        }
    }
}

// MARK: - Word counting (file-level so Task.detached can capture it)

private func countWordsInText(_ text: String) -> Int {
    text.components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.count
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
