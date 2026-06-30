//
//  OnboardingView.swift
//  hyperwhisper
//
//  ONBOARDING FLOW
//  Comprehensive first-launch experience that guides users through
//  all essential setup steps including permissions, model download,
//  and initial configuration.
//

import SwiftUI
import KeyboardShortcuts
import AVFoundation
import Combine

@MainActor
private final class OnboardingDownloadObserverStore: ObservableObject {
    private var cancellables: Set<AnyCancellable> = []

    func store(_ cancellable: AnyCancellable) {
        cancellables.insert(cancellable)
    }

    func removeAll() {
        cancellables.removeAll()
    }
}

// MARK: - Main Onboarding View

/// 8-step onboarding flow for first-time users
struct OnboardingView: View {
    // MARK: - Environment

    @EnvironmentObject var appState: AppState
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var transcriptionPipeline: TranscriptionPipeline
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var whisperModelManager: WhisperModelManager
    @EnvironmentObject var licenseManager: LicenseManager
    /// High-frequency metrics (audioLevel) isolated for performance.
    @EnvironmentObject var liveMetrics: RecordingLiveMetrics
    
    // MARK: - State
    
    /// Current step in the onboarding flow (0-7)
    @State private var currentStep: Int = 0
    
    /// Selected model ID during onboarding
    @State private var selectedModelId: String = "base"
    
    /// Whether to use system default audio device
    @State private var useSystemDefaultDevice: Bool = true
    
    /// Selected audio device ID
    @State private var selectedDeviceId: String = ""
    
    /// Track if test recording was completed
    @State private var testRecordingCompleted: Bool = false
    
    /// Track if accessibility permission was granted
    @State private var hasAccessibilityPermission: Bool = false
    
    /// Track if we're actively polling for permission
    @State private var isPollingForPermission: Bool = false
    
    /// Track if microphone permission was granted
    @State private var hasMicrophonePermission: Bool = false
    
    /// Model download progress (0.0 to 1.0)
    @State private var modelDownloadProgress: Float = 0.0
    
    /// Whether model is currently downloading
    @State private var isDownloadingModel: Bool = false

    /// Combine subscriptions observing download progress/completion.
    /// Retained outside view state so observer lifetime changes do not redraw the UI.
    @StateObject private var downloadObservers = OnboardingDownloadObserverStore()

    /// Error message to display
    @State private var errorMessage: String?
    
    /// Whether to show error alert
    @State private var showErrorAlert: Bool = false
    
    /// Binding to control presentation
    @Binding var isPresented: Bool
    
    // MARK: - Constants
    
    /// Total number of steps in onboarding
    private let totalSteps = 7
    
    // MARK: - Body
    
    var body: some View {
        VStack(spacing: 0) {
            // Progress indicator
            progressIndicator
                .padding(.top, 20)
                .padding(.horizontal, 40)
            
            // Main content area
            ZStack {
                // Step content with transition
                Group {
                    switch currentStep {
                    case 0:
                        accessibilityStep
                    case 1:
                        modelSelectionStep
                    case 2:
                        audioDeviceStep
                    case 3:
                        recordingPermissionStep
                    case 4:
                        testRecordingStep
                    case 5:
                        settingsPreviewStep
                    case 6:
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
        .frame(width: 700, height: 550)
        .background(VisualEffectBackground())
        .onAppear {
            setupInitialState()
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
    
    // MARK: - Step 1: Accessibility
    
    private var accessibilityStep: some View {
        VStack(spacing: 24) {
            // Title and description
            VStack(spacing: 12) {
                Text(localized: "onboarding.accessibility.title")
                    .font(.title)
                    .fontWeight(.semibold)
                
                Text(localized: "onboarding.accessibility.subtitle")
                    .font(.body)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 400)
            }
            
            // Visual placeholder for screenshot
            RoundedRectangle(cornerRadius: 12)
                .fill(Color.gray.opacity(0.1))
                .frame(width: 400, height: 200)
                .overlay(
                    VStack(spacing: 12) {
                        Image(systemName: "hand.tap.fill")
                            .font(.system(size: 48))
                            .foregroundColor(.accentColor.opacity(0.5))
                        Text(localized: "onboarding.accessibility.instructions")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                )
            
            // Permission status
            HStack(spacing: 8) {
                Image(systemName: hasAccessibilityPermission ? "checkmark.circle.fill" : "xmark.circle.fill")
                    .foregroundColor(hasAccessibilityPermission ? .green : .orange)
                Text(localized: hasAccessibilityPermission ? "onboarding.accessibility.status.granted" : "onboarding.accessibility.status.pending")
                    .font(.callout)
                    .foregroundColor(hasAccessibilityPermission ? .green : .orange)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill((hasAccessibilityPermission ? Color.green : Color.orange).opacity(0.1))
            )
            
            // Action button and restart instruction
            VStack(spacing: 12) {
                Button(action: {
                    AccessibilityHelper.shared.openAccessibilitySettings()
                    // Start polling for permission
                    isPollingForPermission = true
                    pollForAccessibilityPermission()
                }) {
                    Label(isPollingForPermission ? LocalizedStringKey("onboarding.accessibility.waiting") : LocalizedStringKey("onboarding.accessibility.open.preferences"), systemImage: "gearshape")
                        .frame(width: 280)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                
                if isPollingForPermission && !hasAccessibilityPermission {
                    Text(localized: "onboarding.accessibility.restart")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
        }
        .padding(40)
    }
    
    // MARK: - Step 2: Model Selection
    
    private var modelSelectionStep: some View {
        VStack(spacing: 24) {
            // Icon
            Image(systemName: "brain")
                .font(.system(size: 56))
                .foregroundColor(.accentColor)
                .symbolRenderingMode(.hierarchical)
            
            // Title and description
            VStack(spacing: 12) {
                Text(localized: "onboarding.model.recommendation.title")
                    .font(.title)
                    .fontWeight(.semibold)
                
                Text(localized: "onboarding.model.recommendation.subtitle")
                    .font(.body)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
                
                Text(localized: "onboarding.model.recommendation.note")
                    .font(.callout)
                    .foregroundColor(.secondary)
            }
            
            // Model info card
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text(getRecommendedModelName())
                        .font(.headline)
                    Spacer()
                    if isDownloadingModel {
                        ProgressView(value: modelDownloadProgress)
                            .progressViewStyle(.linear)
                            .frame(width: 100)
                    } else if isModelDownloaded() {
                        Label(LocalizedStringKey("onboarding.model.downloaded"), systemImage: "checkmark.circle.fill")
                            .foregroundColor(.green)
                            .font(.caption)
                    }
                }
                
                HStack(spacing: 40) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(localized: "onboarding.model.metric.speed")
                            .font(.caption)
                            .foregroundColor(.secondary)
                        HStack(spacing: 2) {
                            ForEach(0..<5, id: \.self) { index in
                                Rectangle()
                                    .fill(index < 3 ? Color.accentColor : Color.gray.opacity(0.3))
                                    .frame(width: 8, height: 16)
                                    .cornerRadius(2)
                            }
                        }
                    }
                    
                    VStack(alignment: .leading, spacing: 4) {
                        Text(localized: "onboarding.model.metric.accuracy")
                            .font(.caption)
                            .foregroundColor(.secondary)
                        HStack(spacing: 2) {
                            ForEach(0..<5, id: \.self) { index in
                                Rectangle()
                                    .fill(index < 4 ? Color.accentColor : Color.gray.opacity(0.3))
                                    .frame(width: 8, height: 16)
                                    .cornerRadius(2)
                            }
                        }
                    }
                    
                    VStack(alignment: .leading, spacing: 4) {
                        Text(localized: "onboarding.model.metric.size")
                            .font(.caption)
                            .foregroundColor(.secondary)
                        Text(localized: "onboarding.model.metric.size.value")
                            .font(.caption)
                            .fontWeight(.medium)
                    }
                }
            }
            .padding(20)
            .frame(width: 400)
            .background(
                RoundedRectangle(cornerRadius: 12)
                    .fill(.thinMaterial)
            )
            
            // Model picker for alternative selection
            VStack(spacing: 8) {
                Text(localized: "onboarding.model.alternatives")
                    .font(.caption)
                    .foregroundColor(.secondary)
                
                Picker(selection: $selectedModelId) {
                    Text(localized: "onboarding.model.option.proEnglish").tag("base.en")
                    Text(localized: "onboarding.model.option.tiny").tag("tiny")
                    Text(localized: "onboarding.model.option.base").tag("base")
                    Text(localized: "onboarding.model.option.small").tag("small")
                    Text(localized: "onboarding.model.option.medium").tag("medium")
                    Text(localized: "onboarding.model.option.large").tag("large-v3")
                } label: {
                    Text(localized: "onboarding.model.picker.title")
                }
                .pickerStyle(.segmented)
                .frame(width: 350)
            }
            
            // Download button
            if !isModelDownloaded() {
                Button(action: downloadSelectedModel) {
                    if isDownloadingModel {
                        let progressPercent = Int(modelDownloadProgress * 100)
                        HStack {
                            ProgressView()
                                .progressViewStyle(.circular)
                                .scaleEffect(0.8)
                            Text("onboarding.model.downloading.progress".localized(arguments: progressPercent))
                        }
                        .frame(width: 200)
                    } else {
                        Label(LocalizedStringKey("onboarding.model.download.button"), systemImage: "arrow.down.circle.fill")
                            .frame(width: 200)
                    }
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                .disabled(isDownloadingModel)
            }
        }
        .padding(40)
    }
    
    // MARK: - Step 4: Audio Device Selection
    
    private var audioDeviceStep: some View {
        VStack(spacing: 24) {
            // Icon
            Image(systemName: "mic")
                .font(.system(size: 56))
                .foregroundColor(.accentColor)
                .symbolRenderingMode(.hierarchical)
            
            // Title and description
            VStack(spacing: 12) {
                Text(localized: "onboarding.audio.title")
                    .font(.title)
                    .fontWeight(.semibold)
                
                Text(localized: "onboarding.audio.subtitle")
                    .font(.body)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
            }
            
            // Device selection
            VStack(alignment: .leading, spacing: 16) {
                // Device picker
                VStack(alignment: .leading, spacing: 8) {
                    Text(localized: "onboarding.audio.input.label")
                        .font(.callout)
                        .foregroundColor(.secondary)
                    
                    Picker(selection: $selectedDeviceId) {
                        ForEach(audioManager.availableDevices, id: \.id) { device in
                            Text(device.name).tag(device.id)
                        }
                    } label: {
                        Text(localized: "onboarding.audio.picker.label")
                    }
                    .pickerStyle(.menu)
                    .frame(width: 300)
                    .disabled(useSystemDefaultDevice)
                }
                
                // System default toggle
                Toggle(isOn: $useSystemDefaultDevice) {
                    Text(localized: "onboarding.audio.use.system.default")
                }
                    .toggleStyle(.switch)
                
                // Audio level indicator
                VStack(alignment: .leading, spacing: 8) {
                    Text(localized: "onboarding.audio.level")
                        .font(.callout)
                        .foregroundColor(.secondary)
                    
                    GeometryReader { geometry in
                        ZStack(alignment: .leading) {
                            RoundedRectangle(cornerRadius: 4)
                                .fill(Color.gray.opacity(0.2))
                            
                            RoundedRectangle(cornerRadius: 4)
                                .fill(Color.green)
                                .frame(width: geometry.size.width * CGFloat(liveMetrics.audioLevel))
                        }
                    }
                    .frame(height: 8)
                }
            }
            .padding(24)
            .frame(width: 400)
            .background(
                RoundedRectangle(cornerRadius: 12)
                    .fill(.thinMaterial)
            )
            
            // Open Sound Preferences button
            Button(action: {
                if let url = URL(string: "x-apple.systempreferences:com.apple.preference.sound") {
                    NSWorkspace.shared.open(url)
                }
            }) {
                Label(LocalizedStringKey("onboarding.audio.open.sound"), systemImage: "speaker.wave.2")
            }
            .controlSize(.regular)
        }
        .padding(40)
    }
    
    // MARK: - Step 5: Recording Permission
    
    private var recordingPermissionStep: some View {
        VStack(spacing: 24) {
            // Icon with status indicator
            ZStack(alignment: .topTrailing) {
                Image(systemName: "mic.fill")
                    .font(.system(size: 56))
                    .foregroundColor(.accentColor)
                    .symbolRenderingMode(.hierarchical)
                
                if hasMicrophonePermission {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 20))
                        .foregroundColor(.green)
                        .background(Circle().fill(.white).frame(width: 16, height: 16))
                        .offset(x: 8, y: -8)
                }
            }
            
            // Title and description
            VStack(spacing: 12) {
                Text(localized: "onboarding.microphone.title")
                    .font(.title)
                    .fontWeight(.semibold)
                
                Text(localized: "onboarding.microphone.subtitle")
                    .font(.body)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
            }
            
            // Permission status
            HStack(spacing: 8) {
                Image(systemName: hasMicrophonePermission ? "checkmark.circle.fill" : "xmark.circle.fill")
                    .foregroundColor(hasMicrophonePermission ? .green : .orange)
                Text(localized: hasMicrophonePermission ? "onboarding.microphone.status.granted" : "onboarding.microphone.status.pending")
                    .font(.callout)
                    .foregroundColor(hasMicrophonePermission ? .green : .orange)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill((hasMicrophonePermission ? Color.green : Color.orange).opacity(0.1))
            )
            
            // Grant permission button
            if !hasMicrophonePermission {
                Button(action: requestMicrophonePermission) {
                    Label(LocalizedStringKey("onboarding.microphone.grant"), systemImage: "mic.badge.plus")
                        .frame(width: 200)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(40)
    }
    
    // MARK: - Step 6: Test Recording
    
    private var testRecordingStep: some View {
        VStack(spacing: 24) {
            // Icon
            Image(systemName: "waveform")
                .font(.system(size: 56))
                .foregroundColor(.accentColor)
                .symbolRenderingMode(.hierarchical)
            
            // Title and description
            VStack(spacing: 12) {
                Text(localized: "onboarding.test.title")
                    .font(.title)
                    .fontWeight(.semibold)
                
                HStack(spacing: 4) {
                    Text(localized: "onboarding.test.press.prefix")
                    KeyboardShortcutBadge(keys: getRecordingShortcut())
                    Text(localized: "onboarding.test.press.suffix")
                }
                .font(.body)
                .foregroundColor(.secondary)
            }
            
            // Recording status
            if audioManager.isRecording {
                VStack(spacing: 16) {
                    // Recording indicator
                    HStack(spacing: 8) {
                        Circle()
                            .fill(Color.red)
                            .frame(width: 12, height: 12)
                            .overlay(
                                Circle()
                                    .stroke(Color.red.opacity(0.3), lineWidth: 8)
                                    .scaleEffect(1.5)
                                    .opacity(0)
                                    .animation(
                                        .easeOut(duration: 1.0)
                                        .repeatForever(autoreverses: false),
                                        value: audioManager.isRecording
                                    )
                            )
                        Text(localized: "onboarding.test.status.recording")
                            .font(.headline)
                            .foregroundColor(.red)
                    }
                    
                    // Waveform placeholder
                    RoundedRectangle(cornerRadius: 8)
                        .fill(Color.gray.opacity(0.1))
                        .frame(width: 300, height: 60)
                        .overlay(
                            Text(localized: "onboarding.test.status.speak")
                                .foregroundColor(.secondary)
                        )
                    
                    // Stop button
                    Button(action: {
                        audioManager.toggleRecordingWithTranscription(trigger: .onboarding)
                        testRecordingCompleted = true
                    }) {
                        Label(LocalizedStringKey("onboarding.test.stop"), systemImage: "stop.circle.fill")
                            .foregroundColor(.white)
                            .frame(width: 150)
                    }
                    .controlSize(.large)
                    .buttonStyle(.borderedProminent)
                    .tint(.red)
                }
            } else if testRecordingCompleted {
                // Success state
                VStack(spacing: 16) {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 48))
                        .foregroundColor(.green)
                    
                    Text(localized: "onboarding.test.success.title")
                        .font(.headline)
                        .foregroundColor(.green)
                    
                    Text(localized: "onboarding.test.success.subtitle")
                        .font(.callout)
                        .foregroundColor(.secondary)
                }
            } else {
                // Ready to record state
                VStack(spacing: 16) {
                    Button(action: {
                        audioManager.toggleRecordingWithTranscription(trigger: .onboarding)
                    }) {
                        VStack(spacing: 8) {
                            Image(systemName: "mic.circle.fill")
                                .font(.system(size: 64))
                            Text(localized: "onboarding.test.start.cta")
                                .font(.headline)
                        }
                    }
                    .buttonStyle(.plain)
                    .foregroundColor(.accentColor)
                    
                    Text(localized: "onboarding.test.start.shortcut")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
        }
        .padding(40)
    }
    
    // MARK: - Step 7: Settings Preview
    
    private var settingsPreviewStep: some View {
        VStack(spacing: 24) {
            // Title and description
            VStack(spacing: 12) {
                Text(localized: "onboarding.settings.title")
                    .font(.title)
                    .fontWeight(.semibold)
                
                Text(localized: "onboarding.settings.subtitle")
                    .font(.body)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
            }
            
            // Visual representation of menu bar and settings
            RoundedRectangle(cornerRadius: 12)
                .fill(Color.gray.opacity(0.1))
                .frame(width: 400, height: 250)
                .overlay(
                    VStack(spacing: 20) {
                        // Mock menu bar
                        HStack {
                            Spacer()
                            Image(systemName: "airplane.circle")
                                .font(.system(size: 20))
                                .foregroundColor(.primary)
                        }
                        .padding(.horizontal, 20)
                        .padding(.vertical, 8)
                        .background(Color.gray.opacity(0.1))
                        
                        // Mock menu items
                        VStack(alignment: .leading, spacing: 4) {
                            Text(localized: "onboarding.settings.menu.startStop")
                            Divider()
                            Text(localized: "onboarding.settings.menu.history")
                            HStack {
                                Text(localized: "onboarding.settings.menu.settings")
                                Spacer()
                                Image(systemName: "arrow.left")
                                    .foregroundColor(.accentColor)
                            }
                            Divider()
                            Text(localized: "onboarding.settings.menu.quit")
                        }
                        .font(.caption)
                        .padding(12)
                        .frame(width: 200)
                        .background(
                            RoundedRectangle(cornerRadius: 8)
                                .fill(.regularMaterial)
                                .shadow(radius: 8)
                        )
                    }
                )
            
            // Info text
            Text(localized: "onboarding.settings.info")
                .font(.callout)
                .foregroundColor(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 400)
        }
        .padding(40)
    }
    
    // MARK: - Step 8: Completion
    
    private var completionStep: some View {
        VStack(spacing: 24) {
            // Success icon
            Image(systemName: "hands.clap")
                .font(.system(size: 56))
                .foregroundColor(.accentColor)
                .symbolRenderingMode(.hierarchical)
            
            // Title and description
            VStack(spacing: 12) {
                Text(localized: "onboarding.completion.title")
                    .font(.title)
                    .fontWeight(.semibold)
                
                Text(localized: "onboarding.completion.subtitle")
                    .font(.body)
                    .foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
            }
            
            // Support buttons
            HStack(spacing: 16) {
                Button(action: {
                    if let url = URL(string: "https://discord.gg/hyperwhisper") {
                        NSWorkspace.shared.open(url)
                    }
                }) {
                    Label(LocalizedStringKey("onboarding.completion.discord"), systemImage: "message.fill")
                        .frame(width: 140)
                }
                .controlSize(.large)
                .buttonStyle(.bordered)
                
                Button(action: {
                    if let url = URL(string: "mailto:support@hyperwhisper.com") {
                        NSWorkspace.shared.open(url)
                    }
                }) {
                    Label(LocalizedStringKey("onboarding.completion.email"), systemImage: "envelope.fill")
                        .frame(width: 140)
                }
                .controlSize(.large)
                .buttonStyle(.bordered)
            }
            
            // Additional resources
            VStack(spacing: 8) {
                Text(localized: "onboarding.completion.resources.title")
                    .font(.headline)
                    .foregroundColor(.secondary)
                
                HStack(spacing: 20) {
                    Button {
                        if let url = URL(string: "https://docs.hyperwhisper.com") {
                            NSWorkspace.shared.open(url)
                        }
                    } label: {
                        Text(localized: "onboarding.completion.resources.documentation")
                    }
                    .buttonStyle(.link)
                    
                    Button {
                        if let url = URL(string: "https://youtube.com/@hyperwhisper") {
                            NSWorkspace.shared.open(url)
                        }
                    } label: {
                        Text(localized: "onboarding.completion.resources.videos")
                    }
                    .buttonStyle(.link)
                    
                    Button {
                        if let url = URL(string: "https://blog.hyperwhisper.com") {
                            NSWorkspace.shared.open(url)
                        }
                    } label: {
                        Text(localized: "onboarding.completion.resources.blog")
                    }
                    .buttonStyle(.link)
                }
            }
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
            
            // Skip button (for steps that can be skipped)
            if canSkipCurrentStep() {
                Button {
                    navigateForward()
                } label: {
                    Text(localized: "common.skip")
                }
                .controlSize(.large)
                .buttonStyle(.borderless)
            }
            
            // Continue/Done button
            if currentStep < totalSteps - 1 {
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
        
        // Audio devices are loaded automatically by AudioRecordingManager
        // Set default device
        if let defaultDevice = audioManager.availableDevices.first {
            selectedDeviceId = defaultDevice.id
        }
        
        // Model catalog is loaded automatically in WhisperModelManager init
        // No need to refresh
    }
    
    /// Navigate to previous step
    private func navigateBack() {
        withAnimation {
            currentStep = max(0, currentStep - 1)
        }
    }
    
    /// Navigate to next step
    private func navigateForward() {
        // Save settings as we progress
        saveCurrentStepSettings()
        
        withAnimation {
            currentStep = min(totalSteps - 1, currentStep + 1)
        }
    }
    
    /// Check if current step can be skipped
    private func canSkipCurrentStep() -> Bool {
        switch currentStep {
        case 0: // Accessibility - can skip
            return true
        case 1: // Model download - can skip if a model exists
            return !whisperModelManager.downloadedModels.isEmpty
        default:
            return false
        }
    }
    
    /// Check if can continue from current step
    private func canContinueFromCurrentStep() -> Bool {
        switch currentStep {
        case 1: // Model selection - need at least one model
            return isModelDownloaded() || !whisperModelManager.downloadedModels.isEmpty
        case 3: // Recording permission - need permission
            return hasMicrophonePermission
        case 4: // Test recording - optional but recommended
            return true
        default:
            return true
        }
    }
    
    /// Save settings for current step
    private func saveCurrentStepSettings() {
        switch currentStep {
        case 1: // Model
            // Model selection is handled by mode configuration
            break
        case 2: // Audio device
            if !useSystemDefaultDevice {
                settingsManager.selectedMicrophoneId = selectedDeviceId
            }
        default:
            break
        }
    }
    
    /// Complete onboarding and close
    private func completeOnboarding() {
        // Mark onboarding as completed
        UserDefaults.standard.set(true, forKey: "hasCompletedOnboarding")
        
        // Close onboarding
        isPresented = false
        
        // Navigate to home
        appState.selectedNavigationItem = .home
    }
    
    /// Get recommended model name based on language
    private func getRecommendedModelName() -> String {
        let preferredLanguage = Locale.preferredLanguages.first ?? "en"
        return preferredLanguage.hasPrefix("en") ? "onboarding.model.pro.english".localized : "onboarding.model.pro.multilingual".localized
    }
    
    /// Check if selected model is downloaded
    private func isModelDownloaded() -> Bool {
        return whisperModelManager.downloadedModels.contains { $0.name == selectedModelId }
    }
    
    /// Download selected model
    private func downloadSelectedModel() {
        guard !isDownloadingModel else { return }
        
        let modelId = selectedModelId
        isDownloadingModel = true
        
        Task {
            // Find model in catalog
            if let model = whisperModelManager.availableModels.first(where: { $0.name == modelId }) {
                // Subscribe to progress/completion BEFORE starting the download so the
                // UI updates live. The cancellables are retained in `downloadObservers`
                // so the pipelines outlive this call (a discarded AnyCancellable would
                // tear the subscription down immediately, freezing progress at 0%).
                modelDownloadProgress = 0.0

                downloadObservers.store(whisperModelManager.$downloadProgress
                    .receive(on: DispatchQueue.main)
                    .sink { progressDict in
                        if let progress = progressDict[modelId] {
                            self.modelDownloadProgress = Float(progress)
                        }
                    })

                downloadObservers.store(whisperModelManager.$downloadedModels
                    .receive(on: DispatchQueue.main)
                    .sink { downloadedModels in
                        if downloadedModels.contains(where: { $0.name == modelId }) {
                            self.isDownloadingModel = false
                        }
                    })

                // Start download (returns only after the download finishes).
                await whisperModelManager.downloadModel(model)

                // Download finished: ensure the flag is cleared and tear down the
                // observers so they don't linger across subsequent downloads.
                isDownloadingModel = false
                downloadObservers.removeAll()
            } else {
                errorMessage = "onboarding.error.model.notFound".localized
                showErrorAlert = true
                isDownloadingModel = false
            }
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
        .environmentObject(LicenseManager())
}
