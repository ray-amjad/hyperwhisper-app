//
//  CreditsSettingsSection.swift
//  hyperwhisper
//
//  HYPERWHISPER CLOUD CREDITS SECTION
//  Displays current credit balance and usage information for HyperWhisper Cloud
//  Shows remaining minutes, total allocated, and total spent
//

import SwiftUI

struct CreditsSettingsSection: View {
    @EnvironmentObject var hyperWhisperCloudManager: HyperWhisperCloudManager
    @EnvironmentObject var licenseManager: LicenseManager

    // Track loading and error states
    @State private var isLoading = false
    @State private var errorMessage: String?
    @State private var showError = false
    @State private var lastRefreshTime: Date?

    var body: some View {
        SettingsSection(title: LocalizedStringKey("settings.credits.title")) {
            refreshButton
        } content: {
            if isLoading && hyperWhisperCloudManager.credits == nil {
                loadingView
            } else if let credits = hyperWhisperCloudManager.credits {
                creditsContent(credits)
            } else if let error = errorMessage {
                errorView(error)
            } else {
                emptyStateView
            }
        }
        .onAppear {
            // Force refresh credits when page loads
            // This ensures the user always sees fresh data when navigating to the credits page,
            // bypassing both the local 60-second cache and the backend's 1-hour cache
            Task {
                await fetchCredits(forceRefresh: true)
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .licenseStatusChanged)) { _ in
            // AUTO-REFRESH ON LICENSE CHANGE
            // When the user activates or deactivates a license, we need to refresh credits
            // because the identifier changes (license_key ↔ device_id), which means different credit pools
            //
            // Flow:
            // 1. User clicks "Deactivate Device" in License settings
            // 2. LicenseManager.clearLicense() posts .licenseStatusChanged notification
            // 3. HyperWhisperCloudManager receives notification and invalidates cache + clears credits
            // 4. This view receives the notification
            // 5. We force a fresh fetch with the new identifier (device_id for trial)
            // 6. UI updates to show trial credits instead of licensed credits
            //
            // This provides a seamless UX where credits update automatically when license changes
            Task {
                await fetchCredits(forceRefresh: true)
            }
        }
    }

    // MARK: - Refresh Button (rendered on the section header row)

    private var refreshButton: some View {
        Button {
            Task {
                await fetchCredits(forceRefresh: true)
            }
        } label: {
            Label(LocalizedStringKey("settings.credits.refresh"), systemImage: "arrow.clockwise")
                .labelStyle(.iconOnly)
        }
        .buttonStyle(.borderless)
        .disabled(isLoading)
    }

    // MARK: - Credits Content

    private func creditsContent(_ credits: HyperWhisperCloudCredits) -> some View {
        VStack(spacing: 20) {
            // Main balance card
            balanceCard(credits)

            // Usage statistics
            usageStatistics(credits)

            // Link to credits page with identifier
            // Uses NetworkConfig.baseURL which automatically switches between:
            // - Development: http://localhost:3000
            // - Production: https://www.hyperwhisper.com
            HStack {
                Button {
                    let (identifier, _) = licenseManager.getTranscriptionIdentifier()
                    if let url = URL(string: "\(NetworkConfig.baseURL)/credits?license_key=\(identifier)") {
                        NSWorkspace.shared.open(url)
                    }
                } label: {
                    HStack {
                        Image(systemName: "creditcard.fill")
                        Text(LocalizedStringKey("settings.credits.manage"))
                    }
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)

                Spacer()
            }
        }
    }

    private func balanceCard(_ credits: HyperWhisperCloudCredits) -> some View {
        let remainingCreditsValue = String(format: "%.1f", credits.creditsRemaining)
        let currencyValue = credits.creditsRemaining / 1000.0

        return GroupBox {
            VStack(alignment: .leading, spacing: 20) {
                // Header with minutes
                VStack(alignment: .leading, spacing: 4) {
                    Text(LocalizedStringKey("settings.credits.timeRemaining"))
                        .font(.subheadline)
                        .foregroundColor(.secondary)

                    let minutesText = "settings.credits.minutesApproximate".localized(arguments: credits.minutesRemaining)
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(minutesText)
                            .font(.system(size: 42, weight: .bold, design: .rounded))
                            .foregroundColor(credits.isExhausted ? .red : credits.isLow ? .orange : .primary)

                        Text(LocalizedStringKey("settings.credits.minutes"))
                            .font(.title3)
                            .foregroundColor(.secondary)
                    }
                }

                // Credits info
                HStack(spacing: 4) {
                    Image(systemName: "circle.fill")
                        .font(.system(size: 6))
                        .foregroundColor(.secondary)
                    Text(String(format: "settings.credits.creditsRemaining".localized, remainingCreditsValue))
                        .font(.callout)
                        .foregroundColor(.secondary)

                    Spacer()

                    // Dollar value
                    Text(String(format: "settings.credits.currency".localized, currencyValue))
                        .font(.callout)
                        .foregroundColor(.secondary)
                }
                .padding(.vertical, 8)
                .padding(.horizontal, 12)
                .background(
                    RoundedRectangle(cornerRadius: 8)
                        .fill(Color.gray.opacity(0.1))
                )

                // Anonymous user daily reset info
                if credits.isAnonymous, let resetsAt = credits.resetsAt {
                    HStack(spacing: 10) {
                        Image(systemName: "clock.fill")
                            .foregroundColor(.blue)
                            .font(.body)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(LocalizedStringKey("settings.credits.freeLimit"))
                                .font(.callout)
                                .fontWeight(.medium)
                            Text(String(format: "settings.credits.resets".localized, timeFormatter.localizedString(for: resetsAt, relativeTo: Date())))
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                        Spacer()
                    }
                    .padding(12)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(
                        RoundedRectangle(cornerRadius: 10)
                            .fill(Color.blue.opacity(0.12))
                    )
                }

                // Warning if credits are low
                if credits.isLow && !credits.isExhausted {
                    warningBanner(
                        icon: "exclamationmark.triangle.fill",
                        color: .orange,
                        message: "settings.credits.warning.low".localized
                    )
                }

                // Exhausted warning
                if credits.isExhausted {
                    warningBanner(
                        icon: "exclamationmark.octagon.fill",
                        color: .red,
                        message: credits.isAnonymous
                            ? "settings.credits.warning.exhausted.daily".localized
                            : "settings.credits.warning.exhausted".localized
                    )
                }
            }
            .padding(8)
        }
    }

    private func warningBanner(icon: String, color: Color, message: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon)
                .foregroundColor(color)
                .font(.body)
            Text(message)
                .font(.callout)
                .foregroundColor(.primary)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(color.opacity(0.12))
        )
    }

    private func usageStatistics(_ credits: HyperWhisperCloudCredits) -> some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 12) {
                Label(LocalizedStringKey("settings.credits.accountInfo"), systemImage: "info.circle.fill")
                    .font(.headline)
                    .foregroundColor(.secondary)

                Divider()

                // Statistics grid
                LazyVGrid(columns: [
                    GridItem(.flexible()),
                    GridItem(.flexible())
                ], spacing: 16) {
                    statisticItem(
                        title: LocalizedStringKey("settings.credits.stat.creditsRemaining"),
                        value: String(format: "settings.credits.amount".localized, credits.creditsRemaining / 1000.0),
                        icon: "dollarsign.circle.fill",
                        color: .green
                    )

                    statisticItem(
                        title: LocalizedStringKey("settings.credits.stat.minutesRemaining"),
                        value: "~\(credits.minutesRemaining)",
                        icon: "clock.fill",
                        color: .orange
                    )

                    statisticItem(
                        title: LocalizedStringKey("settings.credits.stat.accountType"),
                        value: credits.isLicensed
                            ? "settings.credits.accountType.licensed".localized
                            : (credits.isAnonymous ? "settings.credits.accountType.anonymous".localized : "settings.credits.accountType.trial".localized),
                        icon: credits.isLicensed ? "checkmark.seal.fill" : "person.fill",
                        color: credits.isLicensed ? .green : .gray
                    )

                    if let customerId = credits.customerId {
                        statisticItem(
                            title: LocalizedStringKey("settings.credits.stat.customerId"),
                            value: String(customerId.prefix(8)) + "...",
                            icon: "person.text.rectangle.fill",
                            color: .blue
                        )
                    } else if credits.isAnonymous {
                        statisticItem(
                            title: LocalizedStringKey("settings.credits.stat.dailyLimit"),
                            value: "settings.credits.dailyLimit.value".localized,
                            icon: "repeat.circle.fill",
                            color: .blue
                        )
                    }
                }
            }
            .padding(4)
        }
    }

    private func statisticItem(title: LocalizedStringKey, value: String, icon: String, color: Color) -> some View {
        HStack(spacing: 12) {
            Image(systemName: icon)
                .font(.title2)
                .foregroundColor(color)
                .frame(width: 30)

            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.caption)
                    .foregroundColor(.secondary)
                Text(value)
                    .font(.system(.body, design: .rounded))
                    .fontWeight(.medium)
            }

            Spacer()
        }
    }

    // MARK: - State Views

    private var loadingView: some View {
        VStack(spacing: 16) {
            ProgressView()
                .controlSize(.large)
            Text(LocalizedStringKey("settings.credits.loading"))
                .font(.caption)
                .foregroundColor(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(40)
    }

    private func errorView(_ error: String) -> some View {
        GroupBox {
            VStack(spacing: 12) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.largeTitle)
                    .foregroundColor(.orange)

                Text(LocalizedStringKey("settings.credits.error.title"))
                    .font(.headline)

                Text(error)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)

                Button(LocalizedStringKey("settings.credits.error.retry")) {
                    Task {
                        await fetchCredits(forceRefresh: true)
                    }
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.regular)
            }
            .frame(maxWidth: .infinity)
            .padding(20)
        }
    }

    private var emptyStateView: some View {
        GroupBox {
            VStack(spacing: 12) {
                Image(systemName: "icloud.slash")
                    .font(.largeTitle)
                    .foregroundColor(.secondary)

                Text(LocalizedStringKey("settings.credits.empty.title"))
                    .font(.headline)

                Text(LocalizedStringKey("settings.credits.empty.subtitle"))
                    .font(.caption)
                    .foregroundColor(.secondary)

                Button(LocalizedStringKey("settings.credits.empty.button")) {
                    Task {
                        await fetchCredits()
                    }
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.regular)
            }
            .frame(maxWidth: .infinity)
            .padding(20)
        }
    }

    // MARK: - Helper Functions

    private func fetchCredits(forceRefresh: Bool = false) async {
        // Prevent multiple simultaneous fetches
        guard !isLoading else { return }

        isLoading = true
        errorMessage = nil

        do {
            // Fetch credits from the manager (it handles getting the identifier internally)
            let fetchedCredits = try await hyperWhisperCloudManager.fetchCredits(forceRefresh: forceRefresh)

            // Update the published credits property
            await MainActor.run {
                hyperWhisperCloudManager.credits = fetchedCredits
            }

            // Update last refresh time on success
            lastRefreshTime = Date()
            errorMessage = nil
        } catch {
            // Handle different error types
            if let cloudError = error as? HyperWhisperCloudError {
                switch cloudError {
                case .insufficientCredits(let remaining, _):
                    errorMessage = String(format: "settings.credits.error.insufficient".localized, remaining)
                case .transientNetwork(let underlying):
                    let detail = String(describing: underlying)
                    errorMessage = String(format: "settings.credits.error.network".localized, detail)
                case .invalidResponse:
                    errorMessage = "settings.credits.error.invalidResponse".localized
                case .serverError(let message):
                    errorMessage = String(format: "settings.credits.error.server".localized, message)
                }
            } else {
                errorMessage = error.localizedDescription
            }
            showError = true
        }

        isLoading = false
    }

    // Date formatter for last update time
    private var timeFormatter: RelativeDateTimeFormatter {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .abbreviated
        return formatter
    }
}

// MARK: - Preview

#Preview {
    let licenseManager = LicenseManager()
    return CreditsSettingsSection()
        .environmentObject(HyperWhisperCloudManager(licenseManager: licenseManager))
        .environmentObject(licenseManager)
        .frame(width: 600)
        .padding()
}
