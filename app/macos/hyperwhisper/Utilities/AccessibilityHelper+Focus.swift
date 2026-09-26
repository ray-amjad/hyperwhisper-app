//
//  AccessibilityHelper+Focus.swift
//  hyperwhisper
//
//  Created by Assistant on 16/08/2025.
//

import Foundation
import AppKit
import ApplicationServices
import os

extension AccessibilityHelper {

    // MARK: - Focused Element Helpers

    /// Check if a text input field is currently focused
    /// This determines whether auto-paste will work
    /// - Returns: true if a text field/area is focused, false otherwise
    func isTextInputFocused() -> Bool {
        focusedTextInputElement() != nil
    }

    /// Returns the currently focused editable text element when Accessibility exposes one.
    func focusedTextInputElement() -> AXUIElement? {
        logger.debug("🔍 Checking if text input is focused...")

        // First check if we have accessibility permission
        guard AXIsProcessTrusted() else {
            logger.error("❌ No accessibility permission - cannot check focused element")
            return nil
        }

        // Get the system-wide accessibility element
        let systemWideElement = AXUIElementCreateSystemWide()
        var focusedElement: CFTypeRef?

        // Try to get the currently focused element
        let result = AXUIElementCopyAttributeValue(
            systemWideElement,
            kAXFocusedUIElementAttribute as CFString,
            &focusedElement
        )

        guard result == .success, let axElement = AXCast.element(focusedElement) else {
            let cause: String
            if result != .success {
                cause = "error: \(result.rawValue)"
            } else if focusedElement == nil {
                cause = "no value"
            } else {
                cause = "reply is not an AXUIElement"
            }
            logger.error("❌ Could not get focused element (\(cause, privacy: .public))")
            // Fallback 1: try the focused window, then search its children for an editable control
            var focusedWindowRef: CFTypeRef?
            let winRes = AXUIElementCopyAttributeValue(systemWideElement, kAXFocusedWindowAttribute as CFString, &focusedWindowRef)
            if winRes == .success, let winRef = AXCast.element(focusedWindowRef) {
                if let editable = findEditableChildRecursively(winRef, maxDepth: 5) {
                    logger.info("✅ Found editable element via focused window fallback: \(String(describing: editable), privacy: .public)")
                    return editable
                }
            }
            // Fallback 2: enumerate app's windows and search shallowly
            if let frontApp = NSWorkspace.shared.frontmostApplication {
                let appElement = AXUIElementCreateApplication(frontApp.processIdentifier)
                var appWindowsRef: CFTypeRef?
                if AXUIElementCopyAttributeValue(appElement, kAXWindowsAttribute as CFString, &appWindowsRef) == .success,
                   let windowsArray = appWindowsRef as? NSArray {
                    let windows = windowsArray.compactMap { AXCast.element($0 as CFTypeRef) }
                    for window in windows.prefix(3) { // check a few windows
                        if let editable = findEditableChildRecursively(window, maxDepth: 3) {
                            logger.info("✅ Found editable element via windows fallback: \(String(describing: editable), privacy: .public)")
                            return editable
                        }
                    }
                }
            }
            return nil
        }

        // Check if the focused element is text-editable
        if isElementTextEditable(axElement) {
            return axElement
        }

        // RECURSIVE SEARCH FOR NESTED TEXT INPUTS
        // Electron apps often have deep accessibility hierarchies where the actual
        // text input is nested within multiple container elements.
        // For Electron editors specifically, we do a deeper search.
        let searchDepth = (frontmostBundleId().map(isElectronCodeEditor) ?? false) ? 5 : 3
        if let editable = findEditableChildRecursively(axElement, maxDepth: searchDepth) {
            logger.info("✅ Found editable child element in hierarchy")
            return editable
        }

        logger.debug("❌ No text input found")
        return nil
    }

    /// Check if a specific AX element is text-editable
    /// This consolidates the logic for checking roles and attributes
    private func isElementTextEditable(_ element: AXUIElement) -> Bool {
        // Check the role of the element
        var roleValue: CFTypeRef?
        let roleResult = AXUIElementCopyAttributeValue(
            element,
            kAXRoleAttribute as CFString,
            &roleValue
        )

        guard roleResult == .success, let role = roleValue as? String else {
            return false
        }

        logger.debug("   Element role: \(role, privacy: .public)")

        // Check if the role indicates a text input field
        let textInputRoles = [
            "AXTextField",        // Standard text field
            "AXTextArea",         // Multi-line text area
            "AXComboBox",         // Combo box with text input
            "AXSearchField",      // Search fields
            "AXWebArea",          // Web content area (Electron apps)
            "AXGroup"             // Container that might have editable content
        ]

        let isTextInputRole = textInputRoles.contains(role)

        // For WebArea and Group roles, do additional checking
        if role == "AXWebArea" || role == "AXGroup" {
            // Check if it's editable via AXValue
            var isSettable = DarwinBoolean(false)
            let settableResult = AXUIElementIsAttributeSettable(
                element,
                kAXValueAttribute as CFString,
                &isSettable
            )

            if settableResult == .success && isSettable.boolValue {
                logger.info("✅ WebArea/Group has settable value - is editable")
                return true
            }

            // Check for AXEditableAncestor (indicates editable context)
            var editableAncestor: CFTypeRef?
            let ancestorResult = AXUIElementCopyAttributeValue(
                element,
                "AXEditableAncestor" as CFString,
                &editableAncestor
            )

            if ancestorResult == .success && editableAncestor != nil {
                logger.info("✅ Element has editable ancestor")
                return true
            }
        }

        if isTextInputRole {
            logger.info("✅ Standard text input role detected")
            return true
        }

        // Additional check: See if the element is editable
        var isSettable = DarwinBoolean(false)
        let settableResult = AXUIElementIsAttributeSettable(
            element,
            kAXValueAttribute as CFString,
            &isSettable
        )

        if settableResult == .success && isSettable.boolValue {
            logger.info("✅ Element has settable value attribute - is editable")
            return true
        }

        return false
    }

    /// Recursively search for an editable child element
    /// This helps find nested text inputs in complex Electron app hierarchies
    private func findEditableChildRecursively(_ element: AXUIElement, maxDepth: Int, currentDepth: Int = 0) -> AXUIElement? {
        if currentDepth >= maxDepth {
            return nil
        }

        // Check if this element itself is editable
        if isElementTextEditable(element) {
            return element
        }

        // Get children and check each one
        var childrenValue: CFTypeRef?
        let childrenResult = AXUIElementCopyAttributeValue(
            element,
            kAXChildrenAttribute as CFString,
            &childrenValue
        )

        guard childrenResult == .success,
              let childrenArray = childrenValue as? NSArray else {
            return nil
        }

        // Children come from the other app; drop any entry that is not an AXUIElement
        let children = childrenArray.compactMap { AXCast.element($0 as CFTypeRef) }

        // Limit search breadth to avoid performance issues
        for child in children.prefix(25) {
            if let editableChild = findEditableChildRecursively(child, maxDepth: maxDepth, currentDepth: currentDepth + 1) {
                return editableChild
            }
        }

        return nil
    }

    /// Check if a bundle ID belongs to an Electron-based code editor
    /// These apps require special handling as they don't expose standard AX roles
    func isElectronCodeEditor(_ bundleId: String) -> Bool {
        let electronEditors: Set<String> = [
            "com.todesktop.230313mzl4w4u92", // Cursor
            "com.cursor.ide",                 // Cursor (alternative bundle ID)
            "com.exafunction.windsurf",        // Windsurf
            "com.microsoft.VSCode",            // VS Code
            "com.microsoft.VSCodeInsiders"     // VS Code Insiders
        ]
        return electronEditors.contains(bundleId)
    }

    // MARK: - Paste into focused element

    func canPasteIntoFocusedElement() -> Bool {
        #if DEBUG
        // Debug-only test seam; a Release build always reads the real focus.
        if let override = canPasteOverrideForTesting { return override() }
        #endif

        // Browsers: both URL bar and web page inputs are valid paste targets
        if let bid = frontmostBundleId(), isBrowserBundleId(bid) {
            return true
        }

        // Standard role-based detection first
        if isTextInputFocused() {
            return true
        }

        // Last-resort blind paste whitelist for known Electron chat/edit boxes that hide AX focus
        if let bid = frontmostBundleId() {
            let blindPasteBundleIds: Set<String> = [
                "com.tinyspeck.slackmacgap", // Slack
                "com.todesktop.230313mzl4w4u92", // Cursor
                "com.cursor.ide", // Cursor alt
                "com.exafunction.windsurf", // Windsurf
                "com.microsoft.VSCode",
                "com.microsoft.VSCodeInsiders",
                // JetBrains IDEs (Java-based, AX focus detection often fails)
                "com.jetbrains.pycharm",
                "com.jetbrains.pycharm-EAP",
                "com.jetbrains.intellij",
                "com.jetbrains.intellij.ce",
                "com.jetbrains.WebStorm",
                "com.jetbrains.PhpStorm",
                "com.jetbrains.RubyMine",
                "com.jetbrains.AppCode",
                "com.jetbrains.CLion",
                "com.jetbrains.goland",
                "com.jetbrains.datagrip",
                "com.jetbrains.rider",
                "com.jetbrains.fleet",
                // Remote desktop apps (remote AX elements not exposed locally)
                "com.apple.ScreenSharing",
                "com.apple.RemoteDesktop",
            ]
            if blindPasteBundleIds.contains(bid) {
                logger.warning("⚠️ Falling back to blind paste whitelist for \(bid, privacy: .public)")
                return true
            }
        }

        return false
    }

    // MARK: - Focus Heuristics for Browsers

    /// Detects if a secure text field is focused (do not paste/type into these).
    func isSecureFieldFocused() -> Bool {
        guard AXIsProcessTrusted() else { return false }
        let system = AXUIElementCreateSystemWide()
        var focused: CFTypeRef?
        let res = AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused)
        guard res == .success, focused != nil else { return false }
        // A reply of the wrong type fails closed: we cannot rule out a password field, so skip the paste.
        guard let axElement = AXCast.element(focused) else { return true }
        var subroleValue: CFTypeRef?
        _ = AXUIElementCopyAttributeValue(axElement, kAXSubroleAttribute as CFString, &subroleValue)
        let subrole = subroleValue as? String ?? ""
        return subrole == "AXSecureTextField"
    }
}
