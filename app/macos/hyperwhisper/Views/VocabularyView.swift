//
//  VocabularyView.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  VOCABULARY VIEW
//  Manages custom vocabulary for improved transcription accuracy.
//  Users can add industry terms, names, acronyms, and specialized words.
//
//  Features:
//  - Add custom words with optional replacements
//  - Import/export vocabulary lists
//  - Search and filter capabilities
//  - Bulk operations

import SwiftUI
import CoreData

// MARK: - Vocabulary View

/// Main view for managing custom vocabulary
struct VocabularyView: View {
    /// DEEPGRAM KEYWORDS LIMIT:
    /// Deepgram's keywords API supports a maximum of 100 terms per request.
    /// Show warning when user exceeds this limit.
    private static let maxKeywords = 100

    /// Core Data context for database operations
    @Environment(\.managedObjectContext) private var viewContext

    /// Fetch request for vocabulary items from Core Data
    @FetchRequest(
        entity: Vocabulary.entity(),
        sortDescriptors: [NSSortDescriptor(keyPath: \Vocabulary.word, ascending: true)]
    ) private var vocabularyItems: FetchedResults<Vocabulary>
    
    // Search removed: displaying full list without filtering
    
    // Removed bulk selection - no longer needed
    
    /// Whether to show the add word sheet
    @State private var showingAddWord = false

    /// Input for new word
    @State private var newWord = ""
    @State private var replacement = ""

    /// Whether the expanded replacement field is currently shown.
    /// Opens via ⌘ Return on the word field, or auto-opens when editing an item that already has a replacement.
    @State private var showingReplacementField = false

    /// Error message for duplicate entries
    @State private var showDuplicateAlert = false
    @State private var duplicateWord = ""

    /// Edit mode: tracks item being edited via safety-net pattern
    @State private var pendingDeleteId: UUID?
    @State private var editingId: UUID?

    /// One-shot guard for the CloudKit merge-duplicate dedup pass.
    /// Runs once per app launch on the first `.onAppear`.
    @State private var didRunDedupPass = false

    /// FOCUS MANAGEMENT:
    /// Tracks which field currently has keyboard focus
    /// Ensures cursor returns to newWord field after adding a word
    @FocusState private var focusedField: Field?

    /// True when the word field holds a non-empty value after trimming whitespace.
    /// Used to gate Add/Replace so a whitespace-only word can never be persisted
    /// (an empty word compiles to the "\b\b" regex that corrupts transcripts).
    private var hasValidWord: Bool {
        !newWord.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// Enum representing focusable fields in the input section
    enum Field {
        case newWord
        case replacement
    }
    
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Header
            headerSection

            Divider()

            // KEYWORDS LIMIT WARNING:
            // Show warning when user has more than 100 vocabulary items
            // Only 100 keywords can be sent to Deepgram for boosting
            if vocabularyItems.count > Self.maxKeywords {
                keywordsLimitWarning
            }

            // Input section
            inputSection

            Divider()

            // Vocabulary list
            vocabularyList
        }
        .background(VisualEffectBackground())
        .navigationTitle("vocabulary.title".localized)
        .onAppear {
            // INITIAL FOCUS:
            // Set focus to newWord field when view first loads
            // Allows user to start typing immediately without clicking
            focusedField = .newWord

            // CLOUDKIT MERGE-DUPLICATE DEDUP:
            // CloudKit does not support unique constraints, so the same word added
            // on two devices while offline arrives as two rows after sync.
            // Last-writer-wins merge policy can't fix this — it only applies per-object.
            // Run a one-shot dedup pass per launch to collapse duplicates.
            if !didRunDedupPass {
                didRunDedupPass = true
                dedupVocabularyDuplicates()
            }
        }
        .alert("vocabulary.duplicate.title".localized, isPresented: $showDuplicateAlert) {
            Button("vocabulary.ok.button".localized) {
                // FOCUS MANAGEMENT:
                // Return focus to the newWord field after dismissing duplicate alert
                // This allows user to immediately correct their input
                focusedField = .newWord
            }
        } message: {
Text("vocabulary.duplicate.message".localized(arguments: duplicateWord))
        }
    }

    // MARK: - Header Section

    private var headerSection: some View {
        PageHeader(
            title: "vocabulary.title".localized,
            subtitle: "vocabulary.description".localized
        )
    }

    // MARK: - Keywords Limit Warning

    /// Warning banner shown when vocabulary exceeds Deepgram's 100 keyword limit
    private var keywordsLimitWarning: some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundColor(.orange)

            VStack(alignment: .leading, spacing: 2) {
                Text("vocabulary.limit.title".localized)
                    .font(.headline)
                    .foregroundColor(.primary)

                Text("vocabulary.limit.message".localized(arguments: vocabularyItems.count, Self.maxKeywords))
                    .font(.caption)
                    .foregroundColor(.secondary)
            }

            Spacer()
        }
        .padding(.horizontal)
        .padding(.vertical, 10)
        .background(Color.orange.opacity(0.1))
    }

    // MARK: - Input Section

    /// New design: a single rounded input field with inline keyboard-shortcut chips on the right.
    /// Return adds the word as-is. ⌘ Return expands a separate replacement field below
    /// (focused, blue-bordered) where another ⌘ Return commits the replacement pair.
    private var inputSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            wordInputRow

            if showingReplacementField {
                replacementFieldRow
                    .transition(.asymmetric(
                        insertion: .opacity.combined(with: .move(edge: .top)),
                        removal: .opacity
                    ))
            }
        }
        .padding(.horizontal)
        .padding(.vertical, 14)
        .background(Color(NSColor.controlBackgroundColor).opacity(0.5))
        .id("inputSection")
        .animation(.easeInOut(duration: 0.18), value: showingReplacementField)
        // Hidden buttons carrying keyboard shortcuts. SwiftUI fires the
        // matching button's action when its shortcut is pressed and the
        // button isn't disabled, so we gate each on focus + content state.
        .background(keyboardShortcutButtons)
    }

    /// The main pill-shaped input row with trailing keyboard-shortcut chips.
    private var wordInputRow: some View {
        HStack(spacing: 14) {
            TextField("vocabulary.input.placeholder".localized, text: $newWord)
                .textFieldStyle(.plain)
                .font(.system(size: 14))
                .focused($focusedField, equals: .newWord)
                .onSubmit {
                    addWord()
                }

            HStack(spacing: 16) {
                actionChip(
                    label: editingId != nil ? "vocabulary.update.button".localized : "vocabulary.action.addWord".localized,
                    glyphs: ["↵"],
                    enabled: hasValidWord
                ) {
                    addWord()
                }

                actionChip(
                    label: "vocabulary.action.replaceWith".localized,
                    glyphs: ["⌘", "↵"],
                    enabled: hasValidWord && !showingReplacementField
                ) {
                    openReplacementField()
                }

                if editingId != nil {
                    Button("vocabulary.cancel.button".localized) {
                        cancelEdit()
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                }
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(Color.primary.opacity(0.04))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .stroke(Color.primary.opacity(0.10), lineWidth: 1)
        )
    }

    /// The separate replacement field that appears below when ⌘ Return is pressed.
    /// Blue-bordered to make the active state obvious; another ⌘ Return commits.
    private var replacementFieldRow: some View {
        VStack(alignment: .leading, spacing: 6) {
            TextField("vocabulary.replacement.field.placeholder".localized, text: $replacement, axis: .vertical)
                .textFieldStyle(.plain)
                .font(.system(size: 14))
                .lineLimit(2...5)
                .focused($focusedField, equals: .replacement)

            HStack {
                Spacer()
                actionChip(
                    label: "vocabulary.action.replace".localized,
                    glyphs: ["⌘", "↵"],
                    enabled: hasValidWord && !replacement.isEmpty
                ) {
                    addWord()
                }
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(Color.primary.opacity(0.04))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .stroke(Color.accentColor, lineWidth: 2)
        )
        .shadow(color: Color.accentColor.opacity(0.18), radius: 0, x: 0, y: 0)
    }

    /// Inline tappable label + key glyphs. Dims to ~35% when not actionable
    /// so the user can see the shortcut but knows it won't fire yet.
    private func actionChip(label: String, glyphs: [String], enabled: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Text(label)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                HStack(spacing: 3) {
                    ForEach(glyphs, id: \.self) { glyph in
                        keyboardGlyph(glyph)
                    }
                }
            }
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1.0 : 0.35)
    }

    /// Single keyboard-key chip (e.g. "⌘" or "↵").
    private func keyboardGlyph(_ glyph: String) -> some View {
        Text(glyph)
            .font(.system(size: 11, weight: .medium))
            .frame(minWidth: 20, minHeight: 20)
            .padding(.horizontal, 4)
            .background(
                RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .fill(Color.primary.opacity(0.08))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .stroke(Color.primary.opacity(0.10), lineWidth: 0.5)
            )
    }

    /// Hidden buttons attached to the input section that own the keyboard shortcuts.
    /// We disable each based on focus + content so the same ⌘ Return shortcut
    /// routes to the right action depending on which field is focused.
    private var keyboardShortcutButtons: some View {
        ZStack {
            Button("") { openReplacementField() }
                .keyboardShortcut(.return, modifiers: .command)
                .disabled(focusedField != .newWord || !hasValidWord || showingReplacementField)
                .opacity(0)
                .frame(width: 0, height: 0)

            Button("") { addWord() }
                .keyboardShortcut(.return, modifiers: .command)
                .disabled(focusedField != .replacement || !hasValidWord || replacement.isEmpty)
                .opacity(0)
                .frame(width: 0, height: 0)

            Button("") { cancelReplacementField() }
                .keyboardShortcut(.cancelAction)
                .disabled(!showingReplacementField)
                .opacity(0)
                .frame(width: 0, height: 0)
        }
        .accessibilityHidden(true)
    }
    
    // MARK: - Vocabulary List
    
    private var vocabularyList: some View {
        VStack(spacing: 0) {
            // Column headers
            if !filteredVocabulary.isEmpty {
                HStack(spacing: 16) {
                    Text("vocabulary.word.column".localized)
                        .font(.caption)
                        .fontWeight(.semibold)
                        .foregroundColor(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    
                    Spacer()
                        .frame(width: 20)
                    
                    Text("vocabulary.replacement.column".localized)
                        .font(.caption)
                        .fontWeight(.semibold)
                        .foregroundColor(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    
                    Spacer()
                        .frame(width: 20)
                }
                .padding(.horizontal)
                .padding(.vertical, 8)
                .background(Color(NSColor.controlBackgroundColor).opacity(0.3))
                
                Divider()
            }
            
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(spacing: 0) {
                        if filteredVocabulary.isEmpty {
                            emptyState
                        } else {
                            ForEach(filteredVocabulary) { vocabItem in
                                VocabularyRow(
                                    vocabItem: vocabItem,
                                    onEdit: {
                                        editWord(vocabItem, scrollProxy: proxy)
                                    },
                                    onDelete: {
                                        PersistenceController.shared.deleteVocabularyItem(byId: vocabItem.id ?? UUID())
                                    }
                                )

                                Divider()
                            }
                        }
                    }
                }
            }
        }
    }
    
    // MARK: - Empty State
    
    private var emptyState: some View {
        VStack(spacing: 16) {
            Image(systemName: "text.book.closed")
                .font(.system(size: 48))
                .foregroundColor(.secondary)
            
            Text("vocabulary.empty.title".localized)
                .font(.headline)
                .foregroundColor(.secondary)
            
            Text("vocabulary.empty.subtitle".localized)
                .font(.subheadline)
                .foregroundColor(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(40)
    }
    
    // MARK: - Filtered Vocabulary
    
    private var filteredVocabulary: [Vocabulary] {
        Array(vocabularyItems).filter { $0.id != pendingDeleteId }
    }

    // MARK: - Actions
    
    private func addWord() {
        guard hasValidWord else { return }

        let wasAdded = PersistenceController.shared.addVocabularyItem(
            word: newWord,
            replacement: replacement.isEmpty ? nil : replacement,
            excludingId: editingId
        )

        if wasAdded {
            // Delete old item only after new item is successfully saved
            if let deleteId = pendingDeleteId {
                PersistenceController.shared.deleteVocabularyItem(byId: deleteId)
                pendingDeleteId = nil
                editingId = nil
            }

            newWord = ""
            replacement = ""
            showingReplacementField = false
            focusedField = .newWord
        } else {
            duplicateWord = newWord
            showDuplicateAlert = true
        }
    }

    /// Opens the separate replacement field below the word input and moves focus into it.
    /// Triggered by ⌘ Return on the word field or by clicking the "Replace with… ⌘ ↵" chip.
    private func openReplacementField() {
        guard hasValidWord else { return }
        showingReplacementField = true
        // Slight delay lets the field render before focus tries to land on it.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            focusedField = .replacement
        }
    }

    /// Collapses the replacement field and returns focus to the word input.
    /// Triggered by Esc, or implicitly after commit / cancel-edit.
    private func cancelReplacementField() {
        showingReplacementField = false
        replacement = ""
        focusedField = .newWord
    }

    private func editWord(_ item: Vocabulary, scrollProxy: ScrollViewProxy) {
        pendingDeleteId = item.id
        editingId = item.id
        newWord = item.word ?? ""
        replacement = item.replacement ?? ""
        // If the item being edited already has a replacement, open the
        // replacement field so both values are visible. Otherwise stay collapsed.
        showingReplacementField = !(item.replacement ?? "").isEmpty
        focusedField = .newWord

        withAnimation {
            scrollProxy.scrollTo("inputSection", anchor: .top)
        }
    }

    private func cancelEdit() {
        pendingDeleteId = nil
        editingId = nil
        newWord = ""
        replacement = ""
        showingReplacementField = false
        focusedField = .newWord
    }

    // MARK: - CloudKit Merge-Duplicate Dedup

    /// Collapses duplicate vocabulary rows that arise from CloudKit syncing the same
    /// word added on two devices while offline. Groups by normalized word
    /// (whitespace-trimmed + locale-stable case folding), keeps the "best" row in each
    /// group, deletes the rest.
    ///
    /// "Best" means: prefer a row that carries a replacement, then the most recently
    /// created row, then the smallest `id` UUID string as a stable tiebreaker. The edit
    /// flow creates a new row (with the user's replacement and a newer `createdDate`) and
    /// only deletes the old one after save, so a crash or CloudKit conflict can leave both
    /// the stale and the edited row present. Keeping the earliest row would silently
    /// discard the user's replacement, so we keep the replacement-bearing / newer row.
    ///
    /// Before folding, the Unicode dotted capital I is mapped to plain `I` so variants
    /// like `İnstagram` and `instagram` land in the same duplicate group without
    /// collapsing accent-distinct terms such as `resume` and `résumé`.
    ///
    /// Runs once per app launch. Cheap because vocabulary lists are small (tens to low
    /// hundreds of rows). If nothing is duplicated, `context.hasChanges` is false and
    /// this is a no-op.
    private func dedupVocabularyDuplicates() {
        let posixLocale = Locale(identifier: "en_US_POSIX")
        var groups: [String: [Vocabulary]] = [:]
        for item in vocabularyItems {
            let normalized = (item.word ?? "")
                .trimmingCharacters(in: .whitespacesAndNewlines)
                .replacingOccurrences(of: "\u{0130}", with: "I")
                .folding(options: [.caseInsensitive], locale: posixLocale)
            guard !normalized.isEmpty else { continue }
            groups[normalized, default: []].append(item)
        }

        var deletedCount = 0
        for (_, items) in groups where items.count > 1 {
            let sorted = items.sorted { lhs, rhs in
                // Primary: prefer the row that carries a replacement, so a partial edit
                // (add word now, add replacement later) never loses the replacement.
                let lhsHasReplacement = !(lhs.replacement ?? "").isEmpty
                let rhsHasReplacement = !(rhs.replacement ?? "").isEmpty
                if lhsHasReplacement != rhsHasReplacement {
                    return lhsHasReplacement
                }
                // Secondary: keep the most recently created row (latest user intent).
                let lhsDate = lhs.createdDate ?? Date.distantPast
                let rhsDate = rhs.createdDate ?? Date.distantPast
                if lhsDate != rhsDate {
                    return lhsDate > rhsDate
                }
                // Tertiary: stable tiebreaker on id.
                let lhsId = lhs.id?.uuidString ?? ""
                let rhsId = rhs.id?.uuidString ?? ""
                return lhsId < rhsId
            }
            // Keep sorted[0] (the best row), delete the rest
            for duplicate in sorted.dropFirst() {
                viewContext.delete(duplicate)
                deletedCount += 1
            }
        }

        if deletedCount > 0 {
            PersistenceController.shared.save()
        }
    }

}

// MARK: - Vocabulary Row

/// Individual row in the vocabulary list with columnar layout
struct VocabularyRow: View {
    let vocabItem: Vocabulary
    let onEdit: () -> Void
    let onDelete: () -> Void

    @State private var isHovered = false

    var body: some View {
        HStack(spacing: 16) {
            // COLUMN 1: Original word/phrase
            Text(vocabItem.word ?? "")
                .font(.body)
                .foregroundColor(.primary)
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)

            // COLUMN 2: Arrow indicator (if replacement exists)
            if vocabItem.replacement != nil && !vocabItem.replacement!.isEmpty {
                Image(systemName: "arrow.right")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .frame(width: 20)
            } else {
                Spacer()
                    .frame(width: 20)
            }

            // COLUMN 3: Replacement text
            Text(vocabItem.replacement ?? "")
                .font(.body)
                .foregroundColor(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .lineLimit(1)

            // Edit and delete buttons (shown on hover)
            if isHovered {
                Button {
                    onEdit()
                } label: {
                    Image(systemName: "pencil")
                        .foregroundColor(.accentColor)
                }
                .buttonStyle(.plain)

                Button {
                    onDelete()
                } label: {
                    Image(systemName: "trash")
                        .foregroundColor(.red)
                }
                .buttonStyle(.plain)
            } else {
                // Placeholder to maintain consistent width for both buttons
                Spacer()
                    .frame(width: 44)
            }
        }
        .padding(.horizontal)
        .padding(.vertical, 8)
        .background(
            Rectangle()
                .fill(isHovered ? Color.accentColor.opacity(0.05) : Color.clear)
        )
        .contentShape(Rectangle())
        .onHover { hovering in
            isHovered = hovering
        }
    }
}

// MARK: - Preview

#Preview {
    VocabularyView()
        .environment(\.managedObjectContext, PersistenceController.preview.container.viewContext)
        .frame(width: 800, height: 600)
}
