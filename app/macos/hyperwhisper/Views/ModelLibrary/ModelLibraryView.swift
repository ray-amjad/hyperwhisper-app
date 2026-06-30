//
//  ModelLibraryView.swift
//  hyperwhisper
//

import SwiftUI

struct ModelLibraryView: View {
    @EnvironmentObject var library: ModelLibraryManager
    @EnvironmentObject var apiKeys: APIKeySettingsManager
    @EnvironmentObject var cloudHealth: CloudProviderHealthManager
    @EnvironmentObject var whisperManager: WhisperModelManager
    @EnvironmentObject var parakeetManager: ParakeetModelManager
    @EnvironmentObject var qwen3AsrManager: Qwen3AsrModelManager
    @EnvironmentObject var nemotronManager: NemotronModelManager
    @EnvironmentObject var localLLMManager: LocalModelManager
    @EnvironmentObject var licenseManager: LicenseManager
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var customEndpointManager: CustomPostProcessingManager

    @State private var searchText: String = ""
    @State private var providerFilter: ProviderFilter = .all
    @State private var typeFilter: TypeFilter = .all
    @State private var locationFilter: LocationFilter = .all
    @State private var vocabFilter: Bool = false
    @State private var cloudAvailableFilter: Bool = false
    /// Persisted base language code for the library filter; "" = Any language.
    /// Restored on next open per the spec.
    @AppStorage("modelLibraryLanguageFilter") private var languageFilter: String = LibraryLanguageFilter.anyCode
    @State private var sortColumn: SortColumn? = nil
    @State private var sortAscending: Bool = false

    enum SortColumn { case name, type, rating, location }

    @State private var sheetTarget: ProviderKeyTarget?
    @State private var sheetMode: ProviderKeySheetMode = .connect
    @State private var showAPIKeysManager = false
    @State private var showingModelInUseAlert = false
    @State private var modelInUseMessage = ""
    @State private var showCustomEndpointSheet = false
    @State private var editingEndpoint: CustomPostProcessingEndpoint?

    enum ProviderFilter: String, CaseIterable, Identifiable {
        case all
        case openai = "OpenAI"
        case anthropic = "Anthropic"
        case groq = "Groq"
        case deepgram = "Deepgram"
        case gemini = "Gemini"
        case local = "Local"
        var id: String { rawValue }
        var label: String { self == .all ? "All providers" : rawValue }

        func matches(_ key: LibraryProviderKey) -> Bool {
            switch self {
            case .all:
                return true
            case .openai:
                return key == .cloud(.openai) || key == .postProcessing(.openai)
            case .anthropic:
                return key == .postProcessing(.anthropic)
            case .groq:
                return key == .cloud(.groq) || key == .postProcessing(.groq)
            case .deepgram:
                return key == .cloud(.deepgram)
            case .gemini:
                return key == .cloud(.gemini) || key == .postProcessing(.gemini)
            case .local:
                switch key {
                case .appleSpeech, .localWhisper, .parakeet, .qwen3ASR, .nemotron, .postProcessing(.localLLM):
                    return true
                default:
                    return false
                }
            }
        }
    }

    enum TypeFilter: String, CaseIterable, Identifiable {
        case all
        case voice
        case text
        var id: String { rawValue }
        var label: String {
            switch self {
            case .all: return "All types"
            case .voice: return "Voice"
            case .text: return "Text"
            }
        }
        var chipLabel: String? {
            switch self {
            case .all: return nil
            case .voice: return "Voice models"
            case .text: return "Language models"
            }
        }
    }

    enum LocationFilter: String, CaseIterable, Identifiable {
        case all
        case cloud
        case offline
        var id: String { rawValue }
        var label: String {
            switch self {
            case .all: return "Cloud & offline"
            case .cloud: return "Cloud only"
            case .offline: return "Offline only"
            }
        }
        var chipLabel: String? {
            switch self {
            case .all: return nil
            case .cloud: return "Cloud"
            case .offline: return "Offline"
            }
        }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                searchBar
                filterStrip
                table
                legend
                customEndpointsCard
            }
            .padding(.vertical, 24)
            .padding(.horizontal, 28)
        }
        .background(Color(NSColor.windowBackgroundColor).opacity(0.6))
        .onAppear {
            cloudHealth.refreshAll()
            cloudHealth.refreshAllPostProcessing()
            whisperManager.scanDownloadedModels()
            parakeetManager.refreshState()
            qwen3AsrManager.refreshState()
            if #available(macOS 14.0, *) {
                nemotronManager.refreshState()
            }
            localLLMManager.refreshCatalog()
            openAPIKeysManagerIfRequested()
        }
        .onChange(of: appState.shouldOpenModelLibraryAPIKeys) { _, _ in
            openAPIKeysManagerIfRequested()
        }
        .sheet(item: $sheetTarget) { target in
            ProviderKeySheet(target: target, mode: sheetMode)
                .environmentObject(apiKeys)
                .environmentObject(cloudHealth)
        }
        .sheet(isPresented: $showAPIKeysManager) {
            APIKeysManagerModal()
                .environmentObject(apiKeys)
                .environmentObject(cloudHealth)
        }
        .sheet(isPresented: $showCustomEndpointSheet, onDismiss: { editingEndpoint = nil }) {
            CustomEndpointSheet(existingEndpoint: editingEndpoint, onSave: { _ in })
                .environmentObject(customEndpointManager)
        }
        .alert(LocalizedStringKey("settings.models.alert.cannotDelete.title"), isPresented: $showingModelInUseAlert) {
            Button(LocalizedStringKey("common.ok"), role: .cancel) {}
        } message: {
            Text(modelInUseMessage)
        }
    }

    // MARK: - Sections

    private var searchBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .foregroundColor(.secondary)
            TextField("Search models", text: $searchText)
                .textFieldStyle(.plain)
                .font(.system(size: 13))

            if let chip = typeFilter.chipLabel {
                FilterChip(label: chip) { typeFilter = .all }
            }
            if let chip = locationFilter.chipLabel {
                FilterChip(label: chip) { locationFilter = .all }
            }
            if vocabFilter {
                FilterChip(label: "Custom vocabulary") { vocabFilter = false }
            }
            if cloudAvailableFilter {
                FilterChip(label: "On HyperWhisper Cloud") { cloudAvailableFilter = false }
            }

            Menu {
                Section("Type") {
                    filterMenuItem("Voice models", isOn: typeFilter == .voice) {
                        typeFilter = (typeFilter == .voice) ? .all : .voice
                    }
                    filterMenuItem("Language models", isOn: typeFilter == .text) {
                        typeFilter = (typeFilter == .text) ? .all : .text
                    }
                }
                Section("Location") {
                    filterMenuItem("Cloud", isOn: locationFilter == .cloud) {
                        locationFilter = (locationFilter == .cloud) ? .all : .cloud
                    }
                    filterMenuItem("Offline", isOn: locationFilter == .offline) {
                        locationFilter = (locationFilter == .offline) ? .all : .offline
                    }
                }
                Section("Features") {
                    filterMenuItem("Supports custom vocabulary", isOn: vocabFilter) {
                        vocabFilter.toggle()
                    }
                    filterMenuItem("Available on HyperWhisper Cloud", isOn: cloudAvailableFilter) {
                        cloudAvailableFilter.toggle()
                    }
                }
            } label: {
                Image(systemName: "line.3.horizontal.decrease")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundColor(.secondary)
                    .frame(width: 22, height: 22)
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("Filter")
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.05)))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(Color.primary.opacity(0.06), lineWidth: 0.5))
    }

    private func openAPIKeysManagerIfRequested() {
        guard appState.shouldOpenModelLibraryAPIKeys else { return }
        appState.shouldOpenModelLibraryAPIKeys = false
        showAPIKeysManager = true
    }

    private var filterStrip: some View {
        HStack(spacing: 10) {
            Menu {
                ForEach(ProviderFilter.allCases) { f in
                    Button {
                        providerFilter = f
                    } label: {
                        if providerFilter == f {
                            Label(f.label, systemImage: "checkmark")
                        } else {
                            Text(f.label)
                        }
                    }
                }
            } label: {
                HStack(spacing: 4) {
                    Text(providerFilter.label).font(.system(size: 12))
                    Image(systemName: "chevron.down").font(.system(size: 9, weight: .semibold))
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.08)))
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()

            languageMenu

            if let summary = languageFilterSummary {
                HStack(spacing: 6) {
                    Text("\(summary.supported) of \(summary.total) support \(summary.name)")
                        .font(.system(size: 11))
                        .foregroundColor(.secondary)
                    Button("Show all") { languageFilter = LibraryLanguageFilter.anyCode }
                        .buttonStyle(.plain)
                        .font(.system(size: 11))
                        .foregroundColor(.accentColor)
                }
                .fixedSize()
            }

            Spacer()

            Button {
                showAPIKeysManager = true
            } label: {
                Image(systemName: "key.fill")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(.primary)
                    .frame(width: 26, height: 22)
                    .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.08)))
            }
            .buttonStyle(.plain)
            .help("Manage API keys")
        }
    }

    private var languageMenu: some View {
        let active = !languageFilter.isEmpty
        let label = active
            ? (LibraryLanguageFilter.languages.first { $0.code == languageFilter }?.displayName ?? languageFilter)
            : "Language"
        return Menu {
            Button {
                languageFilter = LibraryLanguageFilter.anyCode
            } label: {
                if languageFilter.isEmpty {
                    Label("Any language", systemImage: "checkmark")
                } else {
                    Text("Any language")
                }
            }
            Divider()
            ForEach(LibraryLanguageFilter.languages) { lang in
                Button {
                    languageFilter = lang.code
                } label: {
                    if languageFilter == lang.code {
                        Label(lang.displayName, systemImage: "checkmark")
                    } else {
                        Text(lang.displayName)
                    }
                }
            }
        } label: {
            HStack(spacing: 4) {
                Image(systemName: "globe").font(.system(size: 10, weight: .medium))
                Text(label).font(.system(size: 12))
                Image(systemName: "chevron.down").font(.system(size: 9, weight: .semibold))
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(
                RoundedRectangle(cornerRadius: 6)
                    .fill(active ? Color.accentColor.opacity(0.18) : Color.primary.opacity(0.08))
            )
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Filter models by language")
    }

    private var table: some View {
        let rows = visibleModels
        return LazyVStack(spacing: 0) {
            tableHeader
            Divider()
            if rows.isEmpty {
                Text("No models match your filters")
                    .foregroundColor(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 40)
            } else {
                let lastID = rows.last?.id
                ForEach(rows) { model in
                    ModelRow(
                        model: model,
                        onLockTap: { handleLockTap(model) },
                        onCloudTap: { handleCloudTap(model) },
                        onCancelTap: cancelHandler(for: model)
                    )
                    if model.id != lastID {
                        Divider().padding(.leading, 14)
                    }
                }
            }
        }
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.04)))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(Color.primary.opacity(0.08), lineWidth: 0.5))
    }

    @ViewBuilder
    private func filterMenuItem(_ title: String, isOn: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            if isOn {
                Label(title, systemImage: "checkmark")
            } else {
                Text(title)
            }
        }
    }

    private var tableHeader: some View {
        HStack(spacing: 14) {
            sortableHeader("Model name", column: .name,
                           width: nil, alignment: .leading)
            sortableHeader("Type", column: .type,
                           width: 50, alignment: .leading)
            sortableHeader("Speed / Accuracy", column: .rating,
                           width: 110, alignment: .leading)
            sortableHeader("Cloud / Offline", column: .location,
                           width: 130, alignment: .trailing)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
    }

    @ViewBuilder
    private func sortableHeader(_ title: String, column: SortColumn,
                                width: CGFloat?, alignment: Alignment) -> some View {
        let isActive = (sortColumn == column)
        let label = HStack(spacing: 3) {
            if alignment == .trailing { Spacer(minLength: 0) }
            Text(title)
                .font(.caption)
                .foregroundColor(isActive ? .primary : .secondary)
            if isActive {
                Image(systemName: sortAscending ? "chevron.up" : "chevron.down")
                    .font(.system(size: 8, weight: .bold))
                    .foregroundColor(.primary)
            }
            if alignment != .trailing { Spacer(minLength: 0) }
        }
        let frameWidth = width
        Button {
            toggleSort(column)
        } label: {
            Group {
                if let frameWidth {
                    label.frame(width: frameWidth, alignment: alignment)
                } else {
                    label.frame(maxWidth: .infinity, alignment: alignment)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Sort by \(title)")
    }

    private func toggleSort(_ column: SortColumn) {
        if sortColumn == column {
            sortAscending.toggle()
        } else {
            sortColumn = column
            sortAscending = false
        }
    }

    private var customEndpointsCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("OpenAI-compatible endpoints")
                        .font(.headline)
                    Text("Bring your own server. Each entry is a URL + model that can be used for post-processing.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
                Spacer()
                Button {
                    editingEndpoint = nil
                    showCustomEndpointSheet = true
                } label: {
                    Label("Add endpoint", systemImage: "plus.circle")
                }
            }
            .padding(12)

            if !customEndpointManager.endpoints.isEmpty {
                Divider()
                ForEach(customEndpointManager.endpoints) { endpoint in
                    customEndpointRow(endpoint)
                    if endpoint.id != customEndpointManager.endpoints.last?.id {
                        Divider().padding(.leading, 12)
                    }
                }
            }
        }
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.04)))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(Color.primary.opacity(0.08), lineWidth: 0.5))
        .padding(.top, 8)
    }

    private func customEndpointRow(_ endpoint: CustomPostProcessingEndpoint) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(endpoint.name)
                        .font(.system(size: 13, weight: .medium))
                    if let success = endpoint.lastTestSuccess {
                        Image(systemName: success ? "checkmark.circle.fill" : "xmark.circle.fill")
                            .font(.caption)
                            .foregroundColor(success ? .green : .red)
                    }
                }
                Text("\(endpoint.displayURL) • \(endpoint.modelName)")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            Spacer()
            Button { editingEndpoint = endpoint; showCustomEndpointSheet = true } label: {
                Image(systemName: "pencil")
            }
            .buttonStyle(.plain)
            .help("Edit endpoint")
            Button {
                try? customEndpointManager.duplicateEndpoint(id: endpoint.id)
            } label: {
                Image(systemName: "doc.on.doc")
            }
            .buttonStyle(.plain)
            .help("Duplicate endpoint")
            Button {
                customEndpointManager.deleteEndpoint(id: endpoint.id)
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.plain)
            .help("Delete endpoint")
        }
        .padding(12)
    }

    private var legend: some View {
        HStack(spacing: 18) {
            legendItem(symbol: "lock.open.fill", color: .green, label: "Enabled")
            legendItem(symbol: "lock.fill", color: .secondary, label: "Locked")
            legendItem(symbol: "exclamationmark.triangle.fill", color: .orange, label: "Key invalid")
            Spacer()
        }
        .padding(.top, 4)
    }

    private func legendItem(symbol: String, color: Color, label: String) -> some View {
        HStack(spacing: 6) {
            Image(systemName: symbol).foregroundColor(color).font(.system(size: 11))
            Text(label).font(.caption).foregroundColor(.secondary)
        }
    }

    // MARK: - Filtering

    /// Every filter except the language one. Used both for the visible list and
    /// to compute the "N of M support X" count (M = models matching everything
    /// else; N = those that also support the chosen language).
    private func passesNonLanguageFilters(_ model: LibraryModel) -> Bool {
        let query = searchText.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if !providerFilter.matches(model.providerKey) { return false }
        if typeFilter != .all && model.kind.rawValue != typeFilter.rawValue { return false }
        if locationFilter != .all {
            switch model.location {
            case .cloud where locationFilter != .cloud: return false
            case .offline where locationFilter != .offline: return false
            default: break
            }
        }
        if vocabFilter && !model.supportsCustomVocabulary { return false }
        if cloudAvailableFilter && !model.availableViaHyperWhisperCloud { return false }
        if !query.isEmpty {
            let matchesQuery =
                model.displayName.lowercased().contains(query)
                || model.providerKey.displayName.lowercased().contains(query)
                || (model.tag?.lowercased().contains(query) ?? false)
            if !matchesQuery { return false }
        }
        return true
    }

    /// Language clause. Only voice models are language-filtered; text
    /// (post-processing) models always pass.
    private func passesLanguageFilter(_ model: LibraryModel) -> Bool {
        if languageFilter.isEmpty { return true }
        if model.kind != .voice { return true }
        return model.supportsLanguage(languageFilter)
    }

    /// `(supported, total, displayName)` when a language is selected, else nil.
    var languageFilterSummary: (supported: Int, total: Int, name: String)? {
        guard !languageFilter.isEmpty else { return nil }
        let voiceModels = library.models.filter { $0.kind == .voice && passesNonLanguageFilters($0) }
        let supported = voiceModels.filter { $0.supportsLanguage(languageFilter) }.count
        let name = LibraryLanguageFilter.languages.first { $0.code == languageFilter }?.displayName ?? languageFilter
        return (supported, voiceModels.count, name)
    }

    private var visibleModels: [LibraryModel] {
        let filtered = library.models.filter { passesNonLanguageFilters($0) && passesLanguageFilter($0) }

        guard let column = sortColumn else { return filtered }
        return filtered.sorted { a, b in
            let ascending = sortAscending
            switch column {
            case .name:
                let cmp = a.displayName.localizedCaseInsensitiveCompare(b.displayName)
                return ascending ? (cmp == .orderedAscending) : (cmp == .orderedDescending)
            case .type:
                if a.kind != b.kind {
                    let av = a.kind.rawValue, bv = b.kind.rawValue
                    return ascending ? (av < bv) : (av > bv)
                }
                return a.displayName.localizedCaseInsensitiveCompare(b.displayName) == .orderedAscending
            case .rating:
                let sa = a.speed + a.accuracy
                let sb = b.speed + b.accuracy
                if sa != sb { return ascending ? (sa < sb) : (sa > sb) }
                if a.accuracy != b.accuracy {
                    return ascending ? (a.accuracy < b.accuracy) : (a.accuracy > b.accuracy)
                }
                return ascending ? (a.speed < b.speed) : (a.speed > b.speed)
            case .location:
                let av = locationOrder(a.location)
                let bv = locationOrder(b.location)
                if av != bv { return ascending ? (av < bv) : (av > bv) }
                return a.displayName.localizedCaseInsensitiveCompare(b.displayName) == .orderedAscending
            }
        }
    }

    private func locationOrder(_ l: LibraryModelLocation) -> Int {
        switch l {
        case .cloud: return 0
        case .offline(_, let installed, _): return installed ? 1 : 2
        }
    }

    // MARK: - Row actions

    private func handleLockTap(_ model: LibraryModel) {
        guard let target = providerKeyTarget(for: model) else { return }
        switch model.status {
        case .locked:
            sheetMode = .connect
            sheetTarget = target
        case .error:
            sheetMode = .recover
            sheetTarget = target
        default:
            break
        }
    }

    private func providerKeyTarget(for model: LibraryModel) -> ProviderKeyTarget? {
        switch model.providerKey {
        case .cloud(let provider) where provider.requiresAPIKey:
            return .cloud(provider)
        case .postProcessing(let provider) where provider.requiresAPIKey:
            return .post(provider)
        default:
            return nil
        }
    }

    private func handleCloudTap(_ model: LibraryModel) {
        switch model.location {
        case .cloud:
            break
        case .offline(_, let installed, _):
            if installed {
                triggerDelete(for: model)
            } else {
                // Local model downloads are unlimited (open source) — no gate.
                triggerDownload(for: model)
            }
        }
    }

    private func triggerDownload(for model: LibraryModel) {
        let canonical = model.canonicalModelId
        switch model.providerKey {
        case .localWhisper:
            if let item = whisperManager.availableModels.first(where: { $0.name == canonical }) {
                Task { await whisperManager.downloadModel(item) }
            }
        case .parakeet:
            parakeetManager.startDownload(canonical)
        case .qwen3ASR:
            qwen3AsrManager.startDownload()
        case .nemotron:
            if #available(macOS 14.0, *) {
                nemotronManager.startDownload(canonical)
            }
        case .postProcessing(.localLLM):
            localLLMManager.downloadModel(canonical)
        // Exhaustive on purpose: cloud, Apple Speech and non-localLLM
        // post-processing models aren't downloadable. Listing them (instead of
        // `default:`) makes a future provider a compile error here.
        case .postProcessing, .cloud, .appleSpeech:
            break
        }
    }

    private func cancelHandler(for model: LibraryModel) -> (() -> Void)? {
        // All local downloads are cancellable: URLSession-backed ones (Whisper,
        // local LLM) and FluidAudio ones (Parakeet, Qwen3, Nemotron) — the latter
        // retain their download `Task` and cancel cooperatively.
        switch model.providerKey {
        case .localWhisper, .postProcessing(.localLLM), .parakeet, .qwen3ASR:
            return { cancelDownload(for: model) }
        case .nemotron:
            if #available(macOS 14.0, *) {
                return { cancelDownload(for: model) }
            }
            return nil
        case .postProcessing, .cloud, .appleSpeech:
            return nil
        }
    }

    private func cancelDownload(for model: LibraryModel) {
        let canonical = model.canonicalModelId
        switch model.providerKey {
        case .localWhisper:
            whisperManager.cancelDownload(canonical)
        case .postProcessing(.localLLM):
            localLLMManager.cancelDownload(canonical)
        case .parakeet:
            parakeetManager.cancelDownload(canonical)
        case .qwen3ASR:
            qwen3AsrManager.cancelDownload()
        case .nemotron:
            if #available(macOS 14.0, *) {
                nemotronManager.cancelDownload(canonical)
            }
        case .postProcessing, .cloud, .appleSpeech:
            break
        }
    }

    private func triggerDelete(for model: LibraryModel) {
        let canonical = model.canonicalModelId
        switch model.providerKey {
        case .localWhisper:
            checkAndRemoveModel(canonical)
        case .parakeet:
            checkAndRemoveParakeetModel(canonical)
        case .qwen3ASR:
            checkAndRemoveQwen3AsrModel()
        case .nemotron:
            checkAndRemoveNemotronModel(canonical)
        case .postProcessing(.localLLM):
            checkAndRemoveLocalModel(canonical)
        default:
            break
        }
    }

    @MainActor
    private func checkAndRemoveNemotronModel(_ modelId: String) {
        guard #available(macOS 14.0, *) else { return }
        let modesUsingModel = PersistenceController.shared.fetchAllModes().filter { mode in
            (mode.model ?? "").caseInsensitiveCompare(modelId) == .orderedSame
        }

        guard !showCannotDeleteAlertIfNeeded(modesUsingModel, messageKey: "settings.models.localASR.inUse") else { return }
        nemotronManager.deleteModel(modelId)
    }

    private func checkAndRemoveLocalModel(_ modelId: String) {
        let modesUsingModel = PersistenceController.shared.fetchAllModes().filter { mode in
            let processingMode = PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off
            let provider = mode.postProcessingProvider ?? ""
            let isActiveLocalMode = processingMode == .local
            let matchesProvider = provider == PostProcessingProvider.localLLM.rawValue
            let matchesModel = (mode.languageModel ?? "").caseInsensitiveCompare(modelId) == .orderedSame
            return isActiveLocalMode && matchesProvider && matchesModel
        }

        guard !showCannotDeleteAlertIfNeeded(modesUsingModel, messageKey: "settings.models.local.inUse") else { return }
        localLLMManager.deleteModel(modelId)
    }

    private func checkAndRemoveModel(_ modelId: String) {
        let modesUsingModel = PersistenceController.shared.fetchAllModes().filter { mode in
            (mode.model ?? "").caseInsensitiveCompare(modelId) == .orderedSame
        }

        guard !showCannotDeleteAlertIfNeeded(modesUsingModel, messageKey: "settings.models.localASR.inUse") else { return }

        Task {
            if let model = whisperManager.downloadedModels.first(where: { $0.name == modelId }) {
                await whisperManager.deleteModel(model)
            }
        }
    }

    private func checkAndRemoveQwen3AsrModel() {
        let modesUsingModel = PersistenceController.shared.fetchAllModes().filter { mode in
            (mode.model ?? "").caseInsensitiveCompare(Qwen3AsrModelManager.Constants.modelId) == .orderedSame
        }

        guard !showCannotDeleteAlertIfNeeded(modesUsingModel, messageKey: "settings.models.localASR.inUse") else { return }
        qwen3AsrManager.deleteModel()
    }

    private func checkAndRemoveParakeetModel(_ modelId: String) {
        let modesUsingModel = PersistenceController.shared.fetchAllModes().filter { mode in
            (mode.model ?? "").caseInsensitiveCompare(modelId) == .orderedSame
        }

        guard !showCannotDeleteAlertIfNeeded(modesUsingModel, messageKey: "settings.models.localASR.inUse") else { return }
        parakeetManager.deleteModel(modelId)
    }

    @discardableResult
    private func showCannotDeleteAlertIfNeeded(_ modes: [Mode], messageKey: String) -> Bool {
        guard !modes.isEmpty else { return false }

        let bulletItems = modes.map { mode in
            String(format: "settings.models.mode.bullet".localized, mode.name ?? "settings.models.mode.unknown".localized)
        }
        modelInUseMessage = String(format: messageKey.localized, bulletItems.joined(separator: "\n"))
        showingModelInUseAlert = true
        return true
    }
}
