//
//  ModeEditorConfiguration.swift
//  HyperWhisper
//
//  Whether ModeEditorView creates a mode or edits one. Moved out of
//  ModeEditorView.swift unchanged (issue #730).
//

import Foundation

// MARK: - Configuration

/// Configuration for ModeEditorView behavior.
/// Determines whether the view is in create or edit mode.
enum ModeEditorConfiguration {
    case create
    case edit(mode: Mode, onDelete: (() -> Void)?)

    /// Returns true if in edit mode
    var isEditMode: Bool {
        if case .edit = self { return true }
        return false
    }

    /// Returns the Mode being edited, or nil if creating
    var mode: Mode? {
        if case .edit(let mode, _) = self { return mode }
        return nil
    }

    /// Returns the delete callback, or nil if creating
    var onDelete: (() -> Void)? {
        if case .edit(_, let callback) = self { return callback }
        return nil
    }
}
