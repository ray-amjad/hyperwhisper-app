//
//  AccessibilityHelper+Paste.swift
//  hyperwhisper
//
//  Created by Assistant on 16/08/2025.
//

import Foundation
import AppKit
import ApplicationServices
import os

extension AccessibilityHelper {

    // MARK: - Synthetic Keyboard Events

    /// Send a synthetic ⌘V keystroke to paste.
    /// - Parameter allowBlindPaste: When true, skip AX focus validation and post the paste
    ///   shortcut directly. This uses a simpler clipboard paste strategy and is
    ///   more reliable in terminal hosts that do not expose a standard AX text field.
    /// - Returns: true if the key events were posted.
    func sendPasteCommand(allowBlindPaste: Bool = false) -> Bool {
        logger.info("📋 Attempting to send paste command (⌘V)...")

        guard AXIsProcessTrusted() else {
            logger.error("❌ No accessibility permission - cannot paste")
            return false
        }

        if !allowBlindPaste && !canPasteIntoFocusedElement() {
            logger.error("❌ No text input focused - not sending ⌘V")
            return false
        }

        guard let src = CGEventSource(stateID: .privateState) else {
            logger.error("❌ Failed to create CGEventSource")
            return false
        }
        logger.debug("   ✓ Created CGEventSource")

        let pasteKeyCode = keyCodeForV()

        // Direct paste strategy: explicit Command down, V down/up,
        // then Command up. Resolve V through the active keyboard layout so ⌘V remains
        // paste on layouts where the QWERTY V key position produces another character.
        guard
            let cmdDown = CGEvent(keyboardEventSource: src, virtualKey: 0x37, keyDown: true),
            let vDown = CGEvent(keyboardEventSource: src, virtualKey: pasteKeyCode, keyDown: true),
            let vUp = CGEvent(keyboardEventSource: src, virtualKey: pasteKeyCode, keyDown: false),
            let cmdUp = CGEvent(keyboardEventSource: src, virtualKey: 0x37, keyDown: false)
        else {
            logger.error("❌ Failed to create CGEvents")
            return false
        }
        cmdDown.flags = .maskCommand
        vDown.flags = .maskCommand
        vUp.flags = .maskCommand

        logger.debug("   Posting Command down / V down / V up / Command up to .cghidEventTap...")
        cmdDown.post(tap: .cghidEventTap)
        vDown.post(tap: .cghidEventTap)
        vUp.post(tap: .cghidEventTap)
        cmdUp.post(tap: .cghidEventTap)

        logger.info("✅ Paste command sent")
        return true
    }

    /// Send Control+Space to trigger autocomplete popup in Cursor/Windsurf
    /// - Returns: true if the key combination was sent successfully
    func sendControlSpace() -> Bool {
        logger.info("🔤 Sending Control+Space to trigger autocomplete...")

        guard AXIsProcessTrusted() else {
            logger.error("❌ No accessibility permission")
            return false
        }

        guard let src = CGEventSource(stateID: .hidSystemState) else {
            logger.error("❌ Failed to create CGEventSource")
            return false
        }

        // 0x31 is the virtual-key code for Space
        guard let spaceDown = CGEvent(keyboardEventSource: src, virtualKey: 0x31, keyDown: true),
              let spaceUp = CGEvent(keyboardEventSource: src, virtualKey: 0x31, keyDown: false) else {
            logger.error("❌ Failed to create CGEvents")
            return false
        }

        // Set Control modifier flag
        spaceDown.flags = .maskControl
        spaceUp.flags = .maskControl

        spaceDown.post(tap: .cghidEventTap)
        Thread.sleep(forTimeInterval: 0.01)
        spaceUp.post(tap: .cghidEventTap)

        logger.info("✅ Control+Space sent")
        return true
    }

    /// Send Tab key
    /// - Returns: true if the key was sent successfully
    func sendTab() -> Bool {
        logger.info("⇥ Sending Tab...")

        guard AXIsProcessTrusted() else {
            logger.error("❌ No accessibility permission")
            return false
        }

        guard let src = CGEventSource(stateID: .hidSystemState) else {
            logger.error("❌ Failed to create CGEventSource")
            return false
        }

        // 0x30 is the virtual-key code for Tab
        guard let tabDown = CGEvent(keyboardEventSource: src, virtualKey: 0x30, keyDown: true),
              let tabUp = CGEvent(keyboardEventSource: src, virtualKey: 0x30, keyDown: false) else {
            logger.error("❌ Failed to create CGEvents")
            return false
        }

        // Explicitly clear all modifier flags to ensure it's just Tab, not Cmd+Tab
        tabDown.flags = []
        tabUp.flags = []

        tabDown.post(tap: .cghidEventTap)
        Thread.sleep(forTimeInterval: 0.01)
        tabUp.post(tap: .cghidEventTap)

        logger.info("✅ Tab sent")
        return true
    }
}
