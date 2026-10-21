//
//  ModeEditorView.swift
//  HyperWhisper
//
//  Unified view for creating and editing modes.
//  Consolidates CreateModeView and EditModeView into a single component.
//

import AppKit
import SwiftUI

// MARK: - ModeEditorView

/// Unified view for creating and editing modes.
/// Uses ModeEditorConfiguration to handle create vs edit mode differences.
struct ModeEditorView: View {
    let configuration: ModeEditorConfiguration
    let availableModelIds: [String]
    /// EDIT only: the mode's stored local model when it is not installed. The
    /// picker lists it as not installed so the selection is kept (issue #1434).
    let missingLocalModelId: String?
    /// Every mode in the store (id + stored name), so the editor can refuse a
    /// name another mode already uses (issue #1435). The caller passes its live
    /// fetch, so a mode added while the sheet is open is seen too.
    let existingModes: [ModeNameEntry]
    let onSave: (ModeData) -> Void

    @Environment(\.dismiss) var dismiss
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var whisperModelManager: WhisperModelManager
    @EnvironmentObject var parakeetModelManager: ParakeetModelManager
    @EnvironmentObject var cloudHealth: CloudProviderHealthManager

    // MARK: - State Properties

    @State var name: String
    @State var preset: String
    @State var language: String
    @State var model: String
    @State var provider: ProviderType
    @State var punctuation: Bool
    @State var capitalization: Bool
    @State var profanityFilter: Bool
    @State var customInstructions: String
    @State var languageModel: String
    @State var postProcessingMode: PostProcessingMode
    @State var postProcessingProvider: String
    @State var showCloudModelInfo = false
    @State var cloudProvider: String
    @State var cloudAccuracyTier: String
    @State var cloudPostProcessingModel: String
    @State var cloudTranscriptionModel: String
    @State var cloudTranscriptionDomain: String?
    @State var showAllCloudTranscriptionModels: Bool
    @State var englishSpelling: EnglishSpelling
    @State var userSystemPrompt: String
    @State var removeTrailingPeriod: Bool
    @State var enableScreenOCR: Bool
    @State var geminiCustomPrompt: String

    private var normalizedName: String? {
        ModeNamePolicy.normalized(name)
    }

    /// Set when Save/Create is pressed. A created mode reaches `existingModes`
    /// while the sheet is still animating out, and would otherwise flash the
    /// taken-name message under its own name.
    @State private var hasSubmitted = false

    /// True when another mode already uses this name (issue #1435): Save is
    /// disabled and the field says why. Same rule as the Local API's
    /// `MODE_NAME_TAKEN`; renaming a mode to its own name stays allowed.
    private var nameIsTaken: Bool {
        guard !hasSubmitted else { return false }
        return ModeEditorDefaults.nameIsTaken(
            name,
            editing: configuration.mode.map { ModeNameEntry(id: $0.id, name: $0.name) },
            existingModes: existingModes
        )
    }

    // MARK: - Source-toggle memory
    //
    // The 3-way transcription Source toggle re-derives the persisted
    // provider/cloudProvider fields. Without these, briefly toggling Source
    // away from a saved selection and back would clobber it with a freshly
    // seeded default. We remember the prior selection per-source for the
    // lifetime of the edit session and restore it on toggle-back, only
    // seeding a fresh default when there is genuinely no prior selection.

    /// Last BYOK ("Your provider") transcription provider/model the user had
    /// selected, so toggling away to HW Cloud / On-device and back restores it.
    @State private var lastDirectCloudProvider: String?
    @State private var lastDirectCloudTranscriptionModel: String?

    /// Last HyperWhisper Cloud transcription model + medical domain, so toggling
    /// away and back doesn't reset to the tier default / clear the domain.
    @State private var lastHyperwhisperCloudTranscriptionModel: String?
    @State private var lastHyperwhisperCloudTranscriptionDomain: String?

    // MARK: - Unsaved-changes tracking (issue #1525)
    //
    // The sheet registers with AppState's page guard while it is open, and
    // reports whether its values differ from its baseline. A page change the
    // app makes on its own then keeps the sheet, and a menu-bar History… /
    // Settings… asks before it discards an edit.
    //
    // The baseline follows every change until the user's first key press,
    // click or menu in the app; from then on it is frozen. So the editor's own
    // repairs (on-open clamps, their onChange cascades, however long they
    // take) never read as an edit, and the user's edit is never the baseline,
    // however fast it comes. A repair AFTER the first input (a local model
    // download finishing) does read as an edit: an extra prompt, not a lost
    // edit.

    /// This sheet's registration with AppState's page guard.
    @State var editorSession = UUID()

    /// The values a clean editor holds; nil until the editor appears.
    @State var settledSnapshot: ModeEditorSnapshot?

    /// Set by the first key press, click or menu in the app while the editor
    /// is open; it freezes `settledSnapshot`.
    @State var userHasInteracted = false

    /// Watches for that first input; removed once it comes or on close.
    @State private var userInputMonitor: Any?

    /// Escape → Keep Editing while the discard prompt is up. A button holds
    /// one key: Keep Editing holds Return, so the alert gives Escape to no one.
    @State private var discardPromptEscapeMonitor: Any?

    // MARK: - Initialization

    /// `licenseActive` is read only by the CREATE branch, to pick the provider a
    /// new mode opens on (issue #873). The EDIT branch ignores it.
    init(
        configuration: ModeEditorConfiguration,
        availableModelIds: [String],
        existingModes: [ModeNameEntry] = [],
        licenseActive: Bool = false,
        onSave: @escaping (ModeData) -> Void
    ) {
        self.configuration = configuration
        self.availableModelIds = availableModelIds
        self.existingModes = existingModes
        self.onSave = onSave

        if let mode = configuration.mode {
            // EDIT MODE: Initialize from existing Mode object. The values, and
            // every migration they need (legacy preset, language code, stored
            // model, legacy Azure/Google provider), come from the pure
            // ModeEditorSeed resolver (issue #730).
            let seed = ModeEditorSeed.make(mode: mode, availableModelIds: availableModelIds)
            _name = State(initialValue: seed.name)
            _preset = State(initialValue: seed.preset)
            _language = State(initialValue: seed.language)
            self.missingLocalModelId = seed.missingLocalModelId
            _model = State(initialValue: seed.model)
            _provider = State(initialValue: seed.provider)
            _punctuation = State(initialValue: seed.punctuation)
            _capitalization = State(initialValue: seed.capitalization)
            _profanityFilter = State(initialValue: seed.profanityFilter)
            _customInstructions = State(initialValue: seed.customInstructions)
            _languageModel = State(initialValue: seed.languageModel)
            _postProcessingMode = State(initialValue: seed.postProcessingMode)
            _postProcessingProvider = State(initialValue: seed.postProcessingProvider)
            _cloudProvider = State(initialValue: seed.cloudProvider)
            _cloudTranscriptionModel = State(initialValue: seed.cloudTranscriptionModel)
            _showAllCloudTranscriptionModels = State(initialValue: seed.showAllCloudTranscriptionModels)
            _englishSpelling = State(initialValue: seed.englishSpelling)
            _userSystemPrompt = State(initialValue: seed.userSystemPrompt)
            _cloudAccuracyTier = State(initialValue: seed.cloudAccuracyTier)
            _cloudPostProcessingModel = State(initialValue: seed.cloudPostProcessingModel)
            _removeTrailingPeriod = State(initialValue: seed.removeTrailingPeriod)
            _enableScreenOCR = State(initialValue: seed.enableScreenOCR)
            _geminiCustomPrompt = State(initialValue: seed.geminiCustomPrompt)
            _cloudTranscriptionDomain = State(initialValue: seed.cloudTranscriptionDomain)
        } else {
            // CREATE MODE: Initialize with defaults
            // The seeds that depend on the licence and the installed models come
            // from the pure ModeEditorDefaults resolver (issue #873).
            self.missingLocalModelId = nil
            let seededProvider = ModeEditorDefaults.initialProvider(
                licenseActive: licenseActive,
                availableModelIds: availableModelIds
            )
            let seededModel = ModeEditorDefaults.initialLocalModel(availableModelIds: availableModelIds)
            _name = State(initialValue: "")
            _preset = State(initialValue: "hyper")
            _language = State(initialValue: ModeEditorDefaults.initialLanguage(
                provider: seededProvider,
                model: seededModel
            ))
            _punctuation = State(initialValue: true)
            _capitalization = State(initialValue: true)
            _profanityFilter = State(initialValue: false)
            _customInstructions = State(initialValue: "")
            _languageModel = State(initialValue: PostProcessingModels.defaultModel(for: .openai)?.id ?? "gpt-5.6-luna")
            _postProcessingMode = State(initialValue: ModeEditorDefaults.initialPostProcessingMode(
                licenseActive: licenseActive,
                availableModelIds: availableModelIds
            ))
            _postProcessingProvider = State(initialValue: PostProcessingProvider.hyperwhisper.storageValue)
            _cloudProvider = State(initialValue: "hyperwhisper")
            // Recommended HyperWhisper Cloud defaults: ElevenLabs Scribe v2
            // transcription + Anthropic Claude Haiku 4.5 post-processing. Seed the
            // transcription model to the recommended tier's catalog default so the
            // Model dropdown opens on "Scribe v2 (Recommended)".
            _cloudTranscriptionModel = State(initialValue: CloudAccuracyTier.elevenLabsScribeV2.defaultModelId)
            _showAllCloudTranscriptionModels = State(initialValue: false)
            // Seed the spelling variant from the system region so a brand-new
            // mode opens on the user's own variant instead of always American.
            _englishSpelling = State(initialValue: .defaultForCurrentRegion)
            _userSystemPrompt = State(initialValue: "")
            _cloudAccuracyTier = State(initialValue: CloudAccuracyTier.elevenLabsScribeV2.rawValue)
            _cloudPostProcessingModel = State(initialValue: CloudPostProcessingModel.claudeHaiku.rawValue)
            _removeTrailingPeriod = State(initialValue: false)
            _enableScreenOCR = State(initialValue: false)
            _geminiCustomPrompt = State(initialValue: "")
            _cloudTranscriptionDomain = State(initialValue: nil)

            // HyperWhisper Cloud for a licensed user or when no local model is
            // installed; otherwise On-device, so an unlicensed on-device setup
            // is not switched to Cloud by creating a mode (issue #873).
            _provider = State(initialValue: seededProvider)

            // Local model, in the On-device picker's own order.
            _model = State(initialValue: seededModel)
        }
    }

    // MARK: - Computed Properties

    /// Rows of the On-device Model picker: installed model ids in picker order
    /// (non-Whisper first, then Whisper in `WhisperModel` order; the CREATE seed
    /// uses the same table), preceded by the mode's own missing model when it
    /// has one (issue #1434).
    private func localPickerModelIds() -> [String] {
        ModeEditorDefaults.localPickerModelIds(
            availableModelIds: availableModelIds,
            missingLocalModelId: missingLocalModelId
        )
    }

    /// True while the On-device selection is the mode's missing model.
    private var isShowingMissingLocalModel: Bool {
        provider == .local && missingLocalModelId != nil && model == missingLocalModelId
    }

    /// Picker row label: the missing model is marked as not installed.
    private func localPickerLabel(for id: String) -> String {
        let name = displayName(for: id)
        guard id == missingLocalModelId else { return name }
        return "modes.model.notInstalledFormat".localized(arguments: name)
    }

    /// 3-way Source axis derived from the persisted `provider`/`cloudProvider`
    /// fields. The setter re-applies the choice and seeds dependent fields
    /// (mirrors the old `cloudProvider` onChange seeding logic).
    var transcriptionSource: Binding<TranscriptionSource> {
        Binding(
            get: {
                if provider == .local { return .onDevice }
                return currentCloudProvider == .hyperwhisper ? .hyperwhisperCloud : .yourProvider
            },
            set: { newSource in
                // Remember the selection we're leaving so toggling back restores
                // it instead of reseeding a default. The transcription Source must
                // NOT touch the post-processing provider — post-processing source is
                // an independent axis owned by ModePostProcessingSettings.
                rememberCurrentTranscriptionSelection()

                switch newSource {
                case .onDevice:
                    provider = .local
                    // Back on On-device, a local mode's own missing model is
                    // kept, not swapped for the first installed one (issue #1434).
                    model = ModeEditorDefaults.onDeviceModel(
                        current: model,
                        availableModelIds: availableModelIds,
                        missingLocalModelId: missingLocalModelId
                    )
                case .hyperwhisperCloud:
                    provider = .cloud
                    cloudProvider = CloudProvider.hyperwhisper.rawValue
                    showAllCloudTranscriptionModels = false
                    // Restore a previously-chosen HW Cloud model/domain if we have
                    // one; otherwise seed the selected tier's catalog default and
                    // clear any medical domain (mirrors the cloudProvider onChange).
                    if let savedModel = lastHyperwhisperCloudTranscriptionModel {
                        cloudTranscriptionModel = savedModel
                        cloudTranscriptionDomain = lastHyperwhisperCloudTranscriptionDomain
                    } else {
                        cloudTranscriptionModel = CloudAccuracyTier.fromStorageValue(cloudAccuracyTier).defaultModelId
                        cloudTranscriptionDomain = nil
                    }
                case .yourProvider:
                    provider = .cloud
                    showAllCloudTranscriptionModels = false
                    // Restore a previously-chosen BYOK provider/model if we have a
                    // usable one; otherwise seed the first available direct provider.
                    // This keeps a saved Deepgram/Groq/etc. choice intact across a
                    // harmless Source toggle away and back.
                    if let savedProviderRaw = lastDirectCloudProvider,
                       let savedProvider = CloudProvider.parse(savedProviderRaw),
                       availableDirectCloudProviders.contains(savedProvider) {
                        cloudProvider = savedProviderRaw
                        cloudTranscriptionModel = lastDirectCloudTranscriptionModel
                            ?? CloudTranscriptionModels.defaultModel(for: savedProvider)
                    } else {
                        let current = currentCloudProvider
                        if current == .hyperwhisper || !availableDirectCloudProviders.contains(current) {
                            let firstDirect = availableDirectCloudProviders.first ?? .openai
                            cloudProvider = firstDirect.rawValue
                            cloudTranscriptionModel = CloudTranscriptionModels.defaultModel(for: firstDirect)
                        }
                    }
                    cloudTranscriptionDomain = nil
                }
            }
        )
    }

    /// Snapshot the active transcription selection into the per-source memory
    /// before the Source toggle switches away, so toggling back restores it.
    private func rememberCurrentTranscriptionSelection() {
        guard provider == .cloud else { return }
        if currentCloudProvider == .hyperwhisper {
            lastHyperwhisperCloudTranscriptionModel = cloudTranscriptionModel
            lastHyperwhisperCloudTranscriptionDomain = cloudTranscriptionDomain
        } else {
            lastDirectCloudProvider = cloudProvider
            lastDirectCloudTranscriptionModel = cloudTranscriptionModel
        }
    }

    // MARK: - Sheet sizing

    /// The visible height of the screen the sheet's parent window is on.
    ///
    /// `MainWindowStore.window` is the repo's own handle on that window
    /// (`hyperwhisperApp.swift`, written by `WindowConfigurator`, read the same
    /// way in three other places). It is the app's ONLY sheet parent, and unlike
    /// `NSApp.keyWindow` it neither changes when a recording panel or a progress
    /// popup takes key, nor goes nil while the app is inactive. Read once per
    /// body evaluation (`body` hoists it into a `let`); it does not follow a drag
    /// to another display, which is acceptable for a modal sheet.
    private var maxSheetHeight: CGFloat {
        let hostWindow = MainWindowStore.window
        return Self.sheetMaxHeight(
            visibleScreenHeight: (hostWindow?.screen ?? NSScreen.main)?.visibleFrame.height
        )
    }

    // MARK: - Body

    var body: some View {
        // One AppKit walk per body pass; the two frame arguments then agree.
        let maxHeight = maxSheetHeight

        VStack(spacing: 0) {
            editorHeader
            editorContent
                .padding(.top, 8)
            Divider()
            editorFooter
        }
        .frame(width: 480)
        // Bounded instead of a hard 700: the fixed height drew the footer
        // (Cancel / Create / Delete) below the bottom of the screen's visible
        // area, and the window cannot be resized to reach it (issue #711).
        // `idealHeight` holds the sheet at its full allowance.
        .frame(
            minHeight: Self.sheetMinHeight,
            idealHeight: maxHeight,
            maxHeight: maxHeight
        )
        .background(Color(NSColor.windowBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .onAppear {
            // Ensure model selection is valid to avoid Picker selection warnings.
            // The mode's own missing model IS a picker row, so it is kept: this
            // clamp used to swap it for the first installed model (or flip the
            // mode to Cloud), and Save then wrote the substitute (issue #1434).
            if provider == .local {
                let pickerIds = localPickerModelIds()
                if pickerIds.isEmpty {
                    provider = .cloud
                } else if !pickerIds.contains(model) {
                    model = ModeEditorDefaults.onDeviceModel(
                        current: model,
                        availableModelIds: availableModelIds,
                        missingLocalModelId: missingLocalModelId
                    )
                }
            }

            repairCloudSelectionOnAppear()
        }
        .onChange(of: postProcessingMode) { _, newValue in
            switch newValue {
            case .cloud:
                if let provider = PostProcessingProvider(rawValue: postProcessingProvider), provider.isLocal {
                    postProcessingProvider = PostProcessingProvider.openai.rawValue
                }
                if let provider = PostProcessingProvider(rawValue: postProcessingProvider),
                   PostProcessingModels.model(withId: languageModel, provider: provider) == nil,
                   let defaultModel = PostProcessingModels.defaultModel(for: provider) {
                    languageModel = defaultModel.id
                }
            case .local:
                postProcessingProvider = PostProcessingProvider.localLLM.rawValue
                if PostProcessingModels.model(withId: languageModel, provider: .localLLM) == nil {
                    languageModel = PostProcessingProvider.localLLM.defaultModel
                }
            case .off:
                punctuation = false
                capitalization = false
            }
        }
        .onChange(of: model) { newModel in
            // AUTO-SET LANGUAGE FOR ENGLISH-ONLY MODELS
            // When user selects an .en model, automatically switch to English
            if isEnglishOnlyModel(provider: provider, model: newModel) {
                language = "en"
            }
        }
        // Issue #1525: register with the page guard, and report unsaved changes.
        .onAppear {
            appState.modeEditorDidOpen(session: editorSession)
            settledSnapshot = currentSnapshot
            installUserInputMonitor()
        }
        .onDisappear {
            removeDiscardPromptEscapeMonitor()
            removeUserInputMonitor()
            appState.modeEditorDidClose(session: editorSession)
        }
        .onChange(of: currentSnapshot) { _, current in
            settledSnapshot = ModeEditorSnapshot.settled(
                previous: settledSnapshot,
                current: current,
                userHasInteracted: userHasInteracted
            )
        }
        // Edit > Paste from the menu bar sends no event the monitor sees.
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didBeginTrackingNotification)) { _ in
            markUserInteracted()
        }
        .onChange(of: hasUnsavedChanges) { _, unsaved in
            appState.modeEditor(session: editorSession, hasUnsavedChanges: unsaved)
        }
        .onChange(of: discardPromptIsPresented.wrappedValue) { _, presented in
            if presented {
                installDiscardPromptEscapeMonitor()
            } else {
                removeDiscardPromptEscapeMonitor()
            }
        }
        .alert("modes.editor.discard.title".localized, isPresented: discardPromptIsPresented) {
            // Keep Editing is the default: Return presses it, and Escape
            // reaches it through discardPromptEscapeMonitor. Only Discard
            // leaves.
            Button(role: .cancel) {
                appState.keepEditingModeEditor()
            } label: {
                Text(localized: "modes.editor.discard.keepEditing")
            }
            .keyboardShortcut(.defaultAction)
            Button(role: .destructive) {
                appState.discardModeEditorAndNavigate()
            } label: {
                Text(localized: "modes.editor.discard.button")
            }
        } message: {
            Text(localized: "modes.editor.discard.message")
        }
    }

    // MARK: - First user input

    /// Any key press (including a modifier, e.g. a dictation shortcut) or
    /// click in the app counts. The monitor runs before the event reaches the
    /// control, so the edit that event makes already compares against a frozen
    /// baseline.
    private func installUserInputMonitor() {
        guard userInputMonitor == nil else { return }
        userInputMonitor = NSEvent.addLocalMonitorForEvents(
            matching: [.keyDown, .flagsChanged, .leftMouseDown, .rightMouseDown, .otherMouseDown]
        ) { event in
            markUserInteracted()
            return event
        }
    }

    private func markUserInteracted() {
        if !userHasInteracted {
            userHasInteracted = true
        }
    }

    private func removeUserInputMonitor() {
        if let monitor = userInputMonitor {
            NSEvent.removeMonitor(monitor)
            userInputMonitor = nil
        }
    }

    // MARK: - Discard prompt Escape

    /// A plain Escape while the prompt is up presses Keep Editing.
    private func installDiscardPromptEscapeMonitor() {
        guard discardPromptEscapeMonitor == nil else { return }
        let state = appState
        discardPromptEscapeMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            let modifiers = event.modifierFlags.intersection([.command, .option, .control, .shift])
            guard event.keyCode == 53, modifiers.isEmpty,
                  state.pendingModeEditorNavigation != nil else { return event }
            state.keepEditingModeEditor()
            return nil
        }
    }

    private func removeDiscardPromptEscapeMonitor() {
        if let monitor = discardPromptEscapeMonitor {
            NSEvent.removeMonitor(monitor)
            discardPromptEscapeMonitor = nil
        }
    }

    // MARK: - Header

    private var editorHeader: some View {
        VStack(spacing: 8) {
            Text(localized: configuration.isEditMode ? "modes.edit.title" : "modes.create.title")
                .font(.title2)
                .fontWeight(.bold)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(localized: configuration.isEditMode ? "modes.edit.subtitle" : "modes.create.subtitle")
                .font(.caption)
                .foregroundColor(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(20)
        .background(Color(NSColor.controlBackgroundColor))
    }

    // MARK: - Basic Settings

    var editorBasicSettings: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 16) {
                Label(LocalizedStringKey("modes.section.basic"), systemImage: "gear")
                    .font(.headline)
                    .foregroundColor(.secondary)

                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text(localized: "modes.field.name")
                            .frame(width: 80, alignment: .leading)
                        TextField(
                            LocalizedStringKey(configuration.isEditMode ? "modes.field.name.editPlaceholder" : "modes.field.name.placeholder"),
                            text: $name
                        )
                        .textFieldStyle(.roundedBorder)
                        .disabled(isDefaultMode)
                        // The reason travels with the field, not only as the label
                        // below it: VoiceOver reaches a disabled control but has no
                        // way to associate a loose sibling Text with it.
                        .accessibilityHint(isDefaultMode ? Text(localized: "modes.field.name.defaultLocked") : Text(""))
                    }

                    // A disabled field with nothing beside it reads as a broken
                    // control rather than a deliberate rule (issue #494).
                    if isDefaultMode {
                        Text(localized: "modes.field.name.defaultLocked")
                            .font(.caption)
                            .foregroundColor(.secondary)
                            .padding(.leading, 88)
                    }

                    // Another mode already uses this name (issue #1435). Save
                    // is disabled while it shows.
                    if nameIsTaken {
                        Text(localized: "modes.field.name.taken")
                            .font(.caption)
                            .foregroundColor(.red)
                            .padding(.leading, 88)
                            .accessibilityIdentifier("modeEditor.nameTaken")
                    }
                }
            }
            .padding(12)
        }
    }

    // MARK: - On-device transcription

    @ViewBuilder
    var editorLocalTranscription: some View {
        if localPickerModelIds().isEmpty {
            noLocalModelsNotice
        } else {
            VStack(alignment: .leading, spacing: 12) {
                // Model selection
                HStack {
                    Text(localized: "modes.field.model")
                        .frame(width: 80, alignment: .leading)
                    Picker("", selection: $model) {
                        ForEach(localPickerModelIds(), id: \.self) { mid in
                            Text(localPickerLabel(for: mid)).tag(mid)
                        }
                    }
                    .pickerStyle(.menu)
                    .labelsHidden()
                    Spacer()
                }

                // The mode's stored model is not installed (issue #1434). Save
                // keeps it; the user can download it or pick another model.
                if isShowingMissingLocalModel {
                    missingLocalModelNotice
                }

                // Language selection for local
                LanguageSelectionView(
                    language: $language,
                    provider: .local,
                    model: model
                )

                if showVocabularyUnsupportedNotice {
                    VocabularyUnsupportedNotice()
                }
            }
        }
    }

    // MARK: - Your provider (BYOK) key notice

    /// Shown under the BYOK section while the selected provider's key is
    /// missing or unhealthy; Manage in Library closes the sheet first.
    var yourProviderNeedsKeyNotice: some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.caption)
                .foregroundColor(.orange)
            Text("modes.provider.needsKey".localized(arguments: currentCloudProvider.displayName))
                .font(.caption)
                .foregroundColor(.primary)
            Spacer()
            Button {
                dismiss()
                DispatchQueue.main.async {
                    appState.navigateToModelLibraryAPIKeys(trigger: .fromModeEditor)
                }
            } label: {
                Text(localized: "modes.provider.manageInLibrary")
            }
            .buttonStyle(.link)
            .font(.caption)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.orange.opacity(0.15))
        )
    }

    // MARK: - Footer

    private var editorFooter: some View {
        HStack(spacing: 12) {
            if configuration.isEditMode {
                // Edit mode: Delete button on the left
                Button(role: .destructive) {
                    dismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
                        if let deleteAction = configuration.onDelete { deleteAction() }
                    }
                } label: {
                    Label(LocalizedStringKey("modes.button.delete"), systemImage: "trash")
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .tint(.red)
            }

            Spacer()

            // Cancel button
            Button {
                dismiss()
            } label: {
                Text(localized: "common.cancel")
            }
            .keyboardShortcut(.cancelAction)
            .controlSize(.large)

            // Save/Create button
            Button {
                guard hasValidDictationLanguage, !nameIsTaken else { return }
                hasSubmitted = true
                let chosenModel = ModeEditorDefaults.savedModel(
                    provider: provider,
                    model: model,
                    availableModelIds: availableModelIds
                )
                let finalLanguage = ModeEditorDefaults.savedLanguage(
                    provider: provider,
                    savedModel: chosenModel,
                    language: language
                )
                let modeData = ModeData(
                    id: configuration.mode?.id ?? UUID(),
                    name: normalizedName ?? name,
                    preset: preset,
                    language: finalLanguage,
                    model: chosenModel,
                    punctuation: punctuation,
                    capitalization: capitalization,
                    profanityFilter: profanityFilter,
                    customInstructions: customInstructions,
                    languageModel: languageModel,
                    cloudProvider: cloudProvider,
                    cloudTranscriptionModel: cloudTranscriptionModel,
                    postProcessingMode: postProcessingMode,
                    postProcessingProvider: postProcessingProvider,
                    englishSpelling: englishSpelling,
                    userSystemPrompt: userSystemPrompt,
                    useStreamingTranscription: false,  // Streaming is now a separate feature, not part of modes
                    cloudAccuracyTier: CloudAccuracyTier.fromStorageValue(cloudAccuracyTier),
                    removeTrailingPeriod: removeTrailingPeriod,
                    enableScreenOCR: enableScreenOCR,
                    geminiCustomPrompt: geminiCustomPrompt,
                    cloudPostProcessingModel: CloudPostProcessingModel.fromStorageValue(cloudPostProcessingModel),
                    // Only persist a domain when the selected tier actually uses
                    // one (assemblyAI Medical Mode). Gating on the tier — not merely
                    // provider == hyperwhisper — keeps a stale "medical" domain from
                    // leaking to other tiers via backup/PATCH after the user switches.
                    cloudTranscriptionDomain: showsMedicalDomainToggle ? cloudTranscriptionDomain : nil
                )
                onSave(modeData)
            } label: {
                Text(localized: configuration.isEditMode ? "modes.button.save" : "modes.button.create")
            }
            .keyboardShortcut(.defaultAction)
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(normalizedName == nil || nameIsTaken || !hasValidDictationLanguage || (provider == .local && localPickerModelIds().isEmpty))
        }
        .padding(20)
        .background(Color(NSColor.controlBackgroundColor))
    }
}
