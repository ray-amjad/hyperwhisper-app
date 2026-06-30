//
//  CloudAccountSettingsSection.swift
//  hyperwhisper
//
//  COMBINED LICENSE + HYPERWHISPER CLOUD CREDITS SECTION
//  One panel that holds the credit balance (the wallet) and the license key
//  that owns it. The license key IS the wallet — top-ups refill the same key.
//
//  - Licensed users see their balance, account info, masked license key, and a
//    single "Purchase More" button that opens the web /credits page.
//  - Users without an active license see a key-entry block plus a "Get Credits"
//    button that also opens /credits (buying credits mints + emails a key).
//
//  Replaces the previously separate CreditsSettingsSection + LicenseSettingsSection.
//

import SwiftUI
import AppKit

struct CloudAccountSettingsSection: View {
    @EnvironmentObject var hyperWhisperCloudManager: HyperWhisperCloudManager
    @EnvironmentObject var licenseManager: LicenseManager

    // Credits loading / error state
    @State private var isLoading = false
    @State private var errorMessage: String?

    // License entry state (unlicensed branch)
    @State private var licenseKeyInput: String = ""
    @State private var showLicenseSuccess = false
    @State private var showLicenseError = false

    // License key reveal / copy state (licensed branch)
    @State private var revealKey = false
    @State private var didCopyKey = false

    private var isLicensed: Bool { licenseManager.licenseStatus == .active }

    var body: some View {
        SettingsSection(title: LocalizedStringKey("settings.credits.title")) {
            if isLicensed { refreshButton }
        } content: {
            if isLicensed {
                licensedContent
            } else {
                unlicensedContent
            }
        }
        .onAppear {
            if isLicensed {
                Task { await fetchCredits(forceRefresh: true) }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .licenseStatusChanged)) { _ in
            // Identifier changes when the license is activated/deactivated, which
            // means a different credit pool — refetch so the balance stays correct.
            revealKey = false
            if isLicensed {
                Task { await fetchCredits(forceRefresh: true) }
            }
        }
        .alert(licenseManager.licenseStatus == .active ? LocalizedStringKey("alerts.license.activated.title") : LocalizedStringKey("alerts.license.deactivated.title"), isPresented: $showLicenseSuccess) {
            Button(LocalizedStringKey("common.ok")) { }
        } message: {
            if licenseManager.licenseStatus == .active {
                Text(LocalizedStringKey("alerts.license.activated.message"))
            } else {
                Text(LocalizedStringKey("alerts.license.deactivated.message"))
            }
        }
        .alert(LocalizedStringKey("alerts.license.failed.title"), isPresented: $showLicenseError) {
            Button(LocalizedStringKey("common.ok")) { }
        } message: {
            Text(licenseManager.lastError ?? "alerts.license.failed.message".localized)
        }
    }

    // MARK: - Licensed branch

    @ViewBuilder
    private var licensedContent: some View {
        if isLoading && hyperWhisperCloudManager.credits == nil {
            loadingView
        } else if let credits = hyperWhisperCloudManager.credits {
            VStack(spacing: 20) {
                balanceCard(credits)
                accountInfoCard(credits)
                actionRow
            }
        } else if let error = errorMessage {
            VStack(spacing: 20) {
                errorView(error)
                accountInfoCard(nil)
                actionRow
            }
        } else {
            VStack(spacing: 20) {
                emptyStateView
                accountInfoCard(nil)
                actionRow
            }
        }
    }

    // MARK: - Unlicensed branch

    private var unlicensedContent: some View {
        VStack(spacing: 20) {
            GroupBox {
                VStack(alignment: .leading, spacing: 16) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(LocalizedStringKey("settings.cloud.activate.title"))
                            .font(.headline)
                        Text(LocalizedStringKey("settings.cloud.activate.subtitle"))
                            .font(.callout)
                            .foregroundColor(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    Divider()

                    // License key entry
                    VStack(alignment: .leading, spacing: 12) {
                        Text(LocalizedStringKey("settings.license.key.title"))
                            .font(.subheadline)
                            .foregroundColor(.secondary)
                        TextField(LocalizedStringKey("settings.license.placeholder"), text: $licenseKeyInput)
                            .textFieldStyle(.roundedBorder)
                            .font(.system(.body, design: .monospaced))
                        HStack(spacing: 12) {
                            Button(action: pasteOrClearInput) {
                                if licenseKeyInput.isEmpty {
                                    Label("settings.license.pasteFromClipboard", systemImage: "doc.on.clipboard")
                                } else {
                                    Label("settings.license.clearInput", systemImage: "xmark.circle")
                                }
                            }
                            .buttonStyle(.bordered)

                            activateLicenseButton
                            Spacer()
                        }
                    }
                }
                .padding(10)
            }

            // Get credits (mints a key) → web /credits page
            GroupBox {
                HStack(spacing: 12) {
                    Image(systemName: "sparkles")
                        .foregroundColor(.accentColor)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(LocalizedStringKey("settings.cloud.getCredits.title"))
                            .font(.callout)
                            .fontWeight(.medium)
                        Text(LocalizedStringKey("settings.cloud.getCredits.subtitle"))
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                    Spacer()
                    Button {
                        openCreditsPage()
                    } label: {
                        Text(LocalizedStringKey("settings.cloud.getCredits.button"))
                    }
                    .buttonStyle(.borderedProminent)
                }
                .padding(10)
            }

            // Recovery
            HStack {
                Image(systemName: "questionmark.circle")
                    .foregroundColor(.secondary)
                Text(LocalizedStringKey("settings.license.recovery.prompt"))
                    .font(.caption)
                    .foregroundColor(.secondary)
                Spacer()
                Button(LocalizedStringKey("settings.license.recovery.button")) {
                    if let url = URL(string: "\(NetworkConfig.baseURL)/user") {
                        NSWorkspace.shared.open(url)
                    }
                }
                .buttonStyle(.bordered)
            }
            .padding(.horizontal, 4)

            if let error = licenseManager.lastError {
                licenseErrorBanner(message: error)
            }
        }
    }

    // MARK: - Cards (licensed)

    private func balanceCard(_ credits: HyperWhisperCloudCredits) -> some View {
        let remainingCreditsValue = String(format: "%.1f", credits.creditsRemaining)
        let currencyValue = credits.creditsRemaining / 1000.0

        return GroupBox {
            VStack(alignment: .leading, spacing: 20) {
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

                HStack(spacing: 4) {
                    Image(systemName: "circle.fill")
                        .font(.system(size: 6))
                        .foregroundColor(.secondary)
                    Text(String(format: "settings.credits.creditsRemaining".localized, remainingCreditsValue))
                        .font(.callout)
                        .foregroundColor(.secondary)

                    Spacer()

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

                if credits.isLow && !credits.isExhausted {
                    warningBanner(
                        icon: "exclamationmark.triangle.fill",
                        color: .orange,
                        message: "settings.credits.warning.low".localized
                    )
                }

                if credits.isExhausted {
                    warningBanner(
                        icon: "exclamationmark.octagon.fill",
                        color: .red,
                        message: "settings.credits.warning.exhausted".localized
                    )
                }
            }
            .padding(8)
        }
    }

    private func accountInfoCard(_ credits: HyperWhisperCloudCredits?) -> some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 12) {
                Label(LocalizedStringKey("settings.credits.accountInfo"), systemImage: "info.circle.fill")
                    .font(.headline)
                    .foregroundColor(.secondary)

                Divider()

                if let credits = credits {
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
                            value: "settings.credits.accountType.licensed".localized,
                            icon: "checkmark.seal.fill",
                            color: .green
                        )
                    }

                    Divider()
                }

                // License key (the wallet) — masked, reveal + copy
                licenseKeyRow
            }
            .padding(4)
        }
    }

    private var licenseKeyRow: some View {
        let (key, _) = licenseManager.getTranscriptionIdentifier()
        return HStack(spacing: 12) {
            Image(systemName: "key.fill")
                .font(.title2)
                .foregroundColor(.blue)
                .frame(width: 30)

            VStack(alignment: .leading, spacing: 2) {
                Text(LocalizedStringKey("settings.cloud.licenseKey"))
                    .font(.caption)
                    .foregroundColor(.secondary)
                Text(revealKey ? key : maskedKey(key))
                    .font(.system(.body, design: .monospaced))
                    .fontWeight(.medium)
                    .textSelection(.enabled)
            }

            Spacer()

            Button {
                revealKey.toggle()
            } label: {
                Image(systemName: revealKey ? "eye.slash" : "eye")
            }
            .buttonStyle(.borderless)
            .help(LocalizedStringKey(revealKey ? "settings.cloud.key.hide" : "settings.cloud.key.reveal"))

            Button {
                copyKey(key)
            } label: {
                Image(systemName: didCopyKey ? "checkmark" : "doc.on.doc")
                    .foregroundColor(didCopyKey ? .green : .primary)
            }
            .buttonStyle(.borderless)
            .help(LocalizedStringKey("settings.cloud.key.copy"))
        }
    }

    private var actionRow: some View {
        HStack(spacing: 12) {
            Button {
                openCreditsPage()
            } label: {
                HStack {
                    Image(systemName: "creditcard.fill")
                    Text(LocalizedStringKey("settings.cloud.purchaseMore"))
                }
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)

            Button(LocalizedStringKey(licenseManager.isDeactivating ? "license.button.deactivating" : "license.button.deactivate")) {
                Task {
                    let success = await licenseManager.deactivateLicense()
                    showLicenseSuccess = success
                    showLicenseError = !success
                }
            }
            .buttonStyle(.bordered)
            .controlSize(.large)
            .disabled(licenseManager.isDeactivating)

            Button(LocalizedStringKey("license.button.manageBilling")) {
                licenseManager.openCustomerPortal()
            }
            .buttonStyle(.bordered)
            .controlSize(.large)

            Spacer()
        }
    }

    // MARK: - Shared row builders

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

    private func licenseErrorBanner(message: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle")
                .foregroundColor(.red)
                .font(.caption)
            Text(message)
                .font(.caption)
                .foregroundColor(.red)
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
        }
        .padding(.horizontal, 4)
    }

    // MARK: - State views

    private var refreshButton: some View {
        Button {
            Task { await fetchCredits(forceRefresh: true) }
        } label: {
            Label(LocalizedStringKey("settings.credits.refresh"), systemImage: "arrow.clockwise")
                .labelStyle(.iconOnly)
        }
        .buttonStyle(.borderless)
        .disabled(isLoading)
    }

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
                    Task { await fetchCredits(forceRefresh: true) }
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
                    Task { await fetchCredits() }
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.regular)
            }
            .frame(maxWidth: .infinity)
            .padding(20)
        }
    }

    // MARK: - Actions

    private var activateLicenseButton: some View {
        let titleKey = licenseManager.isValidating ? "license.button.activating" : "license.button.activate"
        return Button(LocalizedStringKey(titleKey)) {
            Task {
                let result = await licenseManager.activateLicense(licenseKeyInput)
                if result.isValid {
                    showLicenseSuccess = true
                    licenseKeyInput = ""
                } else {
                    showLicenseError = true
                }
            }
        }
        .buttonStyle(.borderedProminent)
        .disabled(licenseKeyInput.isEmpty || licenseManager.isValidating)
    }

    private func pasteOrClearInput() {
        if licenseKeyInput.isEmpty {
            if let clipboardContent = NSPasteboard.general.string(forType: .string) {
                licenseKeyInput = clipboardContent.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
            }
        } else {
            licenseKeyInput = ""
        }
    }

    /// Opens the web credits page — the single "purchase more" path.
    /// No in-app tier picker; the web page handles amounts and minting/emailing the key.
    private func openCreditsPage() {
        let (identifier, _) = licenseManager.getTranscriptionIdentifier()
        if let url = URL(string: "\(NetworkConfig.baseURL)/credits?license_key=\(identifier)") {
            NSWorkspace.shared.open(url)
        }
    }

    private func copyKey(_ key: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(key, forType: .string)
        didCopyKey = true
        Task {
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            await MainActor.run { didCopyKey = false }
        }
    }

    /// Masks every alphanumeric character with a bullet while keeping dashes,
    /// e.g. `HW-7F3K-9QXM` → `••-••••-••••`.
    private func maskedKey(_ key: String) -> String {
        String(key.map { $0 == "-" ? "-" : "•" })
    }

    private func fetchCredits(forceRefresh: Bool = false) async {
        guard !isLoading else { return }

        isLoading = true
        errorMessage = nil

        do {
            let fetchedCredits = try await hyperWhisperCloudManager.fetchCredits(forceRefresh: forceRefresh)
            await MainActor.run {
                hyperWhisperCloudManager.credits = fetchedCredits
            }
            errorMessage = nil
        } catch {
            if let cloudError = error as? HyperWhisperCloudError {
                switch cloudError {
                case .insufficientCredits(let remaining, _):
                    errorMessage = String(format: "settings.credits.error.insufficient".localized, remaining)
                case .transientNetwork(let underlying):
                    errorMessage = String(format: "settings.credits.error.network".localized, String(describing: underlying))
                case .invalidResponse:
                    errorMessage = "settings.credits.error.invalidResponse".localized
                case .serverError(let message):
                    errorMessage = String(format: "settings.credits.error.server".localized, message)
                }
            } else {
                errorMessage = error.localizedDescription
            }
        }

        isLoading = false
    }
}

// MARK: - Preview

#Preview {
    let licenseManager = LicenseManager()
    return CloudAccountSettingsSection()
        .environmentObject(HyperWhisperCloudManager(licenseManager: licenseManager))
        .environmentObject(licenseManager)
        .frame(width: 600)
        .padding()
}
