//
//  OnboardingSourceViews.swift
//  hyperwhisper
//
//  The four branching onboarding screens, in the "Focused Task" design:
//  choose a source, configure it, set it up, then check the microphone.
//  Configure and Setup each fork three ways (HyperWhisper Cloud / on this Mac /
//  your own API key), so these four screens cover eight of the flow's twelve
//  states.
//
//  Every card drives the app's EXISTING managers through the flow model's narrow
//  seams. No state, no policy, and no side effects live here: the views render
//  and forward intent, and the single write path to production state stays in
//  `OnboardingFlowModel` + `OnboardingLiveDependencies`.
//

import AppKit
import SwiftUI

// MARK: - Onboarding model selection

/// One curated on-device model offered during onboarding. Deliberately spans
/// BOTH local engines (Whisper + Parakeet) behind a single identity so that:
///   • the setup step downloads via the correct manager, and
///   • the default Mode's `model` field is set to exactly the string the
///     transcription router expects (`TranscriptionProviderRouter` keys off the
///     `parakeet-tdt-` prefix to pick the engine).
///
/// `id` is therefore the single source of truth and is written verbatim to
/// `Mode.model`: Whisper uses its short catalog name ("base", "large-v3_turbo");
/// Parakeet uses its full id ("parakeet-tdt-0.6b-v2").
struct OnboardingModelSelection: Identifiable, Equatable {
    enum Kind: Equatable { case whisper, parakeet }

    let id: String
    let kind: Kind
    let displayName: String
    let subtitleKey: String
    let size: String
    /// Speed / accuracy on a 1–5 scale. Values mirror the rating tables in
    /// `ModelLibraryManager` (`whisperRatings` / `parakeetRatings`), duplicated
    /// here because those tables are private to that manager.
    let speed: Int
    let accuracy: Int
    let isRecommended: Bool

    /// The curated onboarding shortlist: Parakeet V2 (recommended) + V3 plus two
    /// Whisper sizes. Sizes/availability are resolved from the live managers so
    /// they stay correct if the catalog changes.
    static func curated(
        whisper: WhisperModelManager,
        parakeet: ParakeetModelManager
    ) -> [OnboardingModelSelection] {
        func whisperSize(_ name: String) -> String {
            whisper.availableModels.first { $0.name == name }?.size ?? ""
        }
        return [
            OnboardingModelSelection(
                id: ParakeetModelManager.Constants.v2ModelId,
                kind: .parakeet,
                displayName: "Parakeet V2",
                subtitleKey: "onboarding.model.parakeetV2.subtitle",
                size: ParakeetModelManager.Constants.v2SizeDescription,
                speed: 5, accuracy: 3, isRecommended: true
            ),
            OnboardingModelSelection(
                id: ParakeetModelManager.Constants.v3ModelId,
                kind: .parakeet,
                displayName: "Parakeet V3",
                subtitleKey: "onboarding.model.parakeetV3.subtitle",
                size: ParakeetModelManager.Constants.v3SizeDescription,
                speed: 5, accuracy: 3, isRecommended: false
            ),
            OnboardingModelSelection(
                id: "base",
                kind: .whisper,
                displayName: "Whisper Base",
                subtitleKey: "onboarding.model.whisperBase.subtitle",
                size: whisperSize("base"),
                speed: 5, accuracy: 1, isRecommended: false
            ),
            OnboardingModelSelection(
                id: "large-v3_turbo",
                kind: .whisper,
                displayName: "Whisper Large v3 Turbo",
                subtitleKey: "onboarding.model.whisperTurbo.subtitle",
                size: whisperSize("large-v3_turbo"),
                speed: 4, accuracy: 3, isRecommended: false
            )
        ]
    }
}

// MARK: - Source metadata

/// Presentation spec for one source option. In the Focused Task design each
/// source is a single row sized card, so the decision reads as three doors
/// rather than a wall of feature bullets.
struct OnboardingSourceSpec: Identifiable {
    let source: TranscriptionSource
    let symbol: String
    let titleKey: String
    let descriptionKey: String

    var id: String { source.rawValue }

    /// HyperWhisper Cloud is offered first: it is the fastest path to a working
    /// first recording. Entitlement for it is enforced server side.
    static let all: [OnboardingSourceSpec] = [
        OnboardingSourceSpec(
            source: .hyperwhisperCloud,
            symbol: "cloud",
            titleKey: "onboarding.source.cloud.title",
            descriptionKey: "onboarding.source.cloud.description"
        ),
        OnboardingSourceSpec(
            source: .onDevice,
            symbol: "laptopcomputer",
            titleKey: "onboarding.source.onDevice.title",
            descriptionKey: "onboarding.source.onDevice.description"
        ),
        OnboardingSourceSpec(
            source: .yourProvider,
            symbol: "key",
            titleKey: "onboarding.source.provider.title",
            descriptionKey: "onboarding.source.provider.description"
        )
    ]
}

// MARK: - Step 3: Choose source

struct OnboardingSourcePicker: View {
    @ObservedObject var flow: OnboardingFlowModel

    var body: some View {
        OnboardingStepScaffold(
            symbol: "list.bullet",
            question: "onboarding.source.title".localized,
            detail: "onboarding.source.subtitle".localized
        ) {
            ForEach(OnboardingSourceSpec.all) { spec in
                option(spec)
            }

            OnboardingQuietNote(text: "onboarding.source.note".localized)
        }
    }

    private func option(_ spec: OnboardingSourceSpec) -> some View {
        let selected = flow.selectedSource == spec.source

        return Button {
            withAnimation(.easeInOut(duration: 0.15)) {
                flow.select(source: spec.source)
            }
        } label: {
            HStack(spacing: DesignConstants.Spacing.medium) {
                Image(systemName: spec.symbol)
                    .font(.system(size: 16))
                    .foregroundStyle(selected ? Color.accentColor : Color.secondary)
                    .frame(width: 20)

                OnboardingRowText(
                    title: spec.titleKey.localized,
                    caption: spec.descriptionKey.localized
                )

                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 16))
                    .foregroundStyle(Color.green)
                    .opacity(selected ? 1 : 0)
            }
            .padding(DesignConstants.Spacing.medium)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? OnboardingStyle.accentFill : nil)
            .background(.thinMaterial)
            .clipShape(RoundedRectangle(cornerRadius: OnboardingStyle.cardRadius, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: OnboardingStyle.cardRadius, style: .continuous)
                    .strokeBorder(selected ? OnboardingStyle.accentStroke : OnboardingStyle.hairline, lineWidth: 1)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(spec.titleKey.localized)
        .accessibilityValue((selected ? "onboarding.a11y.selected" : "onboarding.a11y.notSelected").localized)
        .accessibilityHint(spec.descriptionKey.localized)
    }
}

// MARK: - Step 4: Configure (branches per source)

struct OnboardingConfigureView: View {
    /// BYOK providers offered during onboarding. Deliberately excludes
    /// `.hyperwhisper` (that is the Cloud branch, not a bring your own key one)
    /// and the two providers whose health probe short circuits to `.healthy`
    /// without an API key, which would otherwise open the gate on a fake pass.
    static let onboardingProviders: [CloudProvider] = CloudProvider.allCases.filter {
        $0 != .hyperwhisper
            && $0 != .microsoftAzureSpeech
            && $0 != .googleSpeech
            && ($0 != .meta || CloudTranscriptionModels.isMetaBYOKCatalogEnabled)
    }

    @ObservedObject var flow: OnboardingFlowModel
    @EnvironmentObject private var hyperWhisperCloudManager: HyperWhisperCloudManager

    var body: some View {
        OnboardingStepScaffold(symbol: symbol, question: question, detail: detail) {
            switch flow.selectedSource {
            case .hyperwhisperCloud:
                cloudCard
                OnboardingQuietNote(text: "onboarding.configure.cloud.note".localized)
            case .onDevice:
                modelCard
                OnboardingQuietNote(text: "onboarding.configure.onDevice.note".localized)
            case .yourProvider:
                providerCard
                OnboardingQuietNote(
                    text: "onboarding.configure.provider.keychainNote".localized,
                    symbol: "lock"
                )
            case nil:
                noSourceCard
            }
        }
        // Clears the inline test result so a pass for a previous key cannot be
        // read as a pass for whatever is in the field now. The Continue gate does
        // NOT depend on this alone: an already stored provider key keeps the gate
        // open on a return visit (see `OnboardingFlowModel.canContinue`).
        .onAppear { flow.resetConfigureTestResults() }
    }

    private var symbol: String {
        switch flow.selectedSource {
        case .hyperwhisperCloud: return "key"
        case .onDevice: return "laptopcomputer"
        case .yourProvider: return "key"
        case nil: return "questionmark"
        }
    }

    private var question: String {
        switch flow.selectedSource {
        case .hyperwhisperCloud: return "onboarding.configure.cloud.title".localized
        case .onDevice: return "onboarding.configure.onDevice.title".localized
        case .yourProvider: return "onboarding.configure.provider.title".localized
        case nil: return "onboarding.configure.noSource.title".localized
        }
    }

    private var detail: String {
        switch flow.selectedSource {
        case .hyperwhisperCloud: return "onboarding.configure.cloud.subtitle".localized
        case .onDevice: return "onboarding.configure.onDevice.subtitle".localized
        case .yourProvider: return "onboarding.configure.provider.subtitle".localized
        case nil: return "onboarding.noSource.detail".localized
        }
    }

    private var noSourceCard: some View {
        OnboardingCard {
            OnboardingCardRow {
                OnboardingRowText(
                    title: "onboarding.setup.selectFirst".localized,
                    caption: "onboarding.configure.noSource.caption".localized
                )
            }
        }
    }

    // MARK: HyperWhisper Cloud, access key + read only test

    private var cloudCard: some View {
        OnboardingCard {
            OnboardingCardRow {
                OnboardingKeyField(
                    placeholder: "onboarding.configure.cloud.placeholder".localized,
                    text: $flow.licenseKeyInput
                )
                Button("onboarding.configure.cloud.testKey".localized, action: flow.testAccessKey)
                    .buttonStyle(.bordered)
                    .disabled(flow.isTestingKey
                              || flow.licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }

            if flow.isTestingKey {
                OnboardingCardDivider()
                OnboardingCardRow {
                    ProgressView().scaleEffect(0.5).frame(width: 16, height: 16)
                    OnboardingRowText(title: "onboarding.configure.test.testing".localized)
                }
            } else if flow.licenseTestPassed == true {
                OnboardingCardDivider()
                OnboardingCardRow {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 16))
                        .foregroundStyle(Color.green)
                    OnboardingRowText(title: "onboarding.configure.test.valid".localized)
                }

                if let credits = hyperWhisperCloudManager.credits {
                    OnboardingCardDivider()
                    OnboardingCardRow {
                        OnboardingBigNumber(
                            value: creditsLabel(credits),
                            caption: "onboarding.configure.cloud.creditsCaption".localized
                        )
                            .frame(maxWidth: .infinity, alignment: .leading)
                        Button("onboarding.configure.cloud.getCredits".localized) {
                            openCreditsPage()
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                    }
                }
            } else if flow.licenseTestPassed == false, let error = flow.setupErrorMessage {
                OnboardingCardDivider()
                OnboardingCardBlock {
                    OnboardingErrorNote(text: error)
                }
            }
        }
        // Credits are read only here and only meaningful once a key resolves to
        // an account. The key is verified but not yet activated at this point, so
        // the manager's default identity is still the anonymous device id and the
        // balance has to be fetched for the tested key explicitly.
        // The task is cancelled by SwiftUI when the step goes away.
        .task(id: flow.licenseTestPassed) {
            guard flow.licenseTestPassed == true else { return }
            let trimmedKey = flow.licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmedKey.isEmpty else { return }
            await hyperWhisperCloudManager.refreshCredits(identifierOverride: trimmedKey)
        }
    }

    private func creditsLabel(_ credits: HyperWhisperCloudCredits) -> String {
        OnboardingFlowContainer.creditsFormatter
            .string(from: NSNumber(value: credits.creditsRemaining)) ?? "\(Int(credits.creditsRemaining))"
    }

    private func openCreditsPage() {
        guard let url = URL(string: "https://hyperwhisper.com") else { return }
        NSWorkspace.shared.open(url)
    }

    // MARK: On this Mac, pick a model

    private var modelCard: some View {
        OnboardingCard {
            ForEach(Array(flow.availableModels.enumerated()), id: \.element.id) { index, model in
                if index > 0 {
                    OnboardingCardDivider()
                }
                modelRow(model)
            }
        }
    }

    private func modelRow(_ model: OnboardingModelSelection) -> some View {
        let selected = flow.selectedModel?.id == model.id
        let installed = flow.isInstalled(model)

        return Button {
            flow.select(model: model)
        } label: {
            HStack(spacing: DesignConstants.Spacing.medium) {
                OnboardingRadioMark(selected: selected)

                OnboardingRowText(
                    title: model.displayName,
                    caption: model.subtitleKey.localized
                )

                if installed {
                    OnboardingStatusPill(
                        text: "onboarding.model.downloaded".localized,
                        symbol: "checkmark",
                        tone: .good
                    )
                } else {
                    Text(model.size)
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            .padding(DesignConstants.Spacing.medium)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? OnboardingStyle.accentFill : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(model.displayName)
        .accessibilityValue(
            (selected ? "onboarding.a11y.selectedDetail" : "onboarding.a11y.notSelectedDetail")
                .localized(arguments: model.size)
        )
    }

    // MARK: Your own API key, provider chips + key + test

    private var providerCard: some View {
        OnboardingCard {
            OnboardingCardBlock {
                OnboardingFlowLayout(spacing: DesignConstants.Spacing.small) {
                    ForEach(Self.onboardingProviders) { provider in
                        OnboardingChip(
                            label: provider.displayName,
                            selected: flow.selectedProvider == provider
                        ) {
                            // Changing the provider clears the entered key on the
                            // flow model, so a masked, stale key can never be
                            // saved under a provider it was not typed for.
                            flow.select(provider: provider)
                        }
                    }
                }
            }

            OnboardingCardDivider()

            OnboardingCardRow {
                OnboardingKeyField(
                    placeholder: "onboarding.configure.provider.keyPlaceholder".localized,
                    text: $flow.apiKeyInput,
                    secure: true
                )
                Button("onboarding.configure.provider.testKey".localized, action: flow.testProviderKey)
                    .buttonStyle(.bordered)
                    .disabled(flow.isTestingKey
                              || flow.apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }

            providerTestResult
        }
    }

    @ViewBuilder
    private var providerTestResult: some View {
        if flow.isTestingKey {
            OnboardingCardDivider()
            OnboardingCardRow {
                ProgressView().scaleEffect(0.5).frame(width: 16, height: 16)
                OnboardingRowText(title: "onboarding.configure.test.testing".localized)
            }
        } else if flow.providerTestHealth == nil, let error = flow.setupErrorMessage {
            OnboardingCardDivider()
            OnboardingCardBlock {
                OnboardingErrorNote(text: error)
            }
        } else if let health = flow.providerTestHealth {
            switch health {
            case .healthy:
                OnboardingCardDivider()
                OnboardingCardRow {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 16))
                        .foregroundStyle(Color.green)
                    OnboardingRowText(title: "onboarding.configure.test.healthy".localized)
                }
            case .unauthorized:
                OnboardingCardDivider()
                OnboardingCardBlock {
                    OnboardingErrorNote(text: "onboarding.configure.test.unauthorized".localized)
                }
            case .unreachable:
                OnboardingCardDivider()
                OnboardingCardBlock {
                    OnboardingErrorNote(text: "onboarding.configure.test.unreachable".localized)
                }
            case .unknown, .checking, .notInstalled:
                EmptyView()
            }
        }
    }
}

// MARK: - Step 5: Set up (perform the action)

struct OnboardingSetupView: View {
    @ObservedObject var flow: OnboardingFlowModel
    @EnvironmentObject private var hyperWhisperCloudManager: HyperWhisperCloudManager

    var body: some View {
        OnboardingStepScaffold(symbol: symbol, question: question, detail: detail) {
            switch flow.selectedSource {
            case .hyperwhisperCloud:
                cloudCard
                OnboardingQuietNote(text: "onboarding.setup.cloud.note".localized)
            case .onDevice:
                downloadCard
                OnboardingQuietNote(text: "onboarding.setup.onDevice.note".localized)
            case .yourProvider:
                keychainCard
                OnboardingQuietNote(text: "onboarding.setup.provider.note".localized)
            case nil:
                OnboardingCard {
                    OnboardingCardRow {
                        OnboardingRowText(
                            title: "onboarding.setup.selectFirst".localized,
                            caption: "onboarding.setup.noSource.caption".localized
                        )
                    }
                }
            }
        }
    }

    private var symbol: String {
        switch flow.selectedSource {
        case .hyperwhisperCloud: return "cloud"
        case .onDevice: return "arrow.down.circle"
        case .yourProvider: return "lock"
        case nil: return "ellipsis"
        }
    }

    private var question: String {
        switch flow.selectedSource {
        case .hyperwhisperCloud: return "onboarding.setup.cloud.title".localized
        case .onDevice: return "onboarding.setup.onDevice.title".localized
        case .yourProvider: return "onboarding.setup.provider.title".localized
        case nil: return "onboarding.setup.noSource.title".localized
        }
    }

    private var detail: String {
        switch flow.selectedSource {
        case .hyperwhisperCloud:
            return "onboarding.setup.cloud.subtitle".localized
        case .onDevice:
            return "onboarding.setup.onDevice.subtitle".localized
        case .yourProvider:
            return "onboarding.setup.provider.subtitle".localized
        case nil:
            return "onboarding.noSource.detail".localized
        }
    }

    // MARK: HyperWhisper Cloud, activate

    private var cloudCard: some View {
        let active = flow.isSelectedSourceUsable

        return OnboardingCard {
            OnboardingCheckLine(
                text: "onboarding.setup.cloud.check.keyVerified".localized,
                done: flow.keyValidated || active
            )
            OnboardingCardDivider()
            OnboardingCheckLine(
                text: "onboarding.setup.cloud.check.activated".localized,
                done: active
            )
            OnboardingCardDivider()
            OnboardingCheckLine(
                text: "onboarding.setup.cloud.check.creditsConfirmed".localized,
                done: active && hyperWhisperCloudManager.credits != nil
            )
            OnboardingCardDivider()

            if active {
                OnboardingCardRow {
                    OnboardingBigNumber(
                        value: creditsValue,
                        caption: "onboarding.setup.cloud.credits.caption".localized
                    )
                    .frame(maxWidth: .infinity, alignment: .leading)
                    OnboardingStatusPill(text: "onboarding.setup.cloud.active".localized, tone: .accent)
                }
            } else {
                OnboardingCardBlock {
                    // Bug 3: the activation task is owned and cancelled by the flow
                    // model, so a late completion cannot write state after dismissal.
                    // Entitlement itself is enforced server side.
                    Button(action: flow.activateCloudLicense) {
                        HStack(spacing: DesignConstants.Spacing.small) {
                            if flow.isActivatingLicense {
                                ProgressView().scaleEffect(0.5).frame(width: 14, height: 14)
                                Text("onboarding.setup.cloud.activating".localized)
                            } else {
                                Image(systemName: "checkmark.seal.fill")
                                Text("onboarding.setup.cloud.activate".localized)
                            }
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(flow.isActivatingLicense
                              || flow.licenseKeyInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                    if let error = flow.setupErrorMessage {
                        OnboardingErrorNote(text: "onboarding.setup.cloud.error".localized(arguments: error))
                            .padding(.top, DesignConstants.Spacing.medium)
                    }
                }
            }
        }
        .task(id: flow.isSelectedSourceUsable) {
            guard flow.isSelectedSourceUsable else { return }
            await hyperWhisperCloudManager.refreshCredits()
        }
    }

    private var creditsValue: String {
        guard let credits = hyperWhisperCloudManager.credits else { return "\u{2026}" }
        return OnboardingFlowContainer.creditsFormatter
            .string(from: NSNumber(value: credits.creditsRemaining)) ?? "\(Int(credits.creditsRemaining))"
    }

    // MARK: On this Mac, download (routes per engine)

    @ViewBuilder
    private var downloadCard: some View {
        if let model = flow.selectedModel {
            let ready = flow.isSelectedModelInstalled()
            let downloading = flow.isSelectedModelDownloading()
            let progress = flow.selectedModelProgress()

            OnboardingCard {
                OnboardingCardRow {
                    Image(systemName: "laptopcomputer")
                        .font(.system(size: 16))
                        .foregroundStyle(.secondary)
                        .frame(width: 20)
                    OnboardingRowText(
                        title: model.displayName,
                        caption: model.subtitleKey.localized
                    )
                    if ready {
                        OnboardingStatusPill(
                            text: "onboarding.setup.onDevice.ready".localized,
                            symbol: "checkmark",
                            tone: .good
                        )
                    } else if downloading {
                        OnboardingStatusPill(
                            text: "onboarding.setup.onDevice.downloading".localized(arguments: Int(progress * 100)),
                            tone: .accent
                        )
                    } else {
                        Text(model.size)
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(.secondary)
                    }
                }

                OnboardingCardDivider()

                OnboardingCardBlock {
                    if ready {
                        OnboardingBigNumber(
                            value: "100%",
                            caption: "onboarding.setup.onDevice.storedCaption".localized
                        )
                    } else if downloading {
                        Text("\(Int(progress * 100))%")
                            .font(.system(size: 30, weight: .semibold, design: .rounded))
                            .monospacedDigit()

                        OnboardingProgressBar(value: progress)
                            .padding(.top, DesignConstants.Spacing.medium)

                        // The managers publish a fraction, not bytes or a rate, so
                        // the reference's "409 MB of 620 MB" and "40s left" are
                        // reduced to the one figure that is actually known.
                        OnboardingBigNumber(
                            value: model.size,
                            caption: "onboarding.setup.onDevice.totalCaption".localized,
                            compact: true
                        )
                            .padding(.top, DesignConstants.Spacing.medium)
                    } else {
                        Button(action: flow.startSelectedModelDownload) {
                            Label(
                                "onboarding.setup.onDevice.download".localized(arguments: model.displayName),
                                systemImage: "arrow.down.circle.fill"
                            )
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                    }

                    // Bug 2: a failed download is surfaced for BOTH engines, in
                    // every branch, so nobody is parked at the mandatory gate with
                    // no explanation. The message is framed by localized copy
                    // because the managers report in hardcoded English.
                    if let error = flow.setupErrorMessage {
                        OnboardingErrorNote(text: "onboarding.setup.onDevice.error".localized(arguments: error))
                            .padding(.top, DesignConstants.Spacing.medium)
                    }
                }
            }
        } else {
            OnboardingCard {
                OnboardingCardRow {
                    OnboardingRowText(title: "onboarding.setup.selectFirst".localized)
                }
            }
        }
    }

    // MARK: Your own API key, save + verify

    private var keychainCard: some View {
        let saved = flow.isSelectedSourceUsable

        return OnboardingCard {
            OnboardingCheckLine(
                text: "onboarding.setup.provider.check.validated"
                    .localized(arguments: flow.selectedProvider.displayName),
                done: flow.keyValidated || saved
            )
            OnboardingCardDivider()
            OnboardingCheckLine(
                text: "onboarding.setup.provider.check.written".localized,
                done: saved
            )
            OnboardingCardDivider()

            if saved {
                OnboardingCardRow {
                    Image(systemName: "lock")
                        .font(.system(size: 16))
                        .foregroundStyle(.secondary)
                        .frame(width: 20)
                    OnboardingRowText(
                        title: "onboarding.setup.provider.keychainItem"
                            .localized(arguments: flow.selectedProvider.displayName),
                        caption: maskedKey,
                        singleLine: true
                    )
                    OnboardingStatusPill(
                        text: "onboarding.setup.provider.saved".localized,
                        symbol: "checkmark",
                        tone: .good
                    )
                }
            } else {
                OnboardingCardBlock {
                    Button(action: flow.saveProviderKey) {
                        Label("onboarding.setup.provider.save".localized, systemImage: "lock.fill")
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(flow.apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                    if let error = flow.setupErrorMessage {
                        OnboardingErrorNote(text: "onboarding.setup.provider.error".localized(arguments: error))
                            .padding(.top, DesignConstants.Spacing.medium)
                    }
                }
            }
        }
    }

    /// Never renders the whole key, even on the machine that just typed it.
    private var maskedKey: String {
        let key = flow.apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard key.count > 12 else { return "onboarding.setup.provider.keyHidden".localized }
        return "\(key.prefix(8))\u{2026}\(key.suffix(4))"
    }
}

// MARK: - Step 6: Microphone (device + live level)

/// Lets the user pick their input device and confirm it registers a live level,
/// before the "try it" step. The meter is driven by the dedicated idle metering
/// session on `AudioRecordingManager` (`startInputLevelPreview`), started on
/// appear and torn down on disappear so the mic is never held open past this
/// screen.
struct OnboardingMicrophoneView: View {
    @ObservedObject var flow: OnboardingFlowModel
    /// Only for the live level, which is published straight off the manager.
    @EnvironmentObject var audioManager: AudioRecordingManager

    var body: some View {
        OnboardingStepScaffold(
            symbol: "mic",
            question: "onboarding.mic.title".localized,
            detail: "onboarding.mic.subtitle".localized
        ) {
            OnboardingCard {
                OnboardingCardBlock {
                    OnboardingLevelMeter(
                        level: audioManager.idleInputLevel,
                        active: flow.hasMicrophonePermission
                    )
                }

                // "System Default" is always the first option (the flow model
                // puts it there), so there is no separate toggle to keep in sync.
                ForEach(flow.deviceOptions) { device in
                    OnboardingCardDivider()
                    deviceRow(device)
                }

                OnboardingCardDivider()

                OnboardingCardRow {
                    Text((flow.hasMicrophonePermission
                          ? "onboarding.mic.levelHint"
                          : "onboarding.mic.permissionHint").localized)
                        .font(.system(size: 12))
                        .foregroundStyle(flow.hasMicrophonePermission ? Color.secondary : Color.orange)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    Button {
                        openSoundSettings()
                    } label: {
                        Label("onboarding.mic.soundSettings".localized, systemImage: "speaker.wave.2")
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.regular)
                }
            }

            OnboardingQuietNote(text: "onboarding.mic.note".localized)
        }
        .onAppear { flow.beginMicrophoneStep() }
        .onDisappear { flow.endMicrophoneStep() }
    }

    private func deviceRow(_ device: OnboardingInputDevice) -> some View {
        let selected = flow.selectedDeviceID == device.id

        return Button {
            flow.selectDevice(id: device.id)
        } label: {
            HStack(spacing: DesignConstants.Spacing.medium) {
                OnboardingRadioMark(selected: selected)

                // The input device name always stays on one line.
                Text(device.name)
                    .font(.system(size: 14, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: .infinity, alignment: .leading)

                Text(detail(for: device))
                    .font(.system(size: 12))
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .layoutPriority(1)
            }
            .padding(DesignConstants.Spacing.medium)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? OnboardingStyle.accentFill : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(device.name)
        .accessibilityValue((selected ? "onboarding.a11y.selectedInput" : "onboarding.a11y.notSelected").localized)
    }

    /// The trailing label on each device row. The synthetic first row names the
    /// device macOS is actually using, so "System Default" is never a mystery.
    private func detail(for device: OnboardingInputDevice) -> String {
        if device.isSystemDefault {
            let resolved = audioManager.activeInputDeviceName
            return resolved.isEmpty ? "onboarding.mic.device.followsSystem".localized : resolved
        }
        return device.id == audioManager.activeInputDeviceIdentifier
            ? "onboarding.mic.device.inUse".localized
            : ""
    }

    private func openSoundSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.sound") else { return }
        NSWorkspace.shared.open(url)
    }
}
