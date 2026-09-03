//
//  OnboardingView.swift
//  hyperwhisper
//
//  ONBOARDING FLOW, "FOCUSED TASK" DESIGN
//  One decision per screen, said out loud. Eight steps: welcome, permissions,
//  choose source, configure, set up, microphone, try it, done. Each step is an
//  anchor glyph, a question, one supporting line, and a single cluster of cards,
//  inside a 760 x 580 sheet with an eight segment progress hairline flush at the
//  top edge and a footer carrying Back, the reassurance line, and one primary.
//
//  All step gating, staging, validation, task ownership, and rollback live in
//  `Onboarding/OnboardingFlowModel.swift`. This file is presentation only.
//  The shared design parts live in `Onboarding/OnboardingFocusComponents.swift`;
//  the source picker and the per source Configure / Setup / Microphone screens
//  live in `Onboarding/OnboardingSourceViews.swift`.
//

import SwiftUI
import KeyboardShortcuts

// MARK: - Main Onboarding View

/// Eight step onboarding flow for first time users. Builds the flow model from
/// the app's real managers and hands it to the container, which owns it for the
/// lifetime of the sheet.
struct OnboardingView: View {
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var transcriptionPipeline: TranscriptionPipeline
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var whisperModelManager: WhisperModelManager
    @EnvironmentObject var parakeetModelManager: ParakeetModelManager
    @EnvironmentObject var licenseManager: LicenseManager
    @EnvironmentObject var cloudProviderHealthManager: CloudProviderHealthManager

    @Binding var isPresented: Bool

    var body: some View {
        OnboardingFlowContainer(
            isPresented: $isPresented,
            // @autoclosure: evaluated exactly once, when the StateObject is first
            // created, not on every body pass.
            makeModel: OnboardingFlowModel.live(
                appState: appState,
                audioManager: audioManager,
                settingsManager: settingsManager,
                whisperModelManager: whisperModelManager,
                parakeetModelManager: parakeetModelManager,
                licenseManager: licenseManager,
                cloudHealth: cloudProviderHealthManager
            )
        )
    }
}

// MARK: - Container (owns the flow model)

struct OnboardingFlowContainer: View {
    @Binding var isPresented: Bool
    @StateObject private var flow: OnboardingFlowModel

    /// Only for the credits figure on the summary. Injected at the sheet.
    @EnvironmentObject private var hyperWhisperCloudManager: HyperWhisperCloudManager

    init(isPresented: Binding<Bool>, makeModel: @autoclosure @escaping () -> OnboardingFlowModel) {
        self._isPresented = isPresented
        self._flow = StateObject(wrappedValue: makeModel())
    }

    private var totalSteps: Int { OnboardingStep.allCases.count }

    var body: some View {
        VStack(spacing: 0) {
            // First in the stack with no inset, so the hairline sits flush at
            // y = 0. A sheet has no title bar, so nothing has to be hidden.
            OnboardingStepProgress(current: flow.step.rawValue, total: totalSteps)

            // The stage is scrollable but not visibly so: `minHeight` keeps every
            // step laid out exactly as designed, vertically centred by its own
            // Spacers, and the ScrollView only engages once real content exceeds
            // the 580pt sheet. A Mac with several virtual inputs listed on the
            // microphone step, or a long dictation on the try it step, would
            // otherwise clip at both ends with no way to reach the content.
            GeometryReader { proxy in
                ScrollView {
                    ZStack {
                        stage
                            .id(flow.step)
                            .transition(
                                .asymmetric(
                                    insertion: .opacity.combined(with: .offset(x: 12)),
                                    removal: .opacity.combined(with: .offset(x: -8))
                                )
                            )
                    }
                    .frame(minHeight: proxy.size.height)
                }
                .scrollBounceBehavior(.basedOnSize)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .clipped()
            .animation(.easeOut(duration: 0.18), value: flow.step)

            OnboardingFooter(
                showsBack: flow.step != .welcome,
                showsDefer: flow.step != .done,
                reassurance: reassurance,
                primaryTitle: primaryTitle,
                primaryEnabled: flow.canContinue,
                deferTitle: "onboarding.setupLater.button".localized,
                onBack: { withAnimation { _ = flow.back() } },
                onDefer: setUpLater,
                onPrimary: primaryAction
            )
        }
        .frame(width: OnboardingStyle.windowWidth, height: OnboardingStyle.windowHeight)
        .background(VisualEffectBackground())
        .onAppear { flow.refreshPermissions() }
        // Re-check both permissions when the user returns from System Settings so
        // the rows and the audio manager's preview guard cannot retain stale state.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            flow.refreshPermissions()
        }
        .alert(
            "common.error".localized,
            isPresented: Binding(
                get: { flow.permissionErrorMessage != nil },
                set: { if !$0 { flow.permissionErrorMessage = nil } }
            )
        ) {
            Button {
                flow.permissionErrorMessage = nil
            } label: {
                Text(localized: "common.ok")
            }
        } message: {
            Text(flow.permissionErrorMessage ?? "app.unknown.error".localized)
        }
    }

    // MARK: - Stage

    @ViewBuilder
    private var stage: some View {
        switch flow.step {
        case .welcome:
            welcomeStep
        case .permissions:
            permissionsStep
        case .source:
            OnboardingSourcePicker(flow: flow)
        case .configure:
            OnboardingConfigureView(flow: flow)
        case .setup:
            OnboardingSetupView(flow: flow)
        case .microphone:
            OnboardingMicrophoneView(flow: flow)
        case .tryIt:
            tryItStep
        case .done:
            doneStep
        }
    }

    // MARK: - Welcome

    private var welcomeStep: some View {
        OnboardingStepScaffold(
            symbol: "waveform",
            question: "onboarding.welcome.title".localized,
            detail: "onboarding.welcome.subtitle".localized
        ) {
            OnboardingCard {
                welcomeTask(
                    1,
                    "onboarding.welcome.task1.title",
                    "onboarding.welcome.task1.caption"
                )
                OnboardingCardDivider()
                welcomeTask(
                    2,
                    "onboarding.welcome.task2.title",
                    "onboarding.welcome.task2.caption"
                )
                OnboardingCardDivider()
                welcomeTask(
                    3,
                    "onboarding.welcome.task3.title",
                    "onboarding.welcome.task3.caption"
                )
            }

            OnboardingQuietNote(text: "onboarding.welcome.note".localized)
        }
    }

    private func welcomeTask(_ number: Int, _ titleKey: String, _ captionKey: String) -> some View {
        OnboardingCardRow {
            OnboardingStepNumber(value: number)
            OnboardingRowText(title: titleKey.localized, caption: captionKey.localized)
        }
    }

    // MARK: - Permissions

    private var permissionsStep: some View {
        OnboardingStepScaffold(
            symbol: flow.hasMicrophonePermission ? "checkmark.shield" : "shield",
            question: "onboarding.permissions.title".localized,
            detail: "onboarding.permissions.subtitle".localized
        ) {
            OnboardingCard {
                permissionRow(
                    symbol: "mic",
                    title: "onboarding.permissions.microphone.title".localized,
                    caption: "onboarding.permissions.microphone.subtitle".localized,
                    granted: flow.hasMicrophonePermission,
                    // After a denial the OS will not re-prompt, so the action
                    // switches to deep linking System Settings.
                    actionTitle: (flow.microphoneAuthorization == .undetermined
                                  ? "onboarding.permissions.grant"
                                  : "onboarding.permissions.open").localized,
                    action: flow.handleMicrophoneAction
                )

                OnboardingCardDivider()

                permissionRow(
                    symbol: "text.cursor",
                    title: "onboarding.permissions.accessibility.title".localized,
                    caption: "onboarding.permissions.accessibility.subtitle".localized,
                    granted: flow.hasAccessibilityPermission,
                    actionTitle: (flow.isPollingForAccessibility
                                  ? "onboarding.accessibility.waiting"
                                  : "onboarding.permissions.open").localized,
                    action: flow.handleAccessibilityAction
                )
            }

            OnboardingQuietNote(text: "onboarding.permissions.note".localized)
        }
    }

    private func permissionRow(
        symbol: String,
        title: String,
        caption: String,
        granted: Bool,
        actionTitle: String,
        action: @escaping () -> Void
    ) -> some View {
        OnboardingCardRow {
            Image(systemName: symbol)
                .font(.system(size: 16))
                .foregroundStyle(.secondary)
                .frame(width: 20)

            OnboardingRowText(title: title, caption: caption)

            if granted {
                OnboardingStatusPill(
                    text: "onboarding.permissions.granted".localized,
                    symbol: "checkmark",
                    tone: .good
                )
            } else {
                Button(actionTitle, action: action)
                    .buttonStyle(.borderedProminent)
                    .controlSize(.small)
            }
        }
    }

    // MARK: - Try it (inline transcript, never pastes)

    private var tryItStep: some View {
        OnboardingStepScaffold(
            symbol: "record.circle",
            question: "onboarding.test.title".localized,
            detail: "onboarding.tryIt.detail".localized
        ) {
            OnboardingCard {
                OnboardingCardRow {
                    ForEach(shortcutKeys, id: \.self) { key in
                        OnboardingKeyCap(label: key)
                    }

                    Text("onboarding.tryIt.shortcutHint".localized)
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    recordControl
                }

                OnboardingCardDivider()

                OnboardingCardBlock {
                    Text("onboarding.tryIt.trySaying".localized)
                        .font(.system(size: 12))
                        .foregroundStyle(.tertiary)
                    Text("onboarding.tryIt.sampleLine".localized)
                        .font(.system(size: 14, weight: .semibold))
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.top, DesignConstants.Spacing.xs)
                }

                OnboardingCardDivider()

                OnboardingCardBlock {
                    Text(transcriptHeading)
                        .font(.system(size: 12))
                        .foregroundStyle(.tertiary)

                    if flow.isRecording {
                        // The idle metering session is deliberately not running
                        // here: `startInputLevelPreview` no-ops during a real
                        // recording, so the level was checked on the previous step.
                        Text("onboarding.tryIt.listening".localized)
                            .font(.system(size: 17, weight: .semibold))
                            .foregroundStyle(Color.accentColor)
                            .padding(.top, DesignConstants.Spacing.xs)
                    } else if flow.transcript.isEmpty {
                        Text("onboarding.tryIt.empty".localized)
                            .font(.system(size: 14))
                            .foregroundStyle(.secondary)
                            .padding(.top, DesignConstants.Spacing.xs)
                    } else {
                        // The transcript is the payoff of the whole flow, so it
                        // gets group header scale.
                        Text(flow.transcriptBody)
                            .font(.system(size: 17, weight: .semibold))
                            .foregroundStyle(flow.transcriptIsError ? Color.red : Color.primary)
                            .lineSpacing(2)
                            .fixedSize(horizontal: false, vertical: true)
                            .padding(.top, DesignConstants.Spacing.xs)

                        if !flow.transcriptIsError {
                            Text(transcriptMeta)
                                .font(.system(size: 12))
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                                .truncationMode(.tail)
                                .padding(.top, DesignConstants.Spacing.xs)
                        }
                    }
                }

                if !flow.transcript.isEmpty && !flow.isRecording {
                    OnboardingCardDivider()
                    OnboardingCardRow {
                        Text("onboarding.try.transcript.noPaste".localized)
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        Button("onboarding.tryIt.recordAgain".localized, action: flow.toggleTestRecording)
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                    }
                }
            }

            OnboardingQuietNote(text: "onboarding.tryIt.note".localized)
        }
        .onAppear { flow.beginTryItStep() }
        .onDisappear { flow.endTryItStep() }
    }

    @ViewBuilder
    private var recordControl: some View {
        if flow.isRecording {
            Button("onboarding.test.stop".localized, action: flow.toggleTestRecording)
                .buttonStyle(.bordered)
                .controlSize(.small)
        } else if flow.transcript.isEmpty {
            Button("onboarding.tryIt.record".localized, action: flow.toggleTestRecording)
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
        } else {
            OnboardingStatusPill(
                text: "onboarding.tryIt.recorded".localized,
                symbol: "checkmark",
                tone: .good
            )
        }
    }

    private var transcriptHeading: String {
        if flow.isRecording { return "onboarding.test.status.speak".localized }
        if flow.transcriptIsError { return "common.error".localized }
        return "onboarding.try.transcript.heading".localized
    }

    private var transcriptMeta: String {
        let words = flow.transcriptBody.split { $0 == " " || $0 == "\n" }.count
        return "onboarding.tryIt.transcriptMeta".localized(arguments: words, flow.selectedDeviceName)
    }

    // MARK: - Done

    private var doneStep: some View {
        OnboardingStepScaffold(
            symbol: "checkmark.circle",
            question: "onboarding.completion.title".localized,
            detail: "onboarding.done.detail".localized
        ) {
            OnboardingCard {
                summaryRow(
                    title: "onboarding.done.summary.transcription".localized,
                    value: sourceSummary
                )
                OnboardingCardDivider()
                summaryRow(
                    title: "onboarding.done.summary.microphone".localized,
                    value: flow.selectedDeviceName
                )
                OnboardingCardDivider()
                summaryRow(
                    title: "onboarding.done.summary.textDelivery".localized,
                    value: (flow.hasAccessibilityPermission
                            ? "onboarding.done.textDelivery.cursor"
                            : "onboarding.done.textDelivery.clipboard").localized
                )
                OnboardingCardDivider()
                OnboardingCardRow {
                    OnboardingRowText(title: "onboarding.done.summary.shortcut".localized)
                    ForEach(shortcutKeys, id: \.self) { key in
                        OnboardingKeyCap(label: key)
                    }
                }
            }

            OnboardingAccentNote(text: "onboarding.done.menuBarNote".localized)
        }
    }

    private func summaryRow(title: String, value: String) -> some View {
        OnboardingCardRow {
            OnboardingRowText(title: title)
            Text(value)
                .font(.system(size: 12))
                .foregroundStyle(.tertiary)
                .lineLimit(1)
                .truncationMode(.tail)
        }
        .accessibilityElement(children: .combine)
    }

    private var sourceSummary: String {
        switch flow.selectedSource {
        case .onDevice:
            let name = flow.selectedModel?.displayName ?? TranscriptionSource.onDevice.label
            return "onboarding.done.summary.onDevice".localized(arguments: name)
        case .hyperwhisperCloud:
            if let credits = hyperWhisperCloudManager.credits {
                let amount = Self.creditsFormatter
                    .string(from: NSNumber(value: credits.creditsRemaining)) ?? ""
                return "onboarding.done.summary.cloud".localized(
                    arguments: TranscriptionSource.hyperwhisperCloud.label, amount
                )
            }
            return TranscriptionSource.hyperwhisperCloud.label
        case .yourProvider:
            return "onboarding.done.summary.provider".localized(arguments: flow.selectedProvider.displayName)
        case nil:
            return "onboarding.setup.selectFirst".localized
        }
    }

    static let creditsFormatter: NumberFormatter = {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.maximumFractionDigits = 0
        return formatter
    }()

    // MARK: - Footer copy and actions

    private var primaryTitle: String {
        switch flow.step {
        case .welcome: return "onboarding.welcome.getStarted".localized
        case .done: return "onboarding.done.button".localized
        default: return "common.continue".localized
        }
    }

    private func primaryAction() {
        if flow.step == .done {
            completeOnboarding()
        } else {
            withAnimation { _ = flow.advance() }
        }
    }

    private var reassurance: String {
        switch flow.step {
        case .welcome:
            return "onboarding.footer.reassurance.welcome".localized
        case .permissions:
            return "onboarding.footer.reassurance.permissions".localized
        case .source:
            return "onboarding.footer.reassurance.source".localized
        case .configure:
            switch flow.selectedSource {
            case .hyperwhisperCloud: return "onboarding.footer.reassurance.configure.cloud".localized
            case .onDevice: return "onboarding.footer.reassurance.configure.onDevice".localized
            case .yourProvider: return "onboarding.footer.reassurance.configure.provider".localized
            case nil: return "onboarding.footer.reassurance.pickSource".localized
            }
        case .setup:
            switch flow.selectedSource {
            case .hyperwhisperCloud: return "onboarding.footer.reassurance.setup.cloud".localized
            case .onDevice: return "onboarding.footer.reassurance.setup.onDevice".localized
            case .yourProvider: return "onboarding.footer.reassurance.setup.provider".localized
            case nil: return "onboarding.footer.reassurance.pickSource".localized
            }
        case .microphone:
            return "onboarding.footer.reassurance.microphone".localized
        case .tryIt:
            return "onboarding.footer.reassurance.tryIt".localized
        case .done:
            return "onboarding.footer.reassurance.done".localized
        }
    }

    /// Explicit completion: the staged source becomes production state.
    ///
    /// #315: completion is refused when the chosen source stopped working during
    /// the last two steps, and the flow puts itself back on `.setup` so the user
    /// can fix it. Dismissing here regardless would tear down the flow with the
    /// Try It write still applied and nothing left alive to roll it back.
    private func completeOnboarding() {
        if flow.complete() { isPresented = false }
    }

    /// Set Up Later: anything already written is rolled back first, so the app is
    /// left exactly as it was before the sheet opened.
    private func setUpLater() {
        flow.deferSetup()
        isPresented = false
    }

    /// Onboarding spells the caps out as words, so every token maps to a label.
    private var shortcutKeys: [String] {
        let description = KeyboardShortcuts.getShortcut(for: .toggleRecordingWithTranscription)?.description
            ?? "keyboard.option.space".localized
        return ShortcutKeyTokens.tokenize(description).map { token in
            switch token {
            case .command: return "keyboard.command".localized
            case .option: return "keyboard.option".localized
            case .control: return "keyboard.control".localized
            case .shift: return "keyboard.shift".localized
            case .capsLock: return "keyboard.capsLock".localized
            case .escape: return "keyboard.escape".localized
            case .return: return "keyboard.return".localized
            case .space: return "keyboard.space".localized
            case .key(let key): return key
            }
        }
    }
}
