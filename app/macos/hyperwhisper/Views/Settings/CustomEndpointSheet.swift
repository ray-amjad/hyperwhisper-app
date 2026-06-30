//
//  CustomEndpointSheet.swift
//  hyperwhisper
//
//  CUSTOM ENDPOINT ADD/EDIT SHEET
//  Modal sheet for configuring a custom OpenAI-compatible post-processing endpoint.
//
//  Features:
//  - Three tabs: LMStudio, Ollama, Custom
//  - LMStudio/Ollama tabs auto-fetch available models
//  - Add new or edit existing custom endpoints
//  - Name, URL, Model Name, and API Key fields
//  - Test button to verify endpoint connectivity
//  - Real-time validation feedback
//  - Secure API key entry (SecureField)
//

import SwiftUI

// MARK: - Endpoint Tab Type

/// Tab selection for endpoint configuration
enum EndpointTab: String, CaseIterable {
    case lmstudio = "LMStudio"
    case ollama = "Ollama"
    case custom = "Custom"
}

// MARK: - Custom Endpoint Sheet

/// Modal sheet for adding or editing a custom post-processing endpoint
struct CustomEndpointSheet: View {
    @Environment(\.dismiss) private var dismiss
    @EnvironmentObject var customEndpointManager: CustomPostProcessingManager

    /// Existing endpoint to edit (nil for new endpoint)
    let existingEndpoint: CustomPostProcessingEndpoint?

    /// Callback when endpoint is saved
    let onSave: ((CustomPostProcessingEndpoint) -> Void)?

    // MARK: - Tab State

    @State private var selectedTab: EndpointTab = .custom

    // MARK: - Form State

    @State private var name: String = ""
    @State private var endpointURL: String = ""
    @State private var modelName: String = ""
    @State private var apiKey: String = ""

    // MARK: - Model Fetching State

    @State private var availableModels: [String] = []
    @State private var isFetchingModels = false
    @State private var modelFetchError: String?
    @State private var selectedModel: String = ""

    private let modelFetcher = LocalModelFetcher()

    // MARK: - UI State

    @State private var isTesting = false
    @State private var testResult: TestResultState = .none
    @State private var validationError: String?
    @State private var isSaving = false

    /// Test result states for UI display
    enum TestResultState: Equatable {
        case none
        case success(preview: String)
        case failure(error: String)
    }

    // MARK: - Computed Properties

    /// Whether we're editing an existing endpoint vs creating new
    private var isEditing: Bool {
        existingEndpoint != nil
    }

    /// Title for the sheet
    private var sheetTitle: String {
        isEditing ? "Edit Endpoint" : "Add Endpoint"
    }

    /// Whether the form has valid input
    private var isFormValid: Bool {
        let trimmedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedURL = endpointURL.trimmingCharacters(in: .whitespacesAndNewlines)

        // Model validation depends on tab
        let hasValidModel: Bool
        switch selectedTab {
        case .lmstudio, .ollama:
            hasValidModel = !selectedModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        case .custom:
            hasValidModel = !modelName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }

        return !trimmedName.isEmpty &&
               !trimmedURL.isEmpty &&
               hasValidModel &&
               URL(string: trimmedURL) != nil
    }

    /// Whether the save button should be enabled
    private var canSave: Bool {
        isFormValid && !isSaving && !isTesting
    }

    /// Default URL for current tab
    private var defaultURL: String {
        switch selectedTab {
        case .lmstudio:
            return "http://localhost:1234/v1"
        case .ollama:
            return "http://localhost:11434"
        case .custom:
            return ""
        }
    }

    /// Default name for current tab
    private var defaultName: String {
        switch selectedTab {
        case .lmstudio:
            return "LMStudio"
        case .ollama:
            return "Ollama"
        case .custom:
            return ""
        }
    }

    // MARK: - Initialization

    init(
        existingEndpoint: CustomPostProcessingEndpoint? = nil,
        onSave: ((CustomPostProcessingEndpoint) -> Void)? = nil
    ) {
        self.existingEndpoint = existingEndpoint
        self.onSave = onSave
    }

    // MARK: - Body

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            // Title
            Text(sheetTitle)
                .font(.title2)
                .fontWeight(.semibold)

            // Tab Picker
            Picker("", selection: $selectedTab) {
                ForEach(EndpointTab.allCases, id: \.self) { tab in
                    Text(tab.rawValue).tag(tab)
                }
            }
            .pickerStyle(.segmented)
            .onChange(of: selectedTab) { _, newTab in
                handleTabChange(newTab)
            }

            // Tab Content
            tabContent

            Spacer()

            // Buttons
            buttonRow
        }
        .padding(24)
        .frame(minWidth: 480, minHeight: 480)
        .onAppear {
            loadExistingData()
        }
    }

    // MARK: - Tab Content

    @ViewBuilder
    private var tabContent: some View {
        switch selectedTab {
        case .lmstudio:
            lmstudioTabContent
        case .ollama:
            ollamaTabContent
        case .custom:
            customTabContent
        }
    }

    // MARK: - LMStudio Tab

    private var lmstudioTabContent: some View {
        VStack(alignment: .leading, spacing: 16) {
            // Name field
            nameField

            // URL field
            urlField(placeholder: "http://localhost:1234/v1")

            // Model Picker with Refresh
            modelPickerWithRefresh

            // API Key field
            apiKeyField

            // Validation error
            validationErrorView

            // Test section
            testSection
        }
    }

    // MARK: - Ollama Tab

    private var ollamaTabContent: some View {
        VStack(alignment: .leading, spacing: 16) {
            // Name field
            nameField

            // URL field
            urlField(placeholder: "http://localhost:11434")

            // Model Picker with Refresh
            modelPickerWithRefresh

            // API Key field
            apiKeyField

            // Validation error
            validationErrorView

            // Test section
            testSection
        }
    }

    // MARK: - Custom Tab

    private var customTabContent: some View {
        VStack(alignment: .leading, spacing: 16) {
            // Name field
            nameField

            // Endpoint URL field
            endpointURLField

            // Model name text field
            modelNameTextField

            // API Key field
            apiKeyField

            // Validation error
            validationErrorView

            // Test section
            testSection
        }
    }

    // MARK: - Shared Form Fields

    private var nameField: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Name")
                .font(.headline)
            TextField("e.g., My Local Model", text: $name)
                .textFieldStyle(.roundedBorder)
                .onChange(of: name) { _, _ in
                    validationError = nil
                }
        }
    }

    private func urlField(placeholder: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Base URL")
                .font(.headline)
            TextField(placeholder, text: $endpointURL)
                .textFieldStyle(.roundedBorder)
                .onChange(of: endpointURL) { _, _ in
                    validationError = nil
                    testResult = .none
                }
            Text("Base URL of your local server")
                .font(.caption)
                .foregroundColor(.secondary)
        }
    }

    private var endpointURLField: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Endpoint URL")
                .font(.headline)
            TextField("e.g., http://localhost:11434/v1/chat/completions", text: $endpointURL)
                .textFieldStyle(.roundedBorder)
                .onChange(of: endpointURL) { _, _ in
                    validationError = nil
                    testResult = .none
                }
            Text("Full URL to the OpenAI-compatible chat completions endpoint")
                .font(.caption)
                .foregroundColor(.secondary)
        }
    }

    private var modelNameTextField: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Model Name")
                .font(.headline)
            TextField("e.g., llama3.2, gpt-4, mistral-7b", text: $modelName)
                .textFieldStyle(.roundedBorder)
                .onChange(of: modelName) { _, _ in
                    validationError = nil
                }
            Text("The model identifier your endpoint expects")
                .font(.caption)
                .foregroundColor(.secondary)
        }
    }

    private var apiKeyField: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("API Key (Optional)")
                .font(.headline)
            SecureField("Enter API key if required", text: $apiKey)
                .textFieldStyle(.roundedBorder)
            Text("Stored securely in your Mac's Keychain")
                .font(.caption)
                .foregroundColor(.secondary)
        }
    }

    @ViewBuilder
    private var validationErrorView: some View {
        if let error = validationError {
            HStack(spacing: 6) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundColor(.orange)
                Text(error)
                    .foregroundColor(.orange)
            }
            .font(.caption)
        }
    }

    // MARK: - Model Picker

    private var modelPickerWithRefresh: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("Model")
                    .font(.headline)

                Spacer()

                // Refresh button
                Button(action: {
                    Task {
                        await fetchModels()
                    }
                }) {
                    Image(systemName: "arrow.clockwise")
                        .font(.caption)
                }
                .buttonStyle(.borderless)
                .disabled(isFetchingModels)
            }

            if isFetchingModels {
                HStack {
                    ProgressView()
                        .controlSize(.small)
                    Text("Fetching models...")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            } else if let error = modelFetchError {
                // Error state - show error and allow manual entry
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .foregroundColor(.orange)
                        Text(error)
                            .foregroundColor(.orange)
                    }
                    .font(.caption)

                    // Manual model entry
                    TextField("Enter model name manually", text: $selectedModel)
                        .textFieldStyle(.roundedBorder)
                }
            } else if availableModels.isEmpty {
                TextField("No models found - enter manually", text: $selectedModel)
                    .textFieldStyle(.roundedBorder)
            } else {
                // Model picker dropdown
                Picker("", selection: $selectedModel) {
                    Text("Select a model...").tag("")
                    ForEach(availableModels, id: \.self) { model in
                        Text(model).tag(model)
                    }
                }
                .labelsHidden()
            }

            Text("Model available on this endpoint")
                .font(.caption)
                .foregroundColor(.secondary)
        }
    }

    // MARK: - Test Section

    private var testSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Divider()

            HStack(spacing: 12) {
                // TEST BUTTON:
                // Sends a simple "Say hello" request to verify the endpoint works
                Button {
                    Task {
                        await runTest()
                    }
                } label: {
                    HStack(spacing: 6) {
                        if isTesting {
                            ProgressView()
                                .controlSize(.small)
                        } else {
                            Image(systemName: "play.circle")
                        }
                        Text(isTesting ? "Testing..." : "Test Connection")
                    }
                }
                .buttonStyle(.bordered)
                .disabled(!isFormValid || isTesting)

                // Test result indicator
                testResultView
            }

            // Test result details
            testResultDetails
        }
    }

    @ViewBuilder
    private var testResultView: some View {
        switch testResult {
        case .none:
            EmptyView()
        case .success:
            HStack(spacing: 4) {
                Image(systemName: "checkmark.circle.fill")
                    .foregroundColor(.green)
                Text("Connected")
                    .foregroundColor(.green)
            }
            .font(.caption)
        case .failure:
            HStack(spacing: 4) {
                Image(systemName: "xmark.circle.fill")
                    .foregroundColor(.red)
                Text("Failed")
                    .foregroundColor(.red)
            }
            .font(.caption)
        }
    }

    @ViewBuilder
    private var testResultDetails: some View {
        switch testResult {
        case .none:
            EmptyView()
        case .success(let preview):
            VStack(alignment: .leading, spacing: 4) {
                Text("Response preview:")
                    .font(.caption)
                    .foregroundColor(.secondary)
                Text(preview)
                    .font(.caption)
                    .foregroundColor(.primary)
                    .lineLimit(2)
                    .padding(8)
                    .background(
                        RoundedRectangle(cornerRadius: 6)
                            .fill(Color.green.opacity(0.1))
                    )
            }
        case .failure(let error):
            Text(error)
                .font(.caption)
                .foregroundColor(.red)
                .lineLimit(3)
                .padding(8)
                .background(
                    RoundedRectangle(cornerRadius: 6)
                        .fill(Color.red.opacity(0.1))
                )
        }
    }

    // MARK: - Button Row

    private var buttonRow: some View {
        HStack {
            Button("Cancel", role: .cancel) {
                dismiss()
            }

            Spacer()

            Button(isEditing ? "Save Changes" : "Add Endpoint") {
                saveEndpoint()
            }
            .buttonStyle(.borderedProminent)
            .disabled(!canSave)
        }
    }

    // MARK: - Tab Change Handler

    private func handleTabChange(_ tab: EndpointTab) {
        // Clear previous state
        availableModels = []
        modelFetchError = nil
        testResult = .none

        // Auto-fill name and URL based on tab (only if they're empty or default values)
        let currentNameIsDefault = name.isEmpty || name == "Ollama" || name == "LMStudio"
        let currentURLIsDefault = endpointURL.isEmpty ||
            endpointURL == "http://localhost:11434" ||
            endpointURL == "http://localhost:1234/v1"

        if currentNameIsDefault {
            name = defaultName
        }

        if currentURLIsDefault {
            endpointURL = defaultURL
        }

        // Fetch models for Ollama/LMStudio tabs
        if tab == .ollama || tab == .lmstudio {
            Task {
                await fetchModels()
            }
        }
    }

    // MARK: - Model Fetching

    private func fetchModels() async {
        isFetchingModels = true
        modelFetchError = nil
        availableModels = []

        do {
            switch selectedTab {
            case .ollama:
                availableModels = try await modelFetcher.fetchOllamaModels(baseURL: endpointURL)
            case .lmstudio:
                availableModels = try await modelFetcher.fetchLMStudioModels(baseURL: endpointURL)
            case .custom:
                break
            }

            // Auto-select first model if available and none selected
            if !availableModels.isEmpty && selectedModel.isEmpty {
                selectedModel = availableModels[0]
            }

        } catch {
            modelFetchError = "Could not fetch models. Ensure \(selectedTab.rawValue) is running."
        }

        isFetchingModels = false
    }

    // MARK: - Actions

    /// Load existing endpoint data for editing
    private func loadExistingData() {
        guard let endpoint = existingEndpoint else { return }

        name = endpoint.name
        endpointURL = endpoint.endpointURL
        modelName = endpoint.modelName
        selectedModel = endpoint.modelName
        apiKey = customEndpointManager.getAPIKey(for: endpoint.id)

        // Detect which tab this endpoint belongs to based on URL patterns
        if endpoint.endpointURL.contains("localhost:1234") {
            selectedTab = .lmstudio
            // Strip the /chat/completions suffix to get base URL
            if endpoint.endpointURL.hasSuffix("/chat/completions") {
                endpointURL = String(endpoint.endpointURL.dropLast("/chat/completions".count))
            }
        } else if endpoint.endpointURL.contains("localhost:11434") {
            selectedTab = .ollama
            // Strip the /v1/chat/completions suffix to get base URL
            if endpoint.endpointURL.hasSuffix("/v1/chat/completions") {
                endpointURL = String(endpoint.endpointURL.dropLast("/v1/chat/completions".count))
            }
        } else {
            selectedTab = .custom
        }

        // Set initial test result based on stored status
        if let success = endpoint.lastTestSuccess {
            testResult = success ? .success(preview: "Previously verified") : .failure(error: "Previously failed")
        }

        // Fetch models if on Ollama/LMStudio tab
        if selectedTab == .ollama || selectedTab == .lmstudio {
            Task {
                await fetchModels()
            }
        }
    }

    /// Run connection test
    private func runTest() async {
        isTesting = true
        testResult = .none

        // Build the full endpoint URL for testing
        let testURL = buildFullEndpointURL()
        let testModelName = selectedTab == .custom ? modelName : selectedModel

        // CREATE TEMPORARY ENDPOINT FOR TESTING:
        let testEndpoint: CustomPostProcessingEndpoint

        if let existing = existingEndpoint {
            // Update existing with current form values for testing
            var updated = existing
            updated.name = name.trimmingCharacters(in: .whitespacesAndNewlines)
            updated.endpointURL = testURL.trimmingCharacters(in: .whitespacesAndNewlines)
            updated.modelName = testModelName.trimmingCharacters(in: .whitespacesAndNewlines)
            testEndpoint = updated
        } else {
            // Create temporary endpoint for new endpoint test
            testEndpoint = CustomPostProcessingEndpoint(
                name: name.trimmingCharacters(in: .whitespacesAndNewlines),
                endpointURL: testURL.trimmingCharacters(in: .whitespacesAndNewlines),
                modelName: testModelName.trimmingCharacters(in: .whitespacesAndNewlines)
            )
        }

        // BUILD TEST REQUEST DIRECTLY
        // The test uses the in-memory `apiKey` for the Authorization header
        // (see testEndpointDirectly), so the Keychain is never read here. We
        // must NOT write the typed key to the Keychain before a save/commit —
        // doing so for an existing endpoint would overwrite the live stored key
        // in place even if the user then cancels. Keychain is only written from
        // saveEndpoint().
        let result = await testEndpointDirectly(testEndpoint)

        isTesting = false

        switch result {
        case .success(let preview):
            testResult = .success(preview: preview)
        case .failure(let error):
            testResult = .failure(error: error)
        }
    }

    /// Test endpoint directly without going through manager
    private func testEndpointDirectly(_ endpoint: CustomPostProcessingEndpoint) async -> CustomEndpointTestResult {
        guard let url = URL(string: endpoint.endpointURL) else {
            return .failure(error: "Invalid URL")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")

        // Add auth if API key provided
        if !apiKey.isEmpty {
            request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }

        // Minimal test request
        let requestBody: [String: Any] = [
            "model": endpoint.modelName,
            "messages": [
                ["role": "user", "content": "Say hello in one word."]
            ],
            "max_tokens": 10,
            "temperature": 0.0
        ]

        do {
            request.httpBody = try JSONSerialization.data(withJSONObject: requestBody)
        } catch {
            return .failure(error: "Failed to encode request")
        }

        do {
            let (data, response) = try await URLSession.shared.data(for: request)

            guard let httpResponse = response as? HTTPURLResponse else {
                return .failure(error: "Invalid response")
            }

            if httpResponse.statusCode != 200 {
                if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                   let error = json["error"] as? [String: Any],
                   let message = error["message"] as? String {
                    return .failure(error: "HTTP \(httpResponse.statusCode): \(message)")
                }
                return .failure(error: "HTTP \(httpResponse.statusCode)")
            }

            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let choices = json["choices"] as? [[String: Any]],
                  let firstChoice = choices.first,
                  let message = firstChoice["message"] as? [String: Any],
                  let content = message["content"] as? String else {
                return .failure(error: "Invalid response format")
            }

            return .success(responsePreview: content.trimmingCharacters(in: .whitespacesAndNewlines))

        } catch let error as URLError {
            return .failure(error: "Network error: \(error.localizedDescription)")
        } catch {
            return .failure(error: error.localizedDescription)
        }
    }

    /// Build the full endpoint URL based on current tab
    private func buildFullEndpointURL() -> String {
        switch selectedTab {
        case .ollama:
            // Ollama endpoint format: baseURL/v1/chat/completions
            let normalizedBase = endpointURL.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            return "\(normalizedBase)/v1/chat/completions"

        case .lmstudio:
            // LMStudio endpoint format: baseURL/chat/completions
            let normalizedBase = endpointURL.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            return "\(normalizedBase)/chat/completions"

        case .custom:
            // Custom: user provides full URL
            return endpointURL
        }
    }

    /// Save the endpoint
    private func saveEndpoint() {
        isSaving = true

        // For Ollama/LMStudio tabs, use selected model and construct full endpoint URL
        let finalModelName: String
        let finalEndpointURL: String

        switch selectedTab {
        case .ollama, .lmstudio:
            finalModelName = selectedModel.trimmingCharacters(in: .whitespacesAndNewlines)
            finalEndpointURL = buildFullEndpointURL()

        case .custom:
            finalModelName = modelName.trimmingCharacters(in: .whitespacesAndNewlines)
            finalEndpointURL = endpointURL.trimmingCharacters(in: .whitespacesAndNewlines)
        }

        do {
            let savedEndpoint: CustomPostProcessingEndpoint

            if let existing = existingEndpoint {
                // UPDATE EXISTING ENDPOINT
                try customEndpointManager.updateEndpoint(
                    id: existing.id,
                    name: name.trimmingCharacters(in: .whitespacesAndNewlines),
                    endpointURL: finalEndpointURL,
                    modelName: finalModelName,
                    apiKey: apiKey.isEmpty ? nil : apiKey
                )
                savedEndpoint = customEndpointManager.getEndpoint(id: existing.id) ?? existing
            } else {
                // CREATE NEW ENDPOINT
                savedEndpoint = try customEndpointManager.addEndpoint(
                    name: name.trimmingCharacters(in: .whitespacesAndNewlines),
                    endpointURL: finalEndpointURL,
                    modelName: finalModelName,
                    apiKey: apiKey.isEmpty ? nil : apiKey
                )
            }

            onSave?(savedEndpoint)
            dismiss()

        } catch let error as CustomPostProcessingEndpoint.ValidationError {
            validationError = error.localizedDescription
            isSaving = false
        } catch {
            validationError = error.localizedDescription
            isSaving = false
        }
    }
}

// MARK: - Preview

#Preview("Add New") {
    CustomEndpointSheet()
        .environmentObject(CustomPostProcessingManager())
}

#Preview("Edit Existing") {
    let endpoint = CustomPostProcessingEndpoint(
        name: "My Ollama",
        endpointURL: "http://localhost:11434/v1/chat/completions",
        modelName: "llama3.2"
    )
    return CustomEndpointSheet(existingEndpoint: endpoint)
        .environmentObject(CustomPostProcessingManager())
}
