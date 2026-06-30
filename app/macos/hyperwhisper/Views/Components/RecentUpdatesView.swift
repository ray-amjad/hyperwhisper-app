//
//  RecentUpdatesView.swift
//  hyperwhisper
//
//  RECENT UPDATES VIEW
//  Displays the latest software releases from the appcast feed.
//  This view is shown on the home page below the Getting Started section.
//
//  Architecture:
//  - Fetches releases on appear using Task/async
//  - Shows loading skeleton while fetching
//  - Displays up to 5 most recent releases
//  - Handles empty and error states gracefully
//  - Uses ReleaseNotesCard for each release
//
//  Design Goals:
//  - Match Getting Started section aesthetic
//  - Provide immediate feedback (loading state)
//  - Show useful error messages with retry option
//  - Cache results to avoid excessive API calls

import SwiftUI

/// VIEW: RecentUpdatesView
/// Displays recent software releases from appcast feed
///
/// Features:
/// - Automatic fetching on view appear
/// - Loading skeleton during fetch
/// - Error handling with retry
/// - Displays latest 5 releases
/// - "NEW" badge on most recent release
///
/// Usage:
/// Add to home page VStack:
/// ```swift
/// RecentUpdatesView()
/// ```
struct RecentUpdatesView: View {
    // MARK: - State

    /// Fetched releases
    @State private var releases: [AppcastItem] = []

    /// Loading state
    @State private var isLoading = false

    /// Error message (if any)
    @State private var errorMessage: String?

    // MARK: - Body

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            // Section header
            sectionHeader

            // Content
            if isLoading {
                loadingView
            } else if let error = errorMessage {
                errorView(error)
            } else if releases.isEmpty {
                emptyView
            } else {
                releasesView
            }
        }
        .onAppear {
            // Trigger fetch when view appears
            if releases.isEmpty && !isLoading {
                fetchReleases()
            }
        }
    }

    // MARK: - Section Header

    /// Section title (sparkles icon intentionally removed — the stats bar
    /// above the section now occupies that visual slot).
    private var sectionHeader: some View {
        Text("home.recent.updates.title".localized)
            .font(.title2)
            .fontWeight(.medium)
    }

    // MARK: - Loading View

    /// Loading skeleton while fetching releases
    private var loadingView: some View {
        VStack(spacing: 12) {
            ForEach(0..<3, id: \.self) { _ in
                loadingSkeleton
            }
        }
    }

    /// Single loading skeleton card
    private var loadingSkeleton: some View {
        VStack(alignment: .leading, spacing: 12) {
            // Version + Date skeleton
            HStack {
                Rectangle()
                    .fill(Color.secondary.opacity(0.2))
                    .frame(width: 100, height: 16)
                    .cornerRadius(4)

                Spacer()

                Rectangle()
                    .fill(Color.secondary.opacity(0.2))
                    .frame(width: 80, height: 14)
                    .cornerRadius(4)
            }

            // Title skeleton
            Rectangle()
                .fill(Color.secondary.opacity(0.2))
                .frame(height: 16)
                .cornerRadius(4)

            // Bullet points skeleton
            VStack(spacing: 8) {
                ForEach(0..<3, id: \.self) { _ in
                    Rectangle()
                        .fill(Color.secondary.opacity(0.15))
                        .frame(height: 12)
                        .cornerRadius(4)
                }
            }
        }
        .padding(16)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(.thinMaterial)
        )
    }

    // MARK: - Error View

    /// Error state with retry button
    private func errorView(_ error: String) -> some View {
        VStack(spacing: 12) {
            HStack(spacing: 12) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 16))
                    .foregroundColor(.orange)

                VStack(alignment: .leading, spacing: 4) {
                    Text("home.recent.updates.error".localized)
                        .font(.system(size: 13, weight: .semibold))

                    Text(error)
                        .font(.system(size: 12))
                        .foregroundColor(.secondary)
                }

                Spacer()

                Button(action: fetchReleases) {
                    Text("home.recent.updates.retry".localized)
                        .font(.system(size: 12, weight: .medium))
                        .padding(.horizontal, 12)
                        .frame(height: 26)
                        .background(Color.orange.opacity(0.9))
                        .foregroundColor(.white)
                        .cornerRadius(6)
                }
                .buttonStyle(.plain)
            }
            .padding(14)
            .background(
                RoundedRectangle(cornerRadius: 10)
                    .fill(Color.orange.opacity(0.10))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 10)
                    .strokeBorder(Color.orange.opacity(0.20), lineWidth: 0.5)
            )
        }
    }

    // MARK: - Empty View

    /// Empty state (no releases found)
    private var emptyView: some View {
        HStack(spacing: 12) {
            Image(systemName: "info.circle.fill")
                .font(.system(size: 16))
                .foregroundColor(.secondary)

            Text("home.recent.updates.empty".localized)
                .font(.system(size: 13))
                .foregroundColor(.secondary)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(.thinMaterial)
        )
    }

    // MARK: - Releases View

    /// Display release cards
    private var releasesView: some View {
        VStack(spacing: 12) {
            ForEach(Array(releases.enumerated()), id: \.element.id) { index, release in
                ReleaseNotesCard(
                    release: release,
                    isLatest: index == 0
                )
            }
        }
    }

    // MARK: - Methods

    /// Fetch releases from appcast feed
    /// Called automatically on view appear and when user retries
    private func fetchReleases() {
        // Reset state
        isLoading = true
        errorMessage = nil

        Task {
            do {
                // Fetch from AppcastParser
                let fetchedReleases = try await AppcastParser.shared.fetchReleases()

                // Update UI on main thread
                await MainActor.run {
                    self.releases = fetchedReleases
                    self.isLoading = false
                }
            } catch {
                // Handle error
                await MainActor.run {
                    self.errorMessage = error.localizedDescription
                    self.isLoading = false
                }
            }
        }
    }

}

// MARK: - Preview

#if DEBUG
#Preview("Loading") {
    RecentUpdatesView()
        .padding()
        .frame(width: 600, height: 500)
}

#Preview("With Releases") {
    RecentUpdatesView()
        .padding()
        .frame(width: 600, height: 500)
}

#Preview("Error") {
    RecentUpdatesView()
        .padding()
        .frame(width: 600, height: 300)
}
#endif
