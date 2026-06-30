//
//  ScreenOCRCapture.swift
//  hyperwhisper
//
//  SCREEN OCR CAPTURE
//  Captures a screenshot of the display containing the frontmost window and
//  extracts visible text via Vision OCR. The resulting text is used as
//  additional context for AI post-processing to improve spelling of proper
//  nouns, technical terms, and domain-specific content.
//
//  DESIGN:
//  - Uses ScreenCaptureKit (macOS 14+) for screen capture
//  - Uses Vision framework for fast OCR text recognition
//  - Every failure path returns nil (graceful degradation)
//  - Never logs OCR text content (privacy) — only metadata like char count
//  - Self-capture guard: skips when HyperWhisper is the frontmost app
//  - 3-second timeout to prevent blocking the recording flow
//

import Foundation
import ScreenCaptureKit
import Vision
import os

// MARK: - Screen OCR Capture

/// Captures a screenshot and extracts text via OCR for post-processing context
@MainActor
class ScreenOCRCapture {

    // MARK: - Singleton

    /// Shared instance for app-wide use
    static let shared = ScreenOCRCapture()

    /// Private init to enforce singleton pattern
    private init() {}

    // MARK: - Public Methods

    /// Check if Screen Recording permission is available
    /// - Returns: `true` if ScreenCaptureKit content enumeration succeeds
    func hasScreenRecordingPermission() async -> Bool {
        do {
            _ = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            return true
        } catch {
            AppLogger.accessibility.debug("Screen Recording permission not granted")
            return false
        }
    }

    /// Capture display containing frontmost window and extract text via OCR
    ///
    /// Returns nil on failure, no permission, timeout, or when HyperWhisper is frontmost.
    /// All failure paths are silent (graceful degradation).
    ///
    /// - Parameters:
    ///   - frontmostPID: The process identifier of the frontmost application at recording start
    ///   - maxCharacters: Maximum number of characters to return (default: 2000)
    /// - Returns: Extracted text from the screen, or nil
    func captureAndOCR(frontmostPID: pid_t?, maxCharacters: Int = 2000) async -> String? {
        // Skip when frontmost PID is unknown (can't verify we're not capturing ourselves)
        guard let pid = frontmostPID else {
            AppLogger.audio.debug("Screen OCR skipped: frontmost PID unknown")
            return nil
        }

        // Self-capture guard: skip when HyperWhisper is the frontmost app
        if pid == ProcessInfo.processInfo.processIdentifier {
            AppLogger.audio.debug("Screen OCR skipped: HyperWhisper is frontmost")
            return nil
        }

        // Race the capture+OCR against a 3-second timeout
        return await withTaskGroup(of: String?.self) { group in
            group.addTask {
                await self.performCaptureAndOCR(frontmostPID: pid, maxCharacters: maxCharacters)
            }

            group.addTask {
                try? await Task.sleep(for: .seconds(3))
                return nil
            }

            // Return the first result (nil = timeout or failure), cancel the rest
            let result = await group.next() ?? nil
            group.cancelAll()
            return result ?? nil
        }
    }

    // MARK: - Private Methods

    /// Performs the actual screen capture and OCR pipeline
    private func performCaptureAndOCR(frontmostPID: pid_t, maxCharacters: Int) async -> String? {
        // Step 1: Get shareable content (also serves as permission check)
        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        } catch {
            AppLogger.accessibility.warning("Screen OCR: failed to get shareable content (permission not granted?)")
            return nil
        }

        guard !Task.isCancelled else { return nil }

        // Step 2: Find the display containing the frontmost window
        let targetDisplay = findDisplay(for: frontmostPID, in: content)
        guard let display = targetDisplay else {
            AppLogger.audio.warning("Screen OCR: no display found")
            return nil
        }

        // Step 3: Capture screenshot at native resolution
        let image: CGImage
        do {
            let config = SCStreamConfiguration()
            config.width = display.width
            config.height = display.height

            let filter = SCContentFilter(display: display, excludingWindows: [])
            image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
        } catch {
            AppLogger.audio.warning("Screen OCR: screenshot capture failed")
            return nil
        }

        guard !Task.isCancelled else { return nil }

        // Step 4: Run OCR on a background queue
        let text = await performOCR(on: image, maxCharacters: maxCharacters)
        return text
    }

    /// Find the SCDisplay that contains the window belonging to frontmostPID
    private func findDisplay(for pid: pid_t, in content: SCShareableContent) -> SCDisplay? {
        guard !content.displays.isEmpty else { return nil }

        // Try to find a window matching the frontmost PID
        if let matchingWindow = content.windows.first(where: { $0.owningApplication?.processID == pid }) {
            // Find which display contains this window's frame
            let windowFrame = matchingWindow.frame
            let windowCenter = CGPoint(
                x: windowFrame.origin.x + windowFrame.width / 2,
                y: windowFrame.origin.y + windowFrame.height / 2
            )

            for display in content.displays {
                if display.frame.contains(windowCenter) {
                    return display
                }
            }
        }

        // Fallback: return the first (primary) display
        return content.displays.first
    }

    /// Perform OCR text recognition on a CGImage, off the main actor
    private func performOCR(on image: CGImage, maxCharacters: Int) async -> String? {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                let handler = VNImageRequestHandler(cgImage: image)
                let request = VNRecognizeTextRequest()
                request.recognitionLevel = .accurate
                request.usesLanguageCorrection = false
                // Don't set recognitionLanguages — let Vision auto-detect

                try? handler.perform([request])

                let observations = request.results ?? []
                let text = observations
                    .filter { $0.confidence >= 0.4 }
                    .sorted { a, b in
                        // Sort top-to-bottom, then left-to-right
                        // Vision uses normalized coordinates (0,0 at bottom-left)
                        let aBox = a.boundingBox
                        let bBox = b.boundingBox
                        // Higher Y = higher on screen (Vision coordinates)
                        // Compare rows first (with tolerance for same-line text)
                        let rowTolerance: CGFloat = 0.01
                        if abs(aBox.origin.y - bBox.origin.y) > rowTolerance {
                            return aBox.origin.y > bBox.origin.y  // Top-to-bottom
                        }
                        return aBox.origin.x < bBox.origin.x  // Left-to-right
                    }
                    .compactMap { $0.topCandidates(1).first?.string }
                    .joined(separator: " ")

                let truncated = String(text.prefix(maxCharacters))
                continuation.resume(returning: truncated.isEmpty ? nil : truncated)
            }
        }
    }
}
