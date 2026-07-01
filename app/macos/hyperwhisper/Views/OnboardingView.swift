//
//  OnboardingView.swift
//  hyperwhisper
//
//  ONBOARDING FLOW
//  First-launch experience rebuilt around the validated "Choose your source"
//  flow: welcome → permissions → choose source → configure → set up → test →
//  done. The pivotal step is a three-card transcription-source picker
//  (On-Device / HyperWhisper Cloud / Your API Key); the chosen source is then
//  configured, set up (download / activate / save), and finally applied to the
//  app's existing default Mode so the first recording just works.
//
//  The three cards + per-source Configure/Setup views live in
//  Onboarding/OnboardingSourceViews.swift.
//

import SwiftUI
import KeyboardShortcuts
import AVFoundation

// MARK: - Main Onboarding View

/// 7-step onboarding flow for first-time users.
struct OnboardingView: View {
    // MARK: - Environment

    @EnvironmentObject var appState: AppState
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var transcriptionPipeline: TranscriptionPipeline
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var whisperModelManager: WhisperModelManager
    @EnvironmentObject var parakeetModelManager: ParakeetModelManager
    @EnvironmentObject var licenseManager: LicenseManager

    // MARK: - State

    /// Current step in the onboarding flow (0-7)
    @State private var currentStep: Int = 0

    // Choose-source selection + per-source configuration.
    @State private var selectedSource: TranscriptionSource?
    @State private var selectedModel: OnboardingModelSelection?
    @State private var licenseKeyInput: String = ""
    @State private var selectedProvider: CloudProvider = .openai
    @State private var apiKeyInput: String = ""

    // Lifted from `OnboardingConfigureView`: true once the Configure step's inline
    // "Test" for the currently-selected cloud source returned success (license
    // valid / provider healthy). The Continue gate for cloud sources reads this so
    // the user cannot advance on an unverified key. Reset by the child on step
    // appear and on any key/provider edit.
    @State private var keyValidated: Bool = false

    /// Track if accessibility permission was granted
    @State private var hasAccessibilityPermission: Bool = false

    /// Track if we're actively polling for permission
    @State private var isPollingForPermission: Bool = false

    /// Track if microphone permission was granted
    @State private var hasMicrophonePermission: Bool = false

    /// Error message to display
    @State private var errorMessage: String?

    /// Whether to show error alert
    @State private var showErrorAlert: Bool = false

    /// Binding to control presentation
    @Binding var isPresented: Bool

    // MARK: - Constants

    /// Total number of steps in onboarding
    private let totalSteps = 8

    /// Well-known UUID of the seeded default Mode (see
    /// `PersistenceController.initializeDefaultModes()`). Used as a stable fallback
    /// when no default Mode is found at completion time.
    private static let defaultModeID = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!

    // MARK: - Body

    var body: some View {
        VStack(spacing: 0) {
            // Progress indicator
            progressIndicator
                .padding(.top, 20)
                .padding(.horizontal, 40)

            // Main content area
            ZStack {
                Group {
                    switch currentStep {
                    case 0:
                        welcomeStep
                    case 1:
                        permissionsStep
                    case 2:
                        OnboardingSourcePicker(selectedSource: $selectedSource)
                    case 3:
                        configureStep
                    case 4:
                        setupStep
                    case 5:
                        OnboardingMicrophoneView()
                    case 6:
                        testRecordingStep
                    case 7:
                        completionStep
                    default:
                        EmptyView()
                    }
                }
                .transition(.asymmetric(
                    insertion: .move(edge: .trailing).combined(with: .opacity),
                    removal: .move(edge: .leading).combined(with: .opacity)
                ))
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .animation(.easeInOut(duration: 0.3), value: currentStep)

            // Navigation buttons
            navigationButtons
                .padding(.horizontal, 40)
                .padding(.bottom, 30)
        }
        .frame(width: 760, height: 580)
        .background(VisualEffectBackground())
        .onAppear {
            setupInitialState()
        }
        // Re-check microphone permission when the user returns from System Settings
        // (e.g. after enabling it there) so the mandatory permissions gate unblocks
        // without them having to re-click. Mirrors HomeView's accessibility re-check.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            hasMicrophonePermission = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        }
        .alert("common.error".localized, isPresented: $showErrorAlert) {
            Button {
                showErrorAlert = false
            } label: {
                Text(localized: "common.ok")
            }
        } message: {
            Text(errorMessage ?? "app.unknown.error".localized)
        }
    }

    // MARK: - Progress Indicator

    private var progressIndicator: some View {
        HStack(spacing: 8) {
            ForEach(0..<totalSteps, id: \.self) { step in
                Circle()
                    .fill(step <= currentStep ? Color.accentColor : Color.gray.opacity(0.3))
                    .frame(width: 8, height: 8)
                    .scaleEffect(step == currentStep ? 1.2 : 1.0)
                    .animation(.spring(response: 0.3, dampingFraction: 0.8), value: currentStep)
            }
        }
        .padding(.vertical, 12)
    }

    // MARK: - Step 0: Welcome

    private var welcomeStep: some View {
        VStack(spacing: 24) {
            Spacer()

            RoundedRectangle(cornerRadius: 18)
                .fill(Color.accentColor.gradient)
                .frame(width: 84, height: 84)
                .overlay(
                    Image(systemName: "waveform")
                        .font(.system(size: 38, weight: .semibold))
                        .foregroundColor(.white)
                )

            VStack(spacing: 12) {
                Text(localized: "onboarding.welcome.title")
                    .font(.system(size: 34, weight: .bold))
                    .multilineTextAlignment(.center)

                Text(localized: "onboarding.welcome.subtitle")
                    .font(.body)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 440)
            }

            VStack(spacing: 8) {
                Button(action: navigateForward) {
                    Label(LocalizedStringKey("onboarding.welcome.getStarted"), systemImage: "arrow.right")
                        .labelStyle(.titleAndIcon)
                        .frame(width: 200)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)

                Text(localized: "onboarding.welcome.duration")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }

            Spacer()
        }
        .padding(40)
    }

    // MARK: - Step 1: Permissions (microphone + accessibility)

    private var permissionsStep: some View {
        VStack(spacing: 24) {
            VStack(spacing: 12) {
                Text(localized: "onboarding.permissions.title")
                    .font(.title)
                    .fontWeight(.semibold)

                Text(localized: "onboarding.permissions.subtitle")
                    .font(.body)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 460)
            }

            VStack(spacing: 12) {
                permissionRow(
                    icon: "mic.fill",
                    title: "onboarding.permissions.microphone.title",
                    subtitle: "onboarding.permissions.microphone.subtitle",
                    isGranted: hasMicrophonePermission,
                    // After a denial the OS won't re-prompt, so the action switches
                    // to deep-linking System Settings — surface that in the label.
                    actionTitle: AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined
                        ? "onboarding.permissions.grant"
                        : "onboarding.permissions.open",
                    action: handleMicrophoneAction
                )

                permissionRow(
                    icon: "hand.tap.fill",
                    title: "onboarding.permissions.accessibility.title",
                    subtitle: "onboarding.permissions.accessibility.subtitle",
                    isGranted: hasAccessibilityPermission,
                    actionTitle: isPollingForPermission ? "onboarding.accessibility.waiting" : "onboarding.permissions.open",
                    action: {
                        AccessibilityHelper.shared.openAccessibilitySettings()
                        isPollingForPermission = true
                        pollForAccessibilityPermission()
                    }
                )
            }
            .frame(maxWidth: 480)
        }
        .padding(40)
    }

    private func permissionRow(icon: String, title: String, subtitle: String, isGranted: Bool, actionTitle: String, action: @escaping () -> Void) -> some View {
        HStack(spacing: 14) {
            Image(systemName: icon)
                .font(.system(size: 22))
                .foregroundColor(.accentColor)
                .frame(width: 32)

            VStack(alignment: .leading, spacing: 2) {
                Text(title.localized)
                    .font(.system(size: 14, weight: .semibold))
                Text(subtitle.localized)
                    .font(.caption)
                    .foregroundColor(.secondary)
            }

            Spacer()

            if isGranted {
                Label("onboarding.permissions.granted".localized, systemImage: "checkmark.circle.fill")
                    .font(.callout)
                    .foregroundColor(.green)
            } else {
                Button(action: action) {
                    Text(actionTitle.localized)
                }
                .controlSize(.regular)
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(16)
        .background(
            RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium)
                .fill(.thinMaterial)
        )
    }

    // MARK: - Step 3: Configure

    @ViewBuilder
    private var configureStep: some View {
        if let source = selectedSource {
            OnboardingConfigureView(
                source: source,
                selectedModel: $selectedModel,
                licenseKeyInput: $licenseKeyInput,
                selectedProvider: $selectedProvider,
                apiKeyInput: $apiKeyInput,
                keyValidated: $keyValidated
            )
        } else {
            EmptyView()
        }
    }

    // MARK: - Step 4: Set up

    @ViewBuilder
    private var setupStep: some View {
        if let source = selectedSource {
            OnboardingSetupView(
                source: source,
                selectedModel: $selectedModel,
                licenseKeyInput: $licenseKeyInput,
                selectedProvider: $selectedProvider,
                apiKeyInput: $apiKeyInput
            )
        } else {
            EmptyView()
        }
    }

    // MARK: - Step 6: Give it a try (inline transcript, never pastes)

    private var testRecordingStep: some View {
        VStack(spacing: 20) {
            VStack(spacing: 10) {
                Text(localized: "onboarding.test.title")
                    .font(.title2)
                    .fontWeight(.semibold)
                    .multilineTextAlignment(.center)

                HStack(spacing: 4) {
                    Text(localized: "onboarding.test.press.prefix")
                    KeyboardShortcutBadge(keys: getRecordingShortcut())
                    Text(localized: "onboarding.test.press.suffix")
                }
                .font(.body)
                .foregroundColor(.secondary)
            }

            // Single record/stop control. The `.onboarding` trigger routes the
            // transcript inline (see RecordingTranscriptionFlow+StopRecording) —
            // it is never pasted into another app.
            Button(action: {
                audioManager.toggleRecordingWithTranscription(trigger: .onboarding)
            }) {
                ZStack {
                    Circle()
                        .fill(audioManager.isRecording ? Color.red.opacity(0.15) : Color.accentColor.opacity(0.12))
                        .frame(width: 78, height: 78)
                    Image(systemName: audioManager.isRecording ? "stop.fill" : "mic.fill")
                        .font(.system(size: 30))
                        .foregroundColor(audioManager.isRecording ? .red : .accentColor)
                }
            }
            .buttonStyle(.plain)

            if audioManager.isRecording {
                Text(localized: "onboarding.test.status.speak")
                    .font(.callout)
                    .foregroundColor(.red)
            }

            // Inline transcript panel — shown only here, not pasted.
            if !appState.lastTranscription.isEmpty {
                VStack(alignment: .leading, spacing: 10) {
                    Text(localized: "onboarding.try.transcript.heading")
                        .font(.system(size: 10.5, weight: .semibold))
                        .tracking(0.5)
                        .textCase(.uppercase)
                        .foregroundColor(.secondary)

                    Text(appState.lastTranscription)
                        .font(.system(size: 15))
                        .fixedSize(horizontal: false, vertical: true)

                    HStack(spacing: 5) {
                        Image(systemName: "checkmark.shield.fill")
                            .font(.caption)
                        Text(localized: "onboarding.try.transcript.noPaste")
                            .font(.caption)
                    }
                    .foregroundColor(.green)
                }
                .padding(16)
                .frame(maxWidth: 460, alignment: .leading)
                .background(
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.large)
                        .fill(.thinMaterial)
                )
            }
        }
        .padding(40)
        // Clear any prior transcript so the panel only reflects a recording made
        // on this screen; clear again on leave so it never lingers.
        .onAppear { appState.lastTranscription = "" }
        .onDisappear { appState.lastTranscription = "" }
    }

    // MARK: - Step 7: Done

    private var completionStep: some View {
        VStack(spacing: 24) {
            Spacer()

            RoundedRectangle(cornerRadius: 18)
                .fill(Color.green.gradient)
                .frame(width: 84, height: 84)
                .overlay(
                    Image(systemName: "checkmark")
                        .font(.system(size: 38, weight: .semibold))
                        .foregroundColor(.white)
                )

            Text(localized: "onboarding.completion.title")
                .font(.system(size: 34, weight: .bold))
                .multilineTextAlignment(.center)

            Spacer()
        }
        .padding(40)
    }

    // MARK: - Navigation Buttons

    private var navigationButtons: some View {
        HStack {
            // Back button
            if currentStep > 0 {
                Button(action: navigateBack) {
                    Label(LocalizedStringKey("common.back"), systemImage: "chevron.left")
                }
                .controlSize(.large)
            }

            Spacer()

            // Forward / Done. The welcome step (0) is driven by its hero button,
            // so no footer Continue there.
            if currentStep == 0 {
                EmptyView()
            } else if currentStep < totalSteps - 1 {
                Button(action: navigateForward) {
                    Label(LocalizedStringKey("common.continue"), systemImage: "chevron.right")
                        .labelStyle(.titleAndIcon)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                .disabled(!canContinueFromCurrentStep())
            } else {
                Button(action: completeOnboarding) {
                    Text(localized: "onboarding.done.button")
                        .frame(width: 150)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
            }
        }
    }

    // MARK: - Helper Methods

    /// Setup initial state when view appears
    private func setupInitialState() {
        // Check current permissions
        hasAccessibilityPermission = AccessibilityHelper.shared.hasAccessibilityPermission()

        // Check microphone permission
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            hasMicrophonePermission = true
        default:
            hasMicrophonePermission = false
        }
    }

    /// Navigate to previous step
    private func navigateBack() {
        withAnimation {
            currentStep = max(0, currentStep - 1)
        }
    }

    /// Navigate to next step
    private func navigateForward() {
        // Apply the chosen source to the default Mode as we leave the Set up step
        // (4) so the Test Recording step (5) actually records through the source
        // the user picked — not the seeded HyperWhisper Cloud default. Idempotent,
        // so returning to step 4 and forward again simply re-applies.
        if currentStep == 4 {
            applySelectedSourceToDefaultMode()
        }
        withAnimation {
            currentStep = min(totalSteps - 1, currentStep + 1)
        }
    }

    /// Check if can continue from current step. The Set up step (4) is the
    /// mandatory gate: the chosen source must be genuinely usable.
    private func canContinueFromCurrentStep() -> Bool {
        switch currentStep {
        case 1: // Permissions — microphone is required to record
            return hasMicrophonePermission
        case 2: // Choose source — must have picked one
            return selectedSource != nil
        case 3: // Configure — must have entered the source's specifics
            guard let source = selectedSource else { return false }
            switch source {
            case .onDevice:
                return selectedModel != nil
            case .hyperwhisperCloud:
                // A cloud source needs a WORKING key to continue. Either the license
                // is already activated/validated on this machine, or the inline
                // "Test" in this session returned valid. A non-empty key text is no
                // longer enough — it could be wrong.
                return licenseManager.licenseStatus == .active || keyValidated
            case .yourProvider:
                // BYOK must pass the inline "Test" (health probe returned healthy)
                // before continuing. A returning user re-presses Test once this
                // session — an accepted tradeoff for never advancing on a dead key.
                return keyValidated
            }
        case 4: // Set up — source must be genuinely usable (mandatory gate)
            guard let source = selectedSource else { return false }
            return isSelectedSourceUsable(source)
        default:
            return true
        }
    }

    /// The per-source "is this actually usable now" check.
    private func isSelectedSourceUsable(_ source: TranscriptionSource) -> Bool {
        switch source {
        case .onDevice:
            guard let model = selectedModel else { return false }
            switch model.kind {
            case .whisper:
                return whisperModelManager.getModelPath(for: model.id) != nil
            case .parakeet:
                return parakeetModelManager.availableModels.first { $0.id == model.id }?.isDownloaded == true
            }
        case .hyperwhisperCloud:
            return licenseManager.licenseStatus == .active
        case .yourProvider:
            return settingsManager.apiKeys.hasAPIKey(for: selectedProvider)
        }
    }

    /// Complete onboarding and close. The chosen source was already applied to the
    /// default Mode when leaving the Set up step (see `navigateForward`); re-apply
    /// here as a final guarantee in case that transition was ever bypassed.
    private func completeOnboarding() {
        applySelectedSourceToDefaultMode()

        // Defensive: release the microphone metering preview in case onboarding is
        // completed without passing back through the Microphone step's onDisappear.
        audioManager.stopInputLevelPreview()

        // Mark onboarding as completed and clear the durable "still owed" flag so
        // a completed run is never re-shown on the next launch.
        UserDefaults.standard.set(true, forKey: "hasCompletedOnboarding")
        UserDefaults.standard.set(false, forKey: "onboardingPending")

        // Close onboarding
        isPresented = false

        // Navigate to home
        appState.selectedNavigationItem = .home
    }

    /// Reconfigure the EXISTING default Mode (well-known UUID …0001, created by
    /// `PersistenceController.initializeDefaultModes()`) to the chosen source.
    /// We update in place rather than creating a second Mode.
    private func applySelectedSourceToDefaultMode() {
        guard let source = selectedSource else { return }

        let persistence = PersistenceController.shared
        let existing = persistence.findDefaultMode()

        let chosenModel: String
        let chosenProvider: String?
        let postProcessingMode: Int16
        let accuracyTier: String?

        switch source {
        case .onDevice:
            // Fully offline/free: local model, post-processing off. `id` is the
            // exact string the transcription router expects — a Whisper catalog
            // name ("base") or a Parakeet id ("parakeet-tdt-0.6b-v2").
            chosenModel = selectedModel?.id ?? "base"
            chosenProvider = nil
            postProcessingMode = 0
            accuracyTier = nil
        case .hyperwhisperCloud:
            chosenModel = "cloud"
            chosenProvider = "hyperwhisper"
            postProcessingMode = 1
            accuracyTier = CloudAccuracyTier.elevenLabsScribeV2.rawValue
        case .yourProvider:
            // BYOK: cloud path via the user's provider, post-processing off by
            // default so first-run never fails on a missing post-processing key.
            chosenModel = "cloud"
            chosenProvider = selectedProvider.rawValue
            postProcessingMode = 0
            accuracyTier = nil
        }

        // Reconfigure ONLY the source-defining fields (model / cloudProvider /
        // postProcessingMode / cloudAccuracyTier). createOrUpdateMode resets any
        // omitted field to its default, so we forward every other value from the
        // existing default Mode to avoid wiping a returning user's customizations
        // (custom instructions, system prompt, spelling, etc.). cloudTranscription-
        // Model is intentionally omitted so it re-derives for the new provider/tier.
        let updated = persistence.createOrUpdateMode(
            id: existing?.id ?? Self.defaultModeID,
            name: existing?.name ?? "Default",
            preset: existing?.preset ?? "hyper",
            language: existing?.language ?? "en",
            model: chosenModel,
            punctuation: existing?.punctuation ?? true,
            capitalization: existing?.capitalization ?? true,
            profanityFilter: existing?.profanityFilter ?? false,
            customInstructions: existing?.customInstructions,
            languageModel: existing?.languageModel,
            cloudProvider: chosenProvider,
            postProcessingMode: postProcessingMode,
            postProcessingProvider: existing?.postProcessingProvider,
            englishSpelling: existing?.englishSpelling,
            userSystemPrompt: existing?.userSystemPrompt,
            useStreamingTranscription: existing?.useStreamingTranscription ?? false,
            cloudAccuracyTier: accuracyTier,
            removeTrailingPeriod: existing?.removeTrailingPeriod ?? false,
            enableScreenOCR: existing?.enableScreenOCR ?? false,
            geminiCustomPrompt: existing?.geminiCustomPrompt,
            cloudPostProcessingModel: existing?.cloudPostProcessingModel,
            cloudTranscriptionDomain: existing?.cloudTranscriptionDomain,
            foreignPlatformExtensions: existing?.foreignPlatformExtensions
        )

        // Defensive: if no default Mode existed (unseeded store), createOrUpdateMode
        // does NOT flag the row it created as default — mark it so the chosen source
        // becomes the active default instead of a stray, non-default Mode.
        if existing == nil && !updated.isDefault {
            updated.isDefault = true
            persistence.save()
        }

        // Repoint the ACTIVE mode at Default. Writing the source onto the Default
        // Mode is not enough on its own: a returning user's `selectedModeId` still
        // points at their old custom mode, so the next recording would keep using
        // that mode's (e.g. Parakeet) source. `selectMode` sets `selectedModeId` to
        // Default and refreshes `selectedModeSnapshot`, which the record-time
        // resolver reads — so the chosen source takes effect with no relaunch.
        appState.selectMode(updated, persist: true)
    }

    /// Handle a tap on the microphone permission action. When the status is still
    /// undetermined we show the system prompt; once denied/restricted the OS will
    /// not re-prompt, so we deep-link to System Settings instead of no-op'ing.
    private func handleMicrophoneAction() {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .notDetermined:
            requestMicrophonePermission()
        default:
            openMicrophoneSettings()
        }
    }

    /// Request microphone permission
    private func requestMicrophonePermission() {
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            DispatchQueue.main.async {
                self.hasMicrophonePermission = granted
                if !granted {
                    self.errorMessage = "onboarding.error.microphone.denied".localized
                    self.showErrorAlert = true
                }
            }
        }
    }

    /// Open System Settings › Privacy & Security › Microphone so the user can grant
    /// access after a prior denial (same pane the main app's alert links to).
    private func openMicrophoneSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Poll for accessibility permission
    private func pollForAccessibilityPermission() {
        AccessibilityHelper.shared.waitForAccessibilityPermission { granted in
            DispatchQueue.main.async {
                self.hasAccessibilityPermission = granted
                self.isPollingForPermission = false
            }
        }
    }

    /// Get formatted recording shortcut
    private func getRecordingShortcut() -> String {
        return KeyboardShortcuts.getShortcut(for: .toggleRecordingWithTranscription)?.description ?? "keyboard.option.space".localized
    }
}

// MARK: - Preview

#Preview {
    OnboardingView(isPresented: .constant(true))
        .environmentObject(AppState())
        .environmentObject(AudioRecordingManager())
        .environmentObject(TranscriptionPipeline())
        .environmentObject(SettingsManager())
        // NOTE: Preview uses fresh instance for isolation
        .environmentObject(WhisperModelManager())
        .environmentObject(ParakeetModelManager())
        .environmentObject(LicenseManager())
        .environmentObject(CloudProviderHealthManager())
}
