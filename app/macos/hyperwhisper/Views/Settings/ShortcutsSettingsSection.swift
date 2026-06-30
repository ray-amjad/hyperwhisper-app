//
//  ShortcutsSettingsSection.swift
//  hyperwhisper
//

import SwiftUI
import KeyboardShortcuts

struct ShortcutsSettingsSection: View {
    @EnvironmentObject var settingsManager: SettingsManager

    @State private var showResetShortcutsConfirmation = false

    /// Modes for the Quick Capture mode picker (sorted by sortOrder, matching ModesView).
    @FetchRequest(
        sortDescriptors: [NSSortDescriptor(keyPath: \Mode.sortOrder, ascending: true)]
    )
    private var modes: FetchedResults<Mode>

    /// Banner state for the Notes Automation permission denial path. Set by
    /// `NotesDestination` when an AppleScript send is rejected by TCC.
    @ObservedObject private var notesPermissionState = NotesAutomationPermissionState.shared

    /// Tag value reserved for the synthetic "Current mode" picker option.
    private static let currentModeTag = ""

    var body: some View {
        SettingsSection(title: "settings.section.shortcuts") {
            shortcutCard
            pushToTalkCard
            quickCaptureCard
            resetRow
        }
    }

    private var shortcutCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                SettingsShortcutRowCustom(
                    systemImage: "mic.circle",
                    title: "settings.shortcuts.toggleRecording.title",
                    subtitle: "settings.shortcuts.toggleRecording.subtitle",
                    standalone: false
                ) {
                    KeyboardShortcuts.Recorder(for: .toggleRecordingWithTranscription) { _ in
                        NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
                        AppLogger.ui.debug("⌨️ Toggle Recording shortcut updated via settings")
                    }
                }

                Divider()

                SettingsShortcutRowCustom(
                    systemImage: "xmark.circle",
                    title: "settings.shortcuts.cancelRecording.title",
                    subtitle: "settings.shortcuts.cancelRecording.subtitle",
                    standalone: false
                ) {
                    NormalizedShortcutRecorder(name: .cancelRecording)
                }

                Divider()

                SettingsShortcutRowCustom(
                    systemImage: "command",
                    title: "settings.shortcuts.changeMode.title",
                    subtitle: "settings.shortcuts.changeMode.subtitle",
                    standalone: false
                ) {
                    KeyboardShortcuts.Recorder(for: .changeMode)
                }
            }
        }
    }

    // MARK: - Push to Talk Card

    /// Separate card for Push to Talk settings
    /// PUSH TO TALK FEATURE - FLEXIBLE MODE SYSTEM
    /// Hybrid approach supporting:
    /// - Disabled: No Push to Talk
    /// - FN/Control/Command: Bare modifier keys (requires Accessibility permission)
    /// - Custom: Use KeyboardShortcuts recorder for any key combo
    private var pushToTalkCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                // Main enable/disable toggle
                SettingsToggleRow(
                    title: "settings.shortcuts.pushToTalk.enable.title",
                    subtitle: "settings.shortcuts.pushToTalk.enable.subtitle",
                    info: nil,
                    isOn: Binding(
                        get: { settingsManager.pushToTalkMode != .disabled },
                        set: { isEnabled in
                            if isEnabled {
                                // Enable with default mode (FN key)
                                settingsManager.pushToTalkMode = .fn
                            } else {
                                // Disable Push to Talk
                                settingsManager.pushToTalkMode = .disabled
                            }
                            // Notify app to update monitors
                            NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
                        }
                    ),
                    standalone: false
                )

                // Show configuration options when enabled
                if settingsManager.pushToTalkMode != .disabled {
                    Divider()

                    // Mode selection row
                    HStack(spacing: 12) {
                        Image(systemName: "keyboard")
                            .font(.title2)
                            .foregroundColor(.accentColor)
                            .frame(width: 30)

                        VStack(alignment: .leading, spacing: 2) {
                            Text("settings.shortcuts.pushToTalk.mode.title")
                                .font(.headline)
                            Text("settings.shortcuts.pushToTalk.mode.subtitle")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }

                        Spacer(minLength: 12)

                        Picker(selection: Binding(
                            get: { settingsManager.pushToTalkMode },
                            set: { newValue in
                                settingsManager.pushToTalkMode = newValue
                                NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
                            }
                        )) {
                            ForEach(PushToTalkMode.allCases.filter { $0 != .disabled }, id: \.self) { mode in
                                Text(mode.localizedName).tag(mode)
                            }
                        } label: {
                            EmptyView()
                        }
                        .labelsHidden()
                        .pickerStyle(.menu)
                        .frame(width: 180)
                    }
                    .padding(10)

                    // Double press to lock toggle - only show for single bare modifier modes
                    // Custom shortcuts use their own hold/release behavior via KeyboardShortcuts
                    // Combo modes (FN+Control, FN+Option) don't support double-press-to-lock
                    if settingsManager.pushToTalkMode != .custom
                        && settingsManager.pushToTalkMode != .fnControl
                        && settingsManager.pushToTalkMode != .fnOption {
                        Divider()

                        SettingsToggleRow(
                            title: "settings.shortcuts.pushToTalk.doublePress.title",
                            subtitle: "settings.shortcuts.pushToTalk.doublePress.subtitle",
                            info: nil,
                            isOn: $settingsManager.pushToTalkDoublePressEnabled,
                            standalone: false
                        )
                        // CRITICAL: Post notification when double-press setting changes
                        // Without this, BareModifierKeyMonitor.doublePressEnabled won't update until app restart
                        // because AudioRecordingManager.setupPushToTalk() only runs on .shortcutDidChange notifications
                        .onChange(of: settingsManager.pushToTalkDoublePressEnabled) { _, _ in
                            NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
                        }
                    }

                    // Show custom shortcut recorder for custom mode
                    if settingsManager.pushToTalkMode == .custom {
                        Divider()

                        SettingsShortcutRowCustom(
                            systemImage: "record.circle",
                            title: "settings.shortcuts.pushToTalk.custom.title",
                            subtitle: "settings.shortcuts.pushToTalk.custom.subtitle",
                            standalone: false
                        ) {
                            KeyboardShortcuts.Recorder(for: .pushToTalk) { _ in
                                NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
                                AppLogger.ui.debug("⌨️ Push to Talk custom shortcut updated via settings")
                            }
                        }
                    }
                }
            }
        }
    }

    // MARK: - Quick Capture Card

    /// Settings card for the Quick Capture shortcut, which records and sends
    /// the transcription to Apple Notes as a brand-new note (instead of
    /// pasting into the focused app). Sits directly below Push to Talk because
    /// it shares the same "shortcut → text out" mental model.
    private var quickCaptureCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                SettingsToggleRow(
                    title: "settings.shortcuts.quickCapture.enable.title",
                    subtitle: "settings.shortcuts.quickCapture.enable.subtitle",
                    info: nil,
                    isOn: $settingsManager.quickCaptureEnabled,
                    standalone: false
                )
                .onChange(of: settingsManager.quickCaptureEnabled) { _, _ in
                    // Mirror the PTT pattern: tell the app to rebind/refresh
                    // shortcut handlers when the feature is toggled.
                    NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
                }

                if settingsManager.quickCaptureEnabled {
                    Divider()

                    SettingsShortcutRowCustom(
                        systemImage: "note.text.badge.plus",
                        title: "settings.shortcuts.quickCapture.shortcut.title",
                        subtitle: "settings.shortcuts.quickCapture.shortcut.subtitle",
                        standalone: false
                    ) {
                        KeyboardShortcuts.Recorder(for: .quickCapture) { _ in
                            NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
                            AppLogger.ui.debug("⌨️ Quick Capture shortcut updated via settings")
                        }
                    }

                    Divider()

                    quickCaptureModeRow

                    if notesPermissionState.needsAutomationPermission {
                        Divider()
                        quickCapturePermissionBanner
                    }
                }
            }
        }
    }

    /// Mode picker row for Quick Capture. Reuses `SettingsShortcutRowCustom`
    /// so the icon / title / subtitle / trailing-control layout stays in lock
    /// step with the other shortcut rows. The "Current mode" option is a
    /// synthetic tag (`""`) that resolves to AppState.selectedMode at fire time.
    private var quickCaptureModeRow: some View {
        SettingsShortcutRowCustom(
            systemImage: "rectangle.stack.badge.person.crop",
            title: "settings.shortcuts.quickCapture.mode.title",
            subtitle: "settings.shortcuts.quickCapture.mode.subtitle",
            standalone: false
        ) {
            Picker(selection: $settingsManager.quickCaptureModeId) {
                Text("settings.shortcuts.quickCapture.mode.current")
                    .tag(Self.currentModeTag)
                ForEach(modes, id: \.objectID) { mode in
                    Text(mode.name ?? "—")
                        .tag(mode.id?.uuidString ?? "")
                }
            } label: {
                EmptyView()
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .fixedSize()
        }
    }

    /// Banner shown when Apple Notes automation permission is denied. We
    /// can't open the Automation pane directly (System Settings has no
    /// stable URL anchor for it), so we point the user at the right path
    /// and let them flip the toggle manually.
    private var quickCapturePermissionBanner: some View {
        HStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.title3)
                .foregroundColor(.orange)
                .frame(width: 30)

            Text("settings.shortcuts.quickCapture.permission.banner")
                .font(.callout)
                .foregroundColor(.primary)
                .fixedSize(horizontal: false, vertical: true)

            Spacer(minLength: 12)
        }
        .padding(10)
        .background(Color.orange.opacity(0.08))
    }

    private var resetRow: some View {
        HStack {
            Button {
                showResetShortcutsConfirmation = true
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: "arrow.counterclockwise")
                        .font(.system(size: 12))
                Text("settings.shortcuts.reset.button".localized)
                        .font(.system(size: 13))
                }
                .foregroundColor(.secondary)
                .padding(.horizontal, 12)
                .padding(.vertical, 6)
                .background(Color.gray.opacity(0.1))
                .cornerRadius(6)
            }
            .buttonStyle(.plain)
            .confirmationDialog(
                "settings.shortcuts.reset.dialog.title",
                isPresented: $showResetShortcutsConfirmation,
                titleVisibility: .visible
            ) {
                Button(LocalizedStringKey("settings.shortcuts.reset.confirm"), role: .destructive) {
                    resetKeyboardShortcuts()
                }
                Button(LocalizedStringKey("common.cancel"), role: .cancel) { }
            } message: {
                Text("settings.shortcuts.reset.dialog.message".localized)
            }

            Spacer()
        }
        .padding(.top, 8)
    }

    private func resetKeyboardShortcuts() {
        KeyboardShortcuts.reset(
            .toggleRecordingWithTranscription,
            .cancelRecording,
            .pushToTalk,
            .startStreaming,
            .changeMode,
            .quickCapture
        )

        AppLogger.ui.info("🔄 Keyboard shortcuts reset to defaults")
        NotificationCenter.default.post(name: .shortcutDidChange, object: nil)
    }
}

private struct SettingsShortcutRowCustom<Recorder: View>: View {
    let systemImage: String
    let title: LocalizedStringKey
    let subtitle: LocalizedStringKey
    var standalone: Bool = true
    @ViewBuilder let recorder: () -> Recorder

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: systemImage)
                .font(.title2)
                .foregroundColor(.accentColor)
                .frame(width: 30)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(subtitle).font(.caption).foregroundColor(.secondary)
            }
            Spacer(minLength: 12)
            recorder()
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .fill(.thinMaterial)
                } else {
                    Color.clear
                }
            }
        )
        .overlay(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                } else {
                    EmptyView()
                }
            }
        )
    }
}
