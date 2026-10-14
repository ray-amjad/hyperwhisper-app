//
//  ShortcutConflictGuard.swift
//  hyperwhisper
//
//  Issue #1474: a shortcut recorder refuses a combo that another HyperWhisper
//  action already uses, and says which action.
//
//  Why the recorder alone cannot do it: every live action's combo is a Carbon
//  hotkey, and the KeyboardShortcuts library's Carbon handler consumes a hotkey
//  press even while a recorder is active (it only skips the action). The
//  keystroke never reaches the recorder, so the press was dropped with no
//  message. While any recorder is active this guard pauses the library's
//  hotkeys (`KeyboardShortcuts.isEnabled`, which keeps their registrations)
//  and holds each live action's combo itself. A press of one of those combos
//  then shows "already used by <action>"; every other combo reaches the
//  recorder as before.
//

import AppKit
import Carbon.HIToolbox
import KeyboardShortcuts

@MainActor
final class ShortcutConflictGuard {
    static let shared = ShortcutConflictGuard()

    /// "HWSC": marks the hotkeys this guard registers, so the library's
    /// handler ignores them and ours ignores the library's.
    private static let hotKeySignature: OSType = 0x4857_5343

    /// The library posts this (with `isActive`) when a recorder starts and
    /// stops recording. The name is internal to the library, so it is
    /// spelled out here.
    private static let recorderActiveStatusDidChange = Notification.Name("KeyboardShortcuts_recorderActiveStatusDidChange")

    private var observer: NSObjectProtocol?
    private var eventHandler: EventHandlerRef?
    private var hotKeys: [EventHotKeyRef] = []
    /// Index = hotkey id: the combo each held hotkey stands for.
    private var heldCombos: [KeyboardShortcuts.Shortcut] = []
    private var isGuarding = false

    private init() {}

    /// Starts listening for recorder activity. Idempotent.
    func start() {
        guard observer == nil else { return }
        observer = NotificationCenter.default.addObserver(
            forName: Self.recorderActiveStatusDidChange,
            object: nil,
            queue: nil
        ) { notification in
            let isActive = notification.userInfo?["isActive"] as? Bool ?? false
            MainActor.assumeIsolated {
                ShortcutConflictGuard.shared.recorderActivityChanged(isActive: isActive)
            }
        }
    }

    // MARK: - Pure rules (unit-tested)

    /// The actions whose combo is live right now. A feature that is off has
    /// released its hotkey and its row is hidden, so it does not block a combo.
    nonisolated static func liveActions(
        pushToTalkMode: PushToTalkMode,
        quickCaptureEnabled: Bool,
        streamingEnabled: Bool
    ) -> [KeyboardShortcuts.Name] {
        var names: [KeyboardShortcuts.Name] = [.toggleRecordingWithTranscription, .cancelRecording, .changeMode]
        if pushToTalkMode == .custom { names.append(.pushToTalk) }
        if quickCaptureEnabled { names.append(.quickCapture) }
        if streamingEnabled { names.append(.startStreaming) }
        return names
    }

    /// The first live action other than `recording` that already uses `combo`,
    /// or nil when the combo is free (or only `recording` itself uses it).
    nonisolated static func conflictingAction<Name: Equatable, Combo: Equatable>(
        for combo: Combo,
        recording: Name?,
        bindings: [(name: Name, shortcut: Combo?)]
    ) -> Name? {
        bindings.first { $0.name != recording && $0.shortcut == combo }?.name
    }

    /// The user-facing name of an action, as its Settings row shows it.
    nonisolated static func displayName(for name: KeyboardShortcuts.Name) -> String {
        switch name {
        case .toggleRecordingWithTranscription: return "settings.shortcuts.toggleRecording.title".localized
        case .cancelRecording: return "settings.shortcuts.cancelRecording.title".localized
        case .changeMode: return "settings.shortcuts.changeMode.title".localized
        case .pushToTalk: return "settings.shortcuts.pushToTalk.enable.title".localized
        case .quickCapture: return "settings.shortcuts.quickCapture.enable.title".localized
        case .startStreaming: return "streaming.title".localized
        default: return name.rawValue
        }
    }

    // MARK: - Recorder activity

    private func recorderActivityChanged(isActive: Bool) {
        // Always drop the previous set first: a recorder that just saved a
        // new combo changes what must be held for the next one.
        stopGuarding()
        if isActive {
            startGuarding()
        }
    }

    private func liveBindings() -> [(name: KeyboardShortcuts.Name, shortcut: KeyboardShortcuts.Shortcut?)] {
        let settings = SettingsManager.shared
        return Self.liveActions(
            pushToTalkMode: settings.pushToTalkMode,
            quickCaptureEnabled: settings.quickCaptureEnabled,
            streamingEnabled: settings.streamingEnabled
        ).map { ($0, KeyboardShortcuts.getShortcut(for: $0)) }
    }

    private func startGuarding() {
        // Soft-unregisters the library's hotkeys and keeps its bookkeeping,
        // so `stopGuarding` restores exactly what was registered before.
        KeyboardShortcuts.isEnabled = false
        isGuarding = true
        installEventHandlerIfNeeded()

        var combos: [KeyboardShortcuts.Shortcut] = []
        for shortcut in liveBindings().compactMap({ $0.shortcut }) where !combos.contains(shortcut) {
            combos.append(shortcut)
        }

        for (index, combo) in combos.enumerated() {
            var hotKey: EventHotKeyRef?
            let status = RegisterEventHotKey(
                UInt32(combo.carbonKeyCode),
                UInt32(combo.carbonModifiers),
                EventHotKeyID(signature: Self.hotKeySignature, id: UInt32(index)),
                GetEventDispatcherTarget(),
                0,
                &hotKey
            )
            if status == noErr, let hotKey {
                hotKeys.append(hotKey)
            } else {
                AppLogger.ui.debug("⌨️ Shortcut conflict guard could not hold a combo (status \(status, privacy: .public))")
            }
        }
        heldCombos = combos
    }

    private func stopGuarding() {
        guard isGuarding else { return }
        // Release ours before the library re-registers the same combos.
        hotKeys.forEach { UnregisterEventHotKey($0) }
        hotKeys = []
        heldCombos = []
        isGuarding = false
        KeyboardShortcuts.isEnabled = true
    }

    private func installEventHandlerIfNeeded() {
        guard eventHandler == nil else { return }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        var handler: EventHandlerRef?
        // Installed after the library's handler, so Carbon asks this one first.
        let status = InstallEventHandler(
            GetEventDispatcherTarget(),
            { _, event, _ in
                guard let event else { return OSStatus(eventNotHandledErr) }
                return MainActor.assumeIsolated {
                    ShortcutConflictGuard.shared.handleHotKeyPressed(event)
                }
            },
            1,
            &spec,
            nil,
            &handler
        )
        if status == noErr {
            eventHandler = handler
        }
    }

    private func handleHotKeyPressed(_ event: EventRef) -> OSStatus {
        var hotKeyID = EventHotKeyID()
        let status = GetEventParameter(
            event,
            EventParamName(kEventParamDirectObject),
            EventParamType(typeEventHotKeyID),
            nil,
            MemoryLayout<EventHotKeyID>.size,
            nil,
            &hotKeyID
        )
        guard
            status == noErr,
            hotKeyID.signature == Self.hotKeySignature
        else {
            return OSStatus(eventNotHandledErr)
        }

        guard
            isGuarding,
            heldCombos.indices.contains(Int(hotKeyID.id)),
            let recorder = Self.activeRecorder()
        else {
            return noErr
        }

        let combo = heldCombos[Int(hotKeyID.id)]
        guard let owner = Self.conflictingAction(
            for: combo,
            recording: recorder.shortcutName,
            bindings: liveBindings()
        ) else {
            // The recorder's own combo: nothing changes, as before.
            return noErr
        }

        // Not from inside the Carbon callback: the alert runs a modal loop.
        DispatchQueue.main.async {
            Self.showConflictAlert(owner: owner, recorder: recorder)
        }
        return noErr
    }

    /// The recorder being edited: its field editor is the first responder and
    /// sits inside the recorder's view hierarchy.
    private static func activeRecorder() -> KeyboardShortcuts.RecorderCocoa? {
        for window in [NSApp.keyWindow].compactMap({ $0 }) + NSApp.windows {
            guard let editor = window.firstResponder as? NSText else { continue }
            if let recorder = (editor.delegate as AnyObject?) as? KeyboardShortcuts.RecorderCocoa {
                return recorder
            }
            var view: NSView? = editor
            while let current = view {
                if let recorder = current as? KeyboardShortcuts.RecorderCocoa {
                    return recorder
                }
                view = current.superview
            }
        }
        return nil
    }

    private static func showConflictAlert(owner: KeyboardShortcuts.Name, recorder: KeyboardShortcuts.RecorderCocoa) {
        AppLogger.ui.info("⌨️ Refused a shortcut already used by \(owner.rawValue, privacy: .public) in the \(recorder.shortcutName.rawValue, privacy: .public) recorder")
        let window = recorder.window
        // Same flow as the library's menu-item conflict: stop recording, show
        // the alert, then put the recorder back in recording mode.
        window?.makeFirstResponder(nil)

        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "settings.shortcuts.conflict.alert".localized(arguments: displayName(for: owner))
        if let window {
            alert.beginSheetModal(for: window) { response in
                NSApp.stopModal(withCode: response)
            }
            NSApp.runModal(for: window)
        } else {
            alert.runModal()
        }

        window?.makeFirstResponder(recorder)
    }
}
