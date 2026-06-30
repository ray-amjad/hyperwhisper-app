//
//  ReleaseNotesCard.swift
//  hyperwhisper
//
//  RELEASE NOTES CARD COMPONENT
//  Displays a single software release with version, date, and release notes.
//  Used in the Recent Updates section on the home page.
//
//  Design Goals:
//  - Match existing GettingStartedCard aesthetic
//  - Show version badge prominently
//  - Display release date in user-friendly format
//  - Format release notes as bullet points
//  - Handle releases with or without notes gracefully
//
//  Visual Hierarchy:
//  1. Version badge (left) + Date (right)
//  2. Release title (bold)
//  3. Bullet point list of features

import SwiftUI

/// COMPONENT: ReleaseNotesCard
/// Displays release information in a card format
///
/// Properties:
/// - release: The AppcastItem containing version and release notes
/// - isLatest: Whether this is the most recent release (shows "NEW" badge)
///
/// Design:
/// - Rounded card with thin material background
/// - Version badge on the left
/// - Release date on the right
/// - Title and bullet points if release notes exist
/// - Compact display if no release notes
struct ReleaseNotesCard: View {
    // MARK: - Properties

    /// The release to display
    let release: AppcastItem

    /// Whether this is the latest release
    let isLatest: Bool

    // MARK: - Body

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            // Header: Version + Date
            HStack {
                // Version badge
                HStack(spacing: 6) {
                    Image(systemName: "arrow.up.circle.fill")
                        .font(.system(size: 14))
                        .foregroundColor(.accentColor)

                    Text("home.recent.updates.version".localized(arguments: release.version))
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundColor(.primary)

                    // NEW badge for latest release
                    if isLatest {
                        Text("home.recent.updates.new.badge".localized)
                            .font(.system(size: 9, weight: .bold))
                            .foregroundColor(.white)
                            .padding(.horizontal, 5)
                            .padding(.vertical, 2)
                            .background(Color.green)
                            .cornerRadius(3)
                    }
                }

                Spacer()

                // Release date
                Text(release.formattedDate)
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
            }

            // Release notes (if available)
            if release.hasReleaseNotes {
                // Title (bold part from HTML)
                if let title = release.releaseTitle {
                    Text(title)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundColor(.primary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                // Bullet points
                if !release.bulletPoints.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(release.bulletPoints, id: \.self) { point in
                            HStack(alignment: .top, spacing: 8) {
                                // Bullet point
                                Circle()
                                    .fill(Color.secondary.opacity(0.5))
                                    .frame(width: 4, height: 4)
                                    .padding(.top, 5)

                                // Point text
                                Text(point)
                                    .font(.system(size: 12))
                                    .foregroundColor(.secondary)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                }
            } else {
                // No release notes available
                Text("home.recent.updates.no.notes".localized)
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
                    .italic()
            }
        }
        .padding(16)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(.thinMaterial)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.accentColor.opacity(isLatest ? 0.2 : 0), lineWidth: 1)
        )
    }
}

// MARK: - Preview

#if DEBUG
#Preview("With Release Notes") {
    ReleaseNotesCard(
        release: .sample,
        isLatest: true
    )
    .padding()
    .frame(width: 500)
}

#Preview("Without Release Notes") {
    ReleaseNotesCard(
        release: .sampleNoNotes,
        isLatest: false
    )
    .padding()
    .frame(width: 500)
}

#Preview("Multiple Cards") {
    ScrollView {
        VStack(spacing: 12) {
            ReleaseNotesCard(
                release: .sample,
                isLatest: true
            )

            ReleaseNotesCard(
                release: .sampleNoNotes,
                isLatest: false
            )
        }
        .padding()
    }
    .frame(width: 500, height: 400)
}
#endif
