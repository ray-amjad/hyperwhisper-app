//
//  OnboardingFocusComponents.swift
//  hyperwhisper
//
//  THE "FOCUSED TASK" ONBOARDING DESIGN SYSTEM
//  One decision per screen, said out loud. Every step is an anchor glyph, a
//  question, one supporting line, and a single cluster of cards. Nothing else is
//  ever on screen.
//
//  These are the shared parts. Every value comes off the production scale in
//  `Utilities/DesignConstants.swift`: spacing on 4 / 8 / 10 / 12 / 20 / 24 and
//  radius on 6 / 10 / 16. Colours are semantic or accent derived only, so the
//  whole flow is correct in light and dark without a single colour literal.
//
//  Named with an `Onboarding` prefix throughout because these live in the app
//  target alongside the settings and menu bar components.
//

import SwiftUI

// MARK: - Tokens

enum OnboardingStyle {
    /// The sheet is pinned to this size. There is no title bar to hide inside a
    /// sheet, so the progress hairline sits flush at y = 0 for free.
    static let windowWidth: CGFloat = 760
    static let windowHeight: CGFloat = 580

    /// The single column every step is written into.
    static let bodyWidth: CGFloat = 520
    static let questionWidth: CGFloat = 480
    static let detailWidth: CGFloat = 440
    static let footerHeight: CGFloat = 56

    /// Stage insets from the design reference, `padding: 24px 100px 32px`. 100
    /// and 32 are stage insets, not control spacing, so they sit outside the
    /// 4 / 8 / 10 / 12 / 20 / 24 scale on purpose.
    static let stageInsetLeading: CGFloat = 100
    static let stageInsetBottom: CGFloat = 32

    static let cardRadius = DesignConstants.CornerRadius.medium   // 10
    static let controlRadius = DesignConstants.CornerRadius.small // 6

    static let hairline = Color(nsColor: .separatorColor)
    static let fillSoft = Color.primary.opacity(0.04)
    static let fill = Color.primary.opacity(0.08)
    static let fillStrong = Color.primary.opacity(0.14)

    static let accentFill = Color.accentColor.opacity(0.12)
    static let accentStroke = Color.accentColor.opacity(0.5)
    static let accentSoft = Color.accentColor.opacity(0.10)
    static let accentLine = Color.accentColor.opacity(0.20)
    static let accentChip = Color.accentColor.opacity(0.18)

    static let okFill = Color.green.opacity(0.12)
}

// MARK: - Progress

/// Eight hairline segments, flush to the top edge of the sheet.
struct OnboardingStepProgress: View {
    let current: Int
    let total: Int

    var body: some View {
        HStack(spacing: 0) {
            ForEach(0..<total, id: \.self) { index in
                Rectangle()
                    .fill(color(for: index))
                    .frame(maxWidth: .infinity)
                if index < total - 1 {
                    Color.clear.frame(width: 2)
                }
            }
        }
        .frame(height: 3)
        .accessibilityElement()
        .accessibilityLabel("onboarding.progress.a11y".localized(arguments: current + 1, total))
    }

    private func color(for index: Int) -> Color {
        if index == current { return Color.accentColor }
        if index < current { return Color.accentColor.opacity(0.45) }
        return OnboardingStyle.fill
    }
}

// MARK: - Step scaffold

/// The anchor glyph, the question, the one supporting line, then a single
/// cluster of cards.
struct OnboardingStepScaffold<Content: View>: View {
    let symbol: String
    let question: String
    let detail: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Spacer(minLength: 0)

            OnboardingGlyphTile(symbol: symbol)

            Text(question)
                .font(.system(size: 24, weight: .semibold))
                .tracking(-0.35)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: OnboardingStyle.questionWidth, alignment: .leading)
                .padding(.top, DesignConstants.Spacing.medium)

            Text(detail)
                .font(.system(size: 14))
                .foregroundStyle(.secondary)
                .lineSpacing(4)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: OnboardingStyle.detailWidth, alignment: .leading)
                .padding(.top, DesignConstants.Spacing.small)

            VStack(alignment: .leading, spacing: DesignConstants.Spacing.small) {
                content
            }
            .padding(.top, DesignConstants.Spacing.xl)

            Spacer(minLength: 0)
        }
        .frame(width: OnboardingStyle.bodyWidth, alignment: .leading)
        .padding(.leading, OnboardingStyle.stageInsetLeading)
        .padding(.top, DesignConstants.Spacing.xl)
        .padding(.bottom, OnboardingStyle.stageInsetBottom)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }
}

/// The 36 pt accent tinted glyph tile that anchors every step.
struct OnboardingGlyphTile: View {
    let symbol: String

    var body: some View {
        RoundedRectangle(cornerRadius: OnboardingStyle.cardRadius, style: .continuous)
            .fill(OnboardingStyle.accentFill)
            .overlay(
                RoundedRectangle(cornerRadius: OnboardingStyle.cardRadius, style: .continuous)
                    .strokeBorder(OnboardingStyle.accentStroke, lineWidth: 1)
            )
            .overlay(
                Image(systemName: symbol)
                    .font(.system(size: 18, weight: .medium))
                    .foregroundStyle(Color.accentColor)
            )
            .frame(width: 36, height: 36)
            .accessibilityHidden(true)
    }
}

// MARK: - Cards

struct OnboardingCard<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        VStack(spacing: 0) {
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.thinMaterial)
        .clipShape(RoundedRectangle(cornerRadius: OnboardingStyle.cardRadius, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: OnboardingStyle.cardRadius, style: .continuous)
                .strokeBorder(OnboardingStyle.hairline, lineWidth: 1)
        )
    }
}

struct OnboardingCardRow<Content: View>: View {
    var highlighted = false
    @ViewBuilder var content: Content

    var body: some View {
        HStack(spacing: DesignConstants.Spacing.medium) {
            content
        }
        .padding(DesignConstants.Spacing.medium)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(highlighted ? OnboardingStyle.accentFill : Color.clear)
    }
}

struct OnboardingCardBlock<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            content
        }
        .padding(DesignConstants.Spacing.medium)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct OnboardingCardDivider: View {
    var body: some View {
        Rectangle()
            .fill(OnboardingStyle.hairline)
            .frame(height: 1)
    }
}

/// Row title plus optional caption: the two type sizes every card row uses.
struct OnboardingRowText: View {
    let title: String
    var caption: String?
    var singleLine = false

    var body: some View {
        VStack(alignment: .leading, spacing: DesignConstants.Spacing.xs) {
            Text(title)
                .font(.system(size: 14, weight: .semibold))
                .lineLimit(singleLine ? 1 : nil)
                .truncationMode(.tail)
                .fixedSize(horizontal: false, vertical: !singleLine)

            if let caption {
                Text(caption)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .lineSpacing(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

// MARK: - Small parts

struct OnboardingRadioMark: View {
    let selected: Bool

    var body: some View {
        ZStack {
            Circle()
                .strokeBorder(selected ? Color.accentColor : OnboardingStyle.fillStrong, lineWidth: 1.5)
                .frame(width: 16, height: 16)
            if selected {
                Circle()
                    .fill(Color.accentColor)
                    .frame(width: 8, height: 8)
            }
        }
        .accessibilityHidden(true)
    }
}

struct OnboardingStatusPill: View {
    enum Tone {
        case neutral
        case good
        case accent
    }

    let text: String
    var symbol: String?
    var tone: Tone = .neutral

    var body: some View {
        HStack(spacing: DesignConstants.Spacing.xs) {
            if let symbol {
                Image(systemName: symbol)
                    .font(.system(size: 10, weight: .bold))
            }
            Text(text)
                .font(.system(size: 12))
        }
        .foregroundStyle(foreground)
        .padding(.horizontal, DesignConstants.Spacing.small)
        .frame(height: 22)
        .background(
            RoundedRectangle(cornerRadius: OnboardingStyle.controlRadius, style: .continuous)
                .fill(background)
        )
        .fixedSize()
    }

    private var foreground: Color {
        switch tone {
        case .neutral: Color.secondary
        case .good: Color.green
        case .accent: Color.accentColor
        }
    }

    private var background: Color {
        switch tone {
        case .neutral: OnboardingStyle.fill
        case .good: OnboardingStyle.okFill
        case .accent: OnboardingStyle.accentChip
        }
    }
}

struct OnboardingStepNumber: View {
    let value: Int

    var body: some View {
        Text("\(value)")
            .font(.system(size: 12, weight: .semibold, design: .rounded))
            .foregroundStyle(.secondary)
            .frame(width: 22, height: 22)
            .background(Circle().fill(OnboardingStyle.fill))
            .accessibilityHidden(true)
    }
}

struct OnboardingKeyCap: View {
    let label: String

    var body: some View {
        Text(label)
            .font(.system(size: 12, weight: .semibold))
            .padding(.horizontal, DesignConstants.Spacing.small)
            .frame(height: 24)
            .background(
                RoundedRectangle(cornerRadius: OnboardingStyle.controlRadius, style: .continuous)
                    .fill(OnboardingStyle.fill)
            )
            .overlay(
                RoundedRectangle(cornerRadius: OnboardingStyle.controlRadius, style: .continuous)
                    .strokeBorder(OnboardingStyle.hairline, lineWidth: 1)
            )
            .fixedSize()
    }
}

struct OnboardingChip: View {
    let label: String
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: DesignConstants.Spacing.xs) {
                if selected {
                    Image(systemName: "checkmark")
                        .font(.system(size: 10, weight: .bold))
                }
                Text(label)
                    .font(.system(size: 12, weight: selected ? .semibold : .regular))
            }
            .foregroundStyle(selected ? Color.accentColor : Color.secondary)
            .padding(.horizontal, DesignConstants.Spacing.rowPadding)
            .frame(height: 28)
            .background(
                RoundedRectangle(cornerRadius: OnboardingStyle.controlRadius, style: .continuous)
                    .fill(selected ? OnboardingStyle.accentChip : OnboardingStyle.fill)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
        .accessibilityAddTraits(selected ? [.isSelected] : [])
    }
}

// MARK: - Notes

/// A quiet footnote. Deliberately not a box, so it never reads as another thing
/// to click next to the card above it.
struct OnboardingQuietNote: View {
    let text: String
    var symbol = "info.circle"

    var body: some View {
        HStack(alignment: .top, spacing: DesignConstants.Spacing.small) {
            Image(systemName: symbol)
                .font(.system(size: 13))
                .foregroundStyle(.tertiary)
            Text(text)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.top, DesignConstants.Spacing.xs)
    }
}

struct OnboardingAccentNote: View {
    let text: String
    var symbol = "waveform"

    var body: some View {
        HStack(alignment: .top, spacing: DesignConstants.Spacing.medium) {
            Image(systemName: symbol)
                .font(.system(size: 14))
                .foregroundStyle(Color.accentColor)
            Text(text)
                .font(.system(size: 13))
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(DesignConstants.Spacing.medium)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: OnboardingStyle.cardRadius, style: .continuous)
                .fill(OnboardingStyle.accentSoft)
        )
        .overlay(
            RoundedRectangle(cornerRadius: OnboardingStyle.cardRadius, style: .continuous)
                .strokeBorder(OnboardingStyle.accentLine, lineWidth: 1)
        )
    }
}

/// Inline failure copy. Kept at caption scale so a card never reflows when an
/// error appears.
struct OnboardingErrorNote: View {
    let text: String

    var body: some View {
        HStack(alignment: .top, spacing: DesignConstants.Spacing.small) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 12))
            Text(text)
                .font(.system(size: 12))
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .foregroundStyle(Color.red)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

// MARK: - Fields

/// The monospaced key field from the reference. `secure` masks the value, which
/// is what a BYOK provider key gets; the HyperWhisper access key stays visible
/// so a paste can be checked by eye.
struct OnboardingKeyField: View {
    let placeholder: String
    @Binding var text: String
    var secure = false

    var body: some View {
        Group {
            if secure {
                SecureField(placeholder, text: $text)
            } else {
                TextField(placeholder, text: $text)
            }
        }
        .textFieldStyle(.plain)
        .font(.system(size: 12, design: .monospaced))
        .lineLimit(1)
        .truncationMode(.tail)
        .padding(.horizontal, DesignConstants.Spacing.rowPadding)
        .frame(height: 32)
        .frame(maxWidth: .infinity)
        .background(
            RoundedRectangle(cornerRadius: OnboardingStyle.controlRadius, style: .continuous)
                .fill(OnboardingStyle.fillSoft)
        )
        .overlay(
            RoundedRectangle(cornerRadius: OnboardingStyle.controlRadius, style: .continuous)
                .strokeBorder(OnboardingStyle.hairline, lineWidth: 1)
        )
    }
}

// MARK: - Setup readouts

struct OnboardingCheckLine: View {
    let text: String
    let done: Bool

    var body: some View {
        OnboardingCardRow {
            Image(systemName: done ? "checkmark.circle.fill" : "circle.dashed")
                .font(.system(size: 14))
                .foregroundStyle(done ? Color.green : Color.secondary)
            OnboardingRowText(title: text)
                .opacity(done ? 1 : 0.55)
        }
        .accessibilityElement(children: .combine)
    }
}

struct OnboardingBigNumber: View {
    let value: String
    let caption: String
    var compact = false

    var body: some View {
        VStack(alignment: .leading, spacing: DesignConstants.Spacing.xs) {
            Text(value)
                .font(.system(size: compact ? 17 : 30, weight: .semibold, design: .rounded))
                .monospacedDigit()
            Text(caption)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.tail)
        }
        .accessibilityElement(children: .combine)
    }
}

struct OnboardingProgressBar: View {
    let value: Double

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(OnboardingStyle.fill)
                Capsule()
                    .fill(Color.accentColor)
                    .frame(width: max(0, min(1, value)) * proxy.size.width)
            }
        }
        .frame(height: 6)
        .accessibilityElement()
        .accessibilityLabel("onboarding.a11y.downloadProgress".localized)
        .accessibilityValue("onboarding.a11y.percent".localized(arguments: Int(max(0, min(1, value)) * 100)))
    }
}

/// The indeterminate sibling of `OnboardingProgressBar`, for a download where
/// work is definitely happening but no trustworthy fraction exists (issue #312:
/// FluidAudio emits nothing usable until a whole file lands, and its compile
/// tail is a four-step staircase, so a number in either would read as frozen).
///
/// Built from the same `GeometryReader` + `Capsule` + `frame(height: 6)` as
/// `OnboardingProgressBar` rather than from `ProgressView(.linear)`. The two
/// swap places inside a card that pins no height of its own, so anything but
/// identical layout resizes the card and everything below it at the swap — and
/// the system bar takes AppKit's intrinsic height, which is not 6 pt and cannot
/// be pinned to 6 pt without risking a clipped animation.
struct OnboardingIndeterminateProgressBar: View {
    /// What VoiceOver reads as this bar's value. There is no percentage to read,
    /// so the caller hands over the live phase line ("Downloading file 12 of 22")
    /// instead of one constant string for the whole four minutes.
    var accessibilityValue: String = "onboarding.a11y.inProgress".localized

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var sweeping = false

    /// Width of the travelling highlight, as a share of the track.
    private static let sweepShare: CGFloat = 0.35

    var body: some View {
        GeometryReader { proxy in
            let trackWidth = proxy.size.width
            let sweepWidth = trackWidth * Self.sweepShare
            ZStack(alignment: .leading) {
                Capsule().fill(OnboardingStyle.fill)
                if reduceMotion {
                    // Reduce Motion: a still, dimmed fill. AppKit's own bar honours
                    // the setting for free; a hand written sweep has to be asked.
                    // The phase line above carries the "something is happening".
                    Capsule().fill(Color.accentColor.opacity(0.45))
                } else {
                    Capsule()
                        .fill(Color.accentColor)
                        .frame(width: sweepWidth)
                        .offset(x: sweeping ? trackWidth : -sweepWidth)
                }
            }
            .clipShape(Capsule())
            .onAppear {
                guard !reduceMotion else { return }
                withAnimation(.easeInOut(duration: 1.2).repeatForever(autoreverses: false)) {
                    sweeping = true
                }
            }
        }
        // Identical to `OnboardingProgressBar`, on purpose. See the note above.
        .frame(height: 6)
        .accessibilityElement()
        .accessibilityLabel("onboarding.a11y.downloadProgress".localized)
        .accessibilityValue(accessibilityValue)
    }
}

// MARK: - Level meter

/// Thirty three bars fed by the real idle metering session on
/// `AudioRecordingManager`. Each published sample is pushed onto the right hand
/// edge, so the row scrolls like a waveform instead of pumping as one block.
struct OnboardingLevelMeter: View {
    let level: Float
    var active = true

    private static let barCount = 33

    @State private var history: [CGFloat] = Array(repeating: 0, count: OnboardingLevelMeter.barCount)

    var body: some View {
        HStack(spacing: DesignConstants.Spacing.small) {
            ForEach(0..<Self.barCount, id: \.self) { index in
                Capsule()
                    .fill(Color.accentColor.opacity(0.65))
                    .frame(width: 6, height: 4 + history[index] * 38)
            }
        }
        .frame(height: 44)
        .animation(.easeOut(duration: 0.08), value: history)
        .onChange(of: level) { _, newValue in
            guard active else { return }
            push(newValue)
        }
        .onChange(of: active) { _, isActive in
            if !isActive { history = Array(repeating: 0, count: Self.barCount) }
        }
        .accessibilityElement()
        .accessibilityLabel("onboarding.a11y.inputLevel".localized)
        .accessibilityValue("onboarding.a11y.percent".localized(arguments: Int(max(0, min(1, level)) * 100)))
    }

    private func push(_ value: Float) {
        var next = history
        next.removeFirst()
        next.append(CGFloat(max(0, min(1, value))))
        history = next
    }
}

// MARK: - Footer

/// Back, the quiet deferral, the reassurance line, and exactly one primary.
struct OnboardingFooter: View {
    let showsBack: Bool
    let showsDefer: Bool
    let reassurance: String
    let primaryTitle: String
    let primaryEnabled: Bool
    let deferTitle: String
    let onBack: () -> Void
    let onDefer: () -> Void
    let onPrimary: () -> Void

    var body: some View {
        HStack(spacing: DesignConstants.Spacing.medium) {
            Button(action: onBack) {
                HStack(spacing: DesignConstants.Spacing.xs) {
                    Image(systemName: "chevron.left")
                        .font(.system(size: 11, weight: .semibold))
                    Text(localized: "common.back")
                        .font(.system(size: 13, weight: .medium))
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .opacity(showsBack ? 1 : 0)
            .disabled(!showsBack)
            .accessibilityHidden(!showsBack)

            // First run setup can need a download or a network round trip, so the
            // deferral stays reachable while there is still setup left to skip,
            // as the quietest control on screen so the single primary keeps its
            // hierarchy. It is withdrawn on the final step, where the work is
            // already done and deferring would only roll it back.
            Button(action: onDefer) {
                Text(deferTitle)
                    .font(.system(size: 13))
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(.tertiary)
            .opacity(showsDefer ? 1 : 0)
            .disabled(!showsDefer)
            .accessibilityHidden(!showsDefer)

            Spacer(minLength: DesignConstants.Spacing.medium)

            Text(reassurance)
                .font(.system(size: 12))
                .foregroundStyle(.tertiary)
                .lineLimit(1)
                .truncationMode(.tail)

            Button(primaryTitle, action: onPrimary)
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(!primaryEnabled)
                .keyboardShortcut(.defaultAction)
        }
        .padding(.horizontal, DesignConstants.Spacing.large)
        .frame(height: OnboardingStyle.footerHeight)
        .overlay(alignment: .top) { OnboardingCardDivider() }
    }
}

// MARK: - Flow layout

/// Wraps the provider chips onto as many rows as they need, at a fixed gap.
struct OnboardingFlowLayout: Layout {
    var spacing: CGFloat = DesignConstants.Spacing.small

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) -> CGSize {
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        return arrange(sizes: sizes, maxWidth: proposal.width ?? .infinity).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) {
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        let result = arrange(sizes: sizes, maxWidth: bounds.width)
        for (index, subview) in subviews.enumerated() {
            let origin = result.origins[index]
            subview.place(
                at: CGPoint(x: bounds.minX + origin.x, y: bounds.minY + origin.y),
                proposal: ProposedViewSize(sizes[index])
            )
        }
    }

    private func arrange(sizes: [CGSize], maxWidth: CGFloat) -> (origins: [CGPoint], size: CGSize) {
        var origins: [CGPoint] = []
        var x: CGFloat = 0
        var y: CGFloat = 0
        var rowHeight: CGFloat = 0
        var widest: CGFloat = 0

        for size in sizes {
            if x > 0, x + size.width > maxWidth {
                x = 0
                y += rowHeight + spacing
                rowHeight = 0
            }
            origins.append(CGPoint(x: x, y: y))
            x += size.width + spacing
            widest = max(widest, x - spacing)
            rowHeight = max(rowHeight, size.height)
        }

        return (origins, CGSize(width: widest, height: y + rowHeight))
    }
}
