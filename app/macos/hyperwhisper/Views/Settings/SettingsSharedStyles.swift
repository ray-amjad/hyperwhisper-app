//
//  SettingsSharedStyles.swift
//  hyperwhisper
//
//  Shared layout helpers used by the settings screens so each
//  section follows the same sizing and card styling.
//

import SwiftUI

enum SettingsLayout {
    /// Maximum width for the scrollable content column. Align all sections to this width
    /// so that none of them can push the navigation sidebar out of place.
    static let contentWidth: CGFloat = 560

    /// Standard width used when a card needs a narrower appearance than the full column.
    static let cardWidth: CGFloat = 520
}

/// Wrapper that applies the common section title styling and constrains content to the shared width.
/// An optional trailing `accessory` is rendered on the title row (e.g. a refresh button) so a
/// section can put a control next to its header instead of on a separate row. Defaults to nothing,
/// so existing call sites are unaffected.
struct SettingsSection<Content: View, Accessory: View>: View {
    let title: LocalizedStringKey
    let maxWidth: CGFloat
    @ViewBuilder var accessory: Accessory
    @ViewBuilder var content: Content

    init(
        title: LocalizedStringKey,
        maxWidth: CGFloat = SettingsLayout.contentWidth,
        @ViewBuilder accessory: () -> Accessory = { EmptyView() },
        @ViewBuilder content: () -> Content
    ) {
        self.title = title
        self.maxWidth = maxWidth
        self.accessory = accessory()
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(alignment: .firstTextBaseline) {
                Text(title)
                    .font(.title)
                    .fontWeight(.semibold)
                Spacer()
                accessory
            }
            content
        }
        .frame(maxWidth: maxWidth, alignment: .leading)
    }
}

/// Group sub-heading rendered between stacked SettingsCards. Visually
/// subordinate to the SettingsSection page title (`.title`).
struct SettingsGroupHeader: View {
    let title: LocalizedStringKey
    var body: some View {
        Text(title)
            .font(.title3)
            .fontWeight(.semibold)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.leading, 4)   // optical alignment with card leading edge
    }
}

/// Card-style container with the translucent background used throughout Settings.
struct SettingsCard<Content: View>: View {
    @ViewBuilder var content: Content
    var horizontalPadding: CGFloat

    var maxWidth: CGFloat

    init(horizontalPadding: CGFloat = 8, maxWidth: CGFloat = SettingsLayout.cardWidth, @ViewBuilder content: () -> Content) {
        self.horizontalPadding = horizontalPadding
        self.maxWidth = maxWidth
        self.content = content()
    }

    var body: some View {
        content
            .padding(.vertical, 0)
            .padding(.horizontal, horizontalPadding)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.thinMaterial))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(Color(nsColor: .separatorColor), lineWidth: 1))
            .frame(maxWidth: maxWidth, alignment: .leading)
    }
}

/// Applies the same rounded background and border styling conditionally (legacy helper used by existing rows).
struct ConditionalBackgroundModifier: ViewModifier {
    let standalone: Bool

    func body(content: Content) -> some View {
        content
            .background(
                Group {
                    if standalone {
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .fill(.thinMaterial)
                    } else {
                        Color.clear
                    }
                }
            )
            .overlay(
                Group {
                    if standalone {
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                    }
                }
            )
    }
}

extension View {
    /// Convenience wrapper for applying the conditional background used by settings rows.
    func applyConditionalBackground(standalone: Bool) -> some View {
        modifier(ConditionalBackgroundModifier(standalone: standalone))
    }
}
