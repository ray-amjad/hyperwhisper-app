//
//  ApplicationContextGatherer.swift
//  hyperwhisper
//
//  APPLICATION CONTEXT GATHERER
//  Dynamically gathers context about the frontmost application using the Accessibility API.
//  This provides real-time information about what the user is working on to improve
//  transcription accuracy and AI post-processing.
//
//  FEATURES:
//  - Detects frontmost application name and bundle ID
//  - Prefers Browser Tab Title (AX-only) over URL for web context
//    • No AppleScript/Automation fallback is used (privacy-friendly)
//    • URL extraction remains best-effort via AX (address bar/title parsing) only
//  - Gets focused element information (role, title, description, value, placeholder)
//    • Descends into AXWebArea to find the actual focused editable element
//    • Captures selected text and placeholder when available
//  - Computes a Context Quality indicator (Full/Partial/Limited)
//  - Categorizes applications (browser, code editor, etc.)
//  - Works without Screen Recording permission (only needs Accessibility)
//
//  IMPORTANT: All methods gracefully handle missing Accessibility permission
//  and return sensible defaults when information is unavailable.

import Foundation
import AppKit
import ApplicationServices

// MARK: - Application Context

/// Represents the current application context
public struct ApplicationContext {
    /// A stable, app-agnostic context. Use for callers (like the local API
    /// `POST /post-process`) where there is no meaningful "frontmost app" —
    /// the foreground state would otherwise leak into the SYSTEM prompt's
    /// contextual-formatting block and bust the prompt cache between calls.
    public static let none = ApplicationContext(
        appName: "",
        bundleId: "",
        category: "",
        description: "",
        browserTabTitle: nil,
        browserHost: nil,
        appType: .other,
        appTypeConfidence: "unknown",
        appTypeSource: "default",
        focusedElement: FocusedElementInfo(role: nil, title: nil, description: nil, value: nil, placeholder: nil),
        textInputFormat: "text",
        contextQuality: "Limited",
        screenOCRText: nil
    )

    /// Name of the frontmost application
    let appName: String
    
    /// Bundle identifier of the frontmost application
    let bundleId: String
    
    /// Category of the application (e.g., "Web Browser", "Code Editor")
    let category: String
    
    /// Description of the application
    let description: String
    
    /// Current browser tab title if it's a browser, nil otherwise
    let browserTabTitle: String?

    /// Browser host/domain when available. Kept separate from full URL for privacy.
    let browserHost: String?

    /// Deterministic app classification used for app-aware formatting.
    let appType: AppType

    /// Confidence for appType: strong, medium, weak, unknown.
    let appTypeConfidence: String

    /// Signal that produced appType: bundleId, browserHost, title, focusedElement, default.
    let appTypeSource: String
    
    /// Information about the focused UI element
    let focusedElement: FocusedElementInfo
    
    /// Text input format hint (e.g., "url", "code", "text")
    let textInputFormat: String

    /// Overall quality indicator for gathered context (Full, Partial, Limited)
    let contextQuality: String

    /// OCR text extracted from the screen at recording start (nil when disabled/failed/not captured)
    let screenOCRText: String?
}

/// Information about the currently focused UI element
public struct FocusedElementInfo {
    /// Accessibility role (e.g., "AXTextField", "AXTextArea")
    let role: String?
    
    /// Title of the element
    let title: String?
    
    /// Description of the element
    let description: String?
    
    /// Current value/content of the element
    let value: String?

    /// Placeholder text if available (for inputs/textareas)
    let placeholder: String?
}

// MARK: - Application Context Gatherer

/// Gathers context about the frontmost application and focused elements
@MainActor
public class ApplicationContextGatherer {
    
    // MARK: - Singleton
    
    /// Shared instance for app-wide use
    public static let shared = ApplicationContextGatherer()
    
    /// Private init to enforce singleton pattern
    private init() {}
    
    // MARK: - Public Methods
    
    /// Gather complete application context
    /// - Parameters:
    ///   - screenOCRText: Optional OCR text extracted from the screen at recording start
    /// - Returns: ApplicationContext with current app and focus information
    ///
    /// Design notes:
    /// - Browser context emphasizes Tab Title over URL for privacy and reliability.
    /// - All data is gathered via the AX (Accessibility) API only; no Automation.
    /// - A coarse Context Quality indicates completeness of the captured context.
    public func gatherContext(screenOCRText: String? = nil, frontmostPID: pid_t? = nil) -> ApplicationContext {
        // Get frontmost application info
        let (appName, bundleId) = getFrontmostAppInfo(pid: frontmostPID)

        // Categorize the application
        let initialCategory = categorizeApplication(bundleId: bundleId)

        // Get tab title and host (AX-only) if applicable
        let tabTitle = isBrowser(bundleId) ? extractBrowserTabTitle(pid: frontmostPID) : nil
        let browserHost = isBrowser(bundleId) ? extractBrowserHost(pid: frontmostPID) : nil

        // Get focused element information
        let focusedElement = getFocusedElementInfo(pid: frontmostPID)

        let appClassification = AppTypeClassifier.shared.classify(
            bundleId: bundleId,
            appName: appName,
            browserHost: browserHost,
            browserTitle: tabTitle,
            focusedElement: focusedElement
        )

        var category = appClassification.appType == .other ? initialCategory : appClassification.appType.category

        // Get application description
        let description = getApplicationDescription(bundleId: bundleId, category: category)
        
        // Determine text input format hint
        var textInputFormat = determineTextInputFormat(
            bundleId: bundleId,
            category: category,
            focusedElement: focusedElement
        )

        // Prefer the shared classifier for strong app-aware formatting signals.
        if appClassification.appType != .other {
            textInputFormat = appClassification.appType.textInputFormat
        }

        // Safety net for webmail titles when browser URL/host is unavailable via AX.
        let resolvedAppType: AppType
        let resolvedConfidence: String
        let resolvedSource: String
        if appClassification.appType == .other,
           initialCategory == "Web Browser",
           let tabTitle,
           ApplicationContextGatherer.isWebmail(tabTitle) {
            category = AppType.email.category
            textInputFormat = AppType.email.textInputFormat
            resolvedAppType = .email
            resolvedConfidence = "weak"
            resolvedSource = "webmailTitleFallback"
        } else {
            resolvedAppType = appClassification.appType
            resolvedConfidence = appClassification.confidence
            resolvedSource = appClassification.source
        }
        
        // Determine overall context quality
        let contextQuality = determineContextQuality(tabTitle: tabTitle, focusedElement: focusedElement)

        return ApplicationContext(
            appName: appName,
            bundleId: bundleId,
            category: category,
            description: description,
            browserTabTitle: tabTitle,
            browserHost: browserHost,
            appType: resolvedAppType,
            appTypeConfidence: resolvedConfidence,
            appTypeSource: resolvedSource,
            focusedElement: focusedElement,
            textInputFormat: textInputFormat,
            contextQuality: contextQuality,
            screenOCRText: screenOCRText
        )
    }
    
    // NOTE: `formatContextForPrompt(_:)` and `xmlEscaped(_:)` were removed in the
    // Wave-3 shared-core swap. App-context formatting (XML escaping, the
    // <APPLICATION_CONTEXT>/<SCREEN_CONTEXT> blocks, focused-element join and
    // focused-content truncation) is now produced by the Rust core's
    // `build_system_info` via `PromptBuilder`. AX/Accessibility GATHERING (below)
    // stays native.

    // MARK: - Private Methods
    
    /// Get frontmost application name and bundle ID
    private func getFrontmostAppInfo(pid: pid_t? = nil) -> (name: String, bundleId: String) {
        if let pid, let app = NSRunningApplication(processIdentifier: pid) {
            return (app.localizedName ?? "Unknown", app.bundleIdentifier ?? "")
        }
        guard let app = NSWorkspace.shared.frontmostApplication else {
            return ("Unknown", "")
        }
        
        let name = app.localizedName ?? "Unknown"
        let bundleId = app.bundleIdentifier ?? ""
        
        return (name, bundleId)
    }
    
    /// Categorize application based on bundle ID
    private func categorizeApplication(bundleId: String) -> String {
        // Check if it's a browser
        if isBrowser(bundleId) {
            return "Web Browser"
        }
        
        // Check if it's a code editor
        if ElectronAppDetector.shared.isElectronEditor(bundleId) {
            return "Code Editor"
        }
        
        // Check for other specific categories
        switch bundleId {
        case "com.tinyspeck.slackmacgap":
            return "Communication"
        case "com.apple.mail", "com.readdle.smartemail", "com.microsoft.Outlook":
            return "Email Client"
        case "com.apple.Notes", "com.evernote.Evernote", "com.microsoft.onenote.mac":
            return "Note Taking"
        case "com.apple.iWork.Pages", "com.microsoft.Word", "com.google.docs":
            return "Word Processor"
        case "com.apple.iWork.Keynote", "com.microsoft.Powerpoint", "com.google.slides":
            return "Presentation"
        case "com.apple.iWork.Numbers", "com.microsoft.Excel", "com.google.sheets":
            return "Spreadsheet"
        case "com.apple.dt.Xcode":
            return "IDE"
        case "com.apple.Terminal",
             "com.googlecode.iterm2",
             "com.cmuxterm.app",
             "com.github.wez.wezterm",
             "com.mitchellh.ghostty",
             "dev.warp.Warp-Stable",
             "dev.warp.WarpPreview",
             "io.alacritty",
             "net.kovidgoyal.kitty":
            return "Terminal"
        default:
            return "Application"
        }
    }
    
    /// Get application description based on bundle ID and category
    private func getApplicationDescription(bundleId: String, category: String) -> String {
        // Specific descriptions for known apps
        switch bundleId {
        case "com.apple.Safari":
            return "Apple's web browser with strong privacy features"
        case "com.google.Chrome":
            return "A fast, secure, and free web browser built for the modern web"
        case "org.mozilla.firefox":
            return "Privacy-focused open source web browser"
        case "com.microsoft.edgemac":
            return "Microsoft's Chromium-based web browser"
        case "company.thebrowser.Arc":
            return "Modern browser with space-based organization"
        case "com.brave.Browser":
            return "Privacy-focused browser with built-in ad blocking"
        case "com.microsoft.VSCode":
            return "Lightweight but powerful source code editor"
        case "com.todesktop.230313mzl4w4u92", "com.cursor.ide":
            return "AI-powered code editor based on VS Code"
        case "com.exafunction.windsurf":
            return "AI-native code editor for modern development"
        case "com.tinyspeck.slackmacgap":
            return "Team communication and collaboration platform"
        case "com.apple.dt.Xcode":
            return "Apple's integrated development environment for macOS"
        default:
            // Generic description based on category
            switch category {
            case "Web Browser":
                return "Web browsing application"
            case "Code Editor":
                return "Source code editing application"
            case "Communication":
                return "Communication and messaging application"
            case "Email Client":
                return "Email management application"
            case "Note Taking":
                return "Note-taking and organization application"
            case "Word Processor":
                return "Document creation and editing application"
            case "Terminal":
                return "Command-line interface application"
            default:
                return "Desktop application"
            }
        }
    }
    
    /// Check if bundle ID is a browser
    private func isBrowser(_ bundleId: String) -> Bool {
        return AccessibilityHelper.shared.isBrowserBundleId(bundleId)
    }
    
    /// Extract the browser tab title using AX only.
    /// Strategy:
    /// 1) AXWebArea's title (page title) when available.
    /// 2) Focused window's title as fallback, normalized for browser suffixes.
    private func extractBrowserTabTitle(pid: pid_t? = nil) -> String? {
        guard AXIsProcessTrusted() else { return nil }
        let targetPID: pid_t
        if let pid {
            targetPID = pid
        } else {
            guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
            targetPID = app.processIdentifier
        }
        let appElement = AXUIElementCreateApplication(targetPID)

        // Focused window
        var focusedWindowRef: CFTypeRef?
        let windowResult = AXUIElementCopyAttributeValue(
            appElement,
            kAXFocusedWindowAttribute as CFString,
            &focusedWindowRef
        )
        var windowElement: AXUIElement?
        if windowResult == .success, let win = focusedWindowRef {
            // Force cast is safe here - AXUIElementCopyAttributeValue guarantees an AXUIElement
            windowElement = unsafeBitCast(win, to: AXUIElement.self)
        }

        // Try AXWebArea title first
        if let winEl = windowElement,
           let webArea = findElementByRoleRecursively(in: winEl, role: "AXWebArea", maxDepth: 6) {
            var webTitleRef: CFTypeRef?
            if AXUIElementCopyAttributeValue(webArea, kAXTitleAttribute as CFString, &webTitleRef) == .success,
               let t = webTitleRef as? String, !t.isEmpty {
                return normalizeWindowTitle(t)
            }
        }

        // Fallback: window title
        if let winEl = windowElement {
            var titleRef: CFTypeRef?
            if AXUIElementCopyAttributeValue(winEl, kAXTitleAttribute as CFString, &titleRef) == .success,
               let t = titleRef as? String, !t.isEmpty {
                return normalizeWindowTitle(t)
            }
        }

        return nil
    }

    /// Normalize window/tab title by stripping common browser suffixes.
    /// Example: "Foo — Google Chrome" -> "Foo"
    private func normalizeWindowTitle(_ title: String) -> String {
        var t = title
        let suffixes = [
            " - Google Chrome", " - Chrome", " - Brave", " - Microsoft Edge", " - Safari",
            " — Google Chrome", " — Chrome", " — Brave", " — Microsoft Edge", " — Safari"
        ]
        for s in suffixes where t.hasSuffix(s) {
            t.removeLast(s.count)
            break
        }
        return t
    }

    /// Extract the active browser host from AXWebArea's AXURL when the browser exposes it.
    private func extractBrowserHost(pid: pid_t? = nil) -> String? {
        guard AXIsProcessTrusted() else { return nil }
        let targetPID: pid_t
        if let pid {
            targetPID = pid
        } else {
            guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
            targetPID = app.processIdentifier
        }

        let appElement = AXUIElementCreateApplication(targetPID)
        guard let windowElement = focusedWindowElement(for: appElement),
              let webArea = findElementByRoleRecursively(in: windowElement, role: "AXWebArea", maxDepth: 6) else {
            return nil
        }

        var urlRef: CFTypeRef?
        if AXUIElementCopyAttributeValue(webArea, "AXURL" as CFString, &urlRef) == .success {
            if let url = urlRef as? URL {
                return normalizedHost(from: url)
            }
            if let urlString = urlRef as? String,
               let url = URL(string: urlString) {
                return normalizedHost(from: url)
            }
        }

        return nil
    }

    private func focusedWindowElement(for appElement: AXUIElement) -> AXUIElement? {
        var focusedWindowRef: CFTypeRef?
        if AXUIElementCopyAttributeValue(
            appElement,
            kAXFocusedWindowAttribute as CFString,
            &focusedWindowRef
        ) == .success, let win = focusedWindowRef {
            return (win as! AXUIElement)
        }
        return nil
    }

    private func normalizedHost(from url: URL) -> String? {
        guard let host = url.host?.lowercased(), !host.isEmpty else { return nil }
        return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }

    /// Determine context quality string based on captured signals.
    /// Full: both tab title and focused text present; Partial: either; Limited: neither.
    private func determineContextQuality(tabTitle: String?, focusedElement: FocusedElementInfo) -> String {
        let hasTitle = (tabTitle?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false)
        let hasText: Bool = {
            if let v = focusedElement.value, !v.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return true }
            return false
        }()
        if hasTitle && hasText { return "Full" }
        if hasTitle || hasText { return "Partial" }
        return "Limited"
    }
    
    /// Get information about the currently focused element
    private func getFocusedElementInfo(pid: pid_t? = nil) -> FocusedElementInfo {
        // Must have accessibility permission
        guard AXIsProcessTrusted() else {
            return FocusedElementInfo(role: nil, title: nil, description: nil, value: nil, placeholder: nil)
        }

        let systemWideElement = AXUIElementCreateSystemWide()
        var focusedElementRef: CFTypeRef?

        let result = AXUIElementCopyAttributeValue(
            systemWideElement,
            kAXFocusedUIElementAttribute as CFString,
            &focusedElementRef
        )

        if result != .success || focusedElementRef == nil {
            // Fallback: attempt to infer focus from the frontmost window hierarchy
            if let fallback = fallbackFocusInfoFromFrontmostWindow(pid: pid) {
                return fallback
            }
            return FocusedElementInfo(role: nil, title: nil, description: nil, value: nil, placeholder: nil)
        }
        
        let axElement = focusedElementRef as! AXUIElement
        
        // Get role
        var roleRef: CFTypeRef?
        AXUIElementCopyAttributeValue(axElement, kAXRoleAttribute as CFString, &roleRef)
        let role = roleRef as? String
        
        // SPECIAL HANDLING FOR WEB CONTENT:
        // If this is a web area, try to find the actual focused element within it
        if role == "AXWebArea" {
            if let webFocused = getFocusedElementInWebArea(axElement) {
                return webFocused
            }
        }
        
        // Get title
        var titleRef: CFTypeRef?
        AXUIElementCopyAttributeValue(axElement, kAXTitleAttribute as CFString, &titleRef)
        let title = titleRef as? String
        
        // Get description
        var descRef: CFTypeRef?
        AXUIElementCopyAttributeValue(axElement, kAXDescriptionAttribute as CFString, &descRef)
        let description = descRef as? String
        
        // Get value (text content for text fields)
        var valueRef: CFTypeRef?
        AXUIElementCopyAttributeValue(axElement, kAXValueAttribute as CFString, &valueRef)
        let value = valueRef as? String
        
        // ADDITIONAL ATTRIBUTES FOR WEB CONTENT:
        // Check for contenteditable or other web-specific attributes
        var roleDescRef: CFTypeRef?
        AXUIElementCopyAttributeValue(axElement, kAXRoleDescriptionAttribute as CFString, &roleDescRef)
        let roleDescription = roleDescRef as? String
        
        // If no value, but it's an editable text area, try to get selected text
        var selectedTextRef: CFTypeRef?
        AXUIElementCopyAttributeValue(axElement, kAXSelectedTextAttribute as CFString, &selectedTextRef)
        let selectedText = selectedTextRef as? String

        // Placeholder value if available
        var placeholderRef: CFTypeRef?
        AXUIElementCopyAttributeValue(axElement, "AXPlaceholderValue" as CFString, &placeholderRef)
        let placeholder = placeholderRef as? String
        
        return FocusedElementInfo(
            role: role,
            title: title,
            description: description ?? roleDescription,
            value: value ?? selectedText,
            placeholder: placeholder
        )
    }
    
    /// Get focused element within a web area
    private func getFocusedElementInWebArea(_ webArea: AXUIElement) -> FocusedElementInfo? {
        // Try to get the selected text or focused element within the web area
        var selectedTextRef: CFTypeRef?
        AXUIElementCopyAttributeValue(webArea, kAXSelectedTextAttribute as CFString, &selectedTextRef)
        
        // Try to get focused UI element within web area
        var focusedRef: CFTypeRef?
        let focusedResult = AXUIElementCopyAttributeValue(
            webArea,
            kAXFocusedUIElementAttribute as CFString,
            &focusedRef
        )
        
        if focusedResult == .success,
           let focused = focusedRef {
            let focusedElement = focused as! AXUIElement
            
            // Get attributes of the focused element within web area
            var roleRef: CFTypeRef?
            AXUIElementCopyAttributeValue(focusedElement, kAXRoleAttribute as CFString, &roleRef)
            
            var valueRef: CFTypeRef?
            AXUIElementCopyAttributeValue(focusedElement, kAXValueAttribute as CFString, &valueRef)
            
            var descRef: CFTypeRef?
            AXUIElementCopyAttributeValue(focusedElement, kAXDescriptionAttribute as CFString, &descRef)
            
            var titleRef: CFTypeRef?
            AXUIElementCopyAttributeValue(focusedElement, kAXTitleAttribute as CFString, &titleRef)

            var placeholderRef: CFTypeRef?
            AXUIElementCopyAttributeValue(focusedElement, "AXPlaceholderValue" as CFString, &placeholderRef)

            return FocusedElementInfo(
                role: roleRef as? String ?? "AXWebContent",
                title: titleRef as? String,
                description: descRef as? String ?? "Web content area",
                value: valueRef as? String ?? selectedTextRef as? String,
                placeholder: placeholderRef as? String
            )
        }
        
        // Fallback: return basic web area info
        return FocusedElementInfo(
            role: "AXWebArea",
            title: nil,
            description: "Web content area",
            value: selectedTextRef as? String,
            placeholder: nil
        )
    }

    /// Fallback: derive focused element info by scanning the frontmost window hierarchy
    private func fallbackFocusInfoFromFrontmostWindow(pid: pid_t? = nil) -> FocusedElementInfo? {
        guard AXIsProcessTrusted() else { return nil }
        let targetPID: pid_t
        if let pid {
            targetPID = pid
        } else {
            guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
            targetPID = app.processIdentifier
        }
        let appElement = AXUIElementCreateApplication(targetPID)

        // Get focused window or first window
        var windowRef: CFTypeRef?
        var windowElement: AXUIElement?
        if AXUIElementCopyAttributeValue(appElement, kAXFocusedWindowAttribute as CFString, &windowRef) == .success,
           let win = windowRef {
            // Force cast is safe here - AXUIElementCopyAttributeValue guarantees an AXUIElement
            windowElement = unsafeBitCast(win, to: AXUIElement.self)
        } else {
            var windowsRef: CFTypeRef?
            if AXUIElementCopyAttributeValue(appElement, kAXWindowsAttribute as CFString, &windowsRef) == .success,
               let windowsArray = windowsRef as? NSArray,
               let first = windowsArray.firstObject as AnyObject? {
                // Force cast is safe here - window array contains AXUIElements
                windowElement = unsafeBitCast(first, to: AXUIElement.self)
            }
        }
        guard let windowEl = windowElement else { return nil }

        // Prefer web content if present
        if let webArea = findElementByRoleRecursively(in: windowEl, role: "AXWebArea", maxDepth: 6) {
            if let info = getFocusedElementInWebArea(webArea) { return info }
            return FocusedElementInfo(role: "AXWebArea", title: nil, description: "Web content area", value: nil, placeholder: nil)
        }

        // Otherwise return the first editable text element we can find
        if let textEl = findFirstEditableTextElement(in: windowEl, maxDepth: 6) {
            var roleRef: CFTypeRef?
            AXUIElementCopyAttributeValue(textEl, kAXRoleAttribute as CFString, &roleRef)
            var titleRef: CFTypeRef?
            AXUIElementCopyAttributeValue(textEl, kAXTitleAttribute as CFString, &titleRef)
            var descRef: CFTypeRef?
            AXUIElementCopyAttributeValue(textEl, kAXDescriptionAttribute as CFString, &descRef)
            var valueRef: CFTypeRef?
            AXUIElementCopyAttributeValue(textEl, kAXValueAttribute as CFString, &valueRef)
            return FocusedElementInfo(
                role: roleRef as? String,
                title: titleRef as? String,
                description: descRef as? String,
                value: valueRef as? String,
                placeholder: nil
            )
        }
        return nil
    }

    /// Recursively find the first element matching a specific role
    private func findElementByRoleRecursively(in element: AXUIElement, role targetRole: String, maxDepth: Int) -> AXUIElement? {
        guard maxDepth > 0 else { return nil }
        var roleRef: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &roleRef) == .success,
           let role = roleRef as? String,
           role == targetRole {
            return element
        }
        var childrenRef: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXChildrenAttribute as CFString, &childrenRef) == .success,
           let childrenArray = childrenRef as? NSArray {
            for childAny in childrenArray {
                let child = childAny as! AXUIElement
                if let found = findElementByRoleRecursively(in: child, role: targetRole, maxDepth: maxDepth - 1) {
                    return found
                }
            }
        }
        return nil
    }

    /// Recursively find a text-editable element (text field/area) by checking role or settable value
    private func findFirstEditableTextElement(in element: AXUIElement, maxDepth: Int) -> AXUIElement? {
        guard maxDepth > 0 else { return nil }
        // Check if element itself looks editable
        var roleRef: CFTypeRef?
        _ = AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &roleRef)
        let role = roleRef as? String ?? ""
        if role == "AXTextField" || role == "AXTextArea" || role == "AXSearchField" {
            return element
        }
        var settable = DarwinBoolean(false)
        if AXUIElementIsAttributeSettable(element, kAXValueAttribute as CFString, &settable) == .success,
           settable.boolValue {
            return element
        }
        // Recurse into children
        var childrenRef: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXChildrenAttribute as CFString, &childrenRef) == .success,
           let childrenArray = childrenRef as? NSArray {
            for childAny in childrenArray {
                let child = childAny as! AXUIElement
                if let found = findFirstEditableTextElement(in: child, maxDepth: maxDepth - 1) {
                    return found
                }
            }
        }
        return nil
    }
    
    // MARK: - Webmail Detection

    private static let webmailKeywords = [
        "gmail", "inbox", "mail.google",
        "outlook.live", "outlook.office",
        "mail.yahoo", "yahoo mail",
        "protonmail", "proton mail",
        "hey.com",
        "fastmail",
        "icloud.com/mail", "icloud mail",
        "zoho mail",
        "aol mail"
    ]

    /// Matches an email address pattern in the tab title (e.g. "user@domain.com")
    private static let emailAddressPattern = try! NSRegularExpression(
        pattern: #"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"#
    )

    private static func isWebmail(_ tabTitle: String) -> Bool {
        let lower = tabTitle.lowercased()
        if webmailKeywords.contains(where: { lower.contains($0) }) {
            return true
        }
        // Google Workspace tabs show "[Name] Mail" instead of "Gmail"
        let range = NSRange(tabTitle.startIndex..., in: tabTitle)
        return emailAddressPattern.firstMatch(in: tabTitle, range: range) != nil
    }

    /// Determine text input format hint based on context
    private func determineTextInputFormat(
        bundleId: String,
        category: String,
        focusedElement: FocusedElementInfo
    ) -> String {
        // Check for specific patterns in focused element
        if let value = focusedElement.value {
            // URL detection
            if value.hasPrefix("http://") || value.hasPrefix("https://") || value.hasPrefix("file://") {
                return "url"
            }
            
            // Code detection (simple heuristics)
            if value.contains("function") || value.contains("class") || 
               value.contains("import") || value.contains("const") ||
               value.contains("def ") || value.contains("if ") {
                return "code"
            }
            
            // Email detection
            if value.contains("@") && value.contains(".") {
                return "email"
            }
        }
        
        // Based on application category
        switch category {
        case "Web Browser":
            // Check if we're in URL bar
            if let role = focusedElement.role,
               role == "AXTextField",
               let desc = focusedElement.description?.lowercased(),
               (desc.contains("address") || desc.contains("url") || desc.contains("search")) {
                return "url"
            }
            return "text"
            
        case "Code Editor", "IDE":
            return "code"
            
        case "Terminal":
            return "command"
            
        case "Email Client":
            return "email"
            
        case "Note Taking":
            return "markdown"
            
        default:
            return "text"
        }
    }
}
