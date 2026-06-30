//
//  SettingsComponents.swift
//  hyperwhisper
//
//  SHARED SETTINGS COMPONENTS
//  Reusable UI components for settings views to ensure consistency.
//  All settings-related views should use these components.
//
//  COMPONENTS:
//  - SettingsToggleRow: Toggle switch with title, subtitle, and info
//  - SettingsActionRow: Action button with title and subtitle
//  - SettingsShortcutRow: Keyboard shortcut recorder row
//  - InfoTooltipButton: Information button with popover

import SwiftUI
import KeyboardShortcuts

// MARK: - Settings Toggle Row

/// A standardized toggle row for settings
/// Used throughout settings views for boolean preferences
struct SettingsToggleRow: View {
    let title: LocalizedStringKey
    var subtitle: LocalizedStringKey?
    var info: LocalizedStringKey?
    @Binding var isOn: Bool
    var standalone: Bool = true
    var titleFont: Font = .headline
    var subtitleFont: Font = .caption
    
    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(titleFont)
                if let subtitle {
                    Text(subtitle)
                        .font(subtitleFont)
                        .foregroundColor(.secondary)
                }
            }
            
            Spacer(minLength: 12)
            Toggle("", isOn: $isOn)
                .toggleStyle(.switch)
                .tint(.blue)
                .labelsHidden()
            
            if let info {
                InfoTooltipButton(text: info)
            }
        }
        .padding(DesignConstants.Spacing.rowPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .fill(.thinMaterial)
                } else {
                    Color.clear
                }
            }
        )
        .overlay(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                } else {
                    EmptyView()
                }
            }
        )
    }
}

// MARK: - Settings Picker Row

/// A standardized picker row for settings
/// Used throughout settings views for enum/dropdown preferences
/// Matches the style of SettingsToggleRow but with a dropdown menu
struct SettingsPickerRow<T: Hashable>: View {
    let title: LocalizedStringKey
    var subtitle: LocalizedStringKey?
    var info: LocalizedStringKey?
    @Binding var selection: T
    let options: [T]
    let optionLabel: (T) -> String
    var standalone: Bool = true
    var pickerWidth: CGFloat = 140

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.headline)
                if let subtitle {
                    Text(subtitle)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            Spacer(minLength: 12)

            Picker("", selection: $selection) {
                ForEach(options, id: \.self) { option in
                    Text(optionLabel(option)).tag(option)
                }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .frame(width: pickerWidth)

            if let info {
                InfoTooltipButton(text: info)
            }
        }
        .padding(DesignConstants.Spacing.rowPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .fill(.thinMaterial)
                } else {
                    Color.clear
                }
            }
        )
        .overlay(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                } else {
                    EmptyView()
                }
            }
        )
    }
}

// MARK: - Settings Action Row

/// A standardized action row for settings
/// Used for rows that trigger an action when clicked
struct SettingsActionRow: View {
    let title: LocalizedStringKey
    var subtitle: LocalizedStringKey?
    let buttonTitle: LocalizedStringKey
    var standalone: Bool = true
    let action: () -> Void
    
    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.headline)
                if let subtitle {
                    Text(subtitle)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
            
            Spacer(minLength: 12)
            
            Button(buttonTitle, action: action)
                .buttonStyle(.borderedProminent)
                .controlSize(.regular)
        }
        .padding(DesignConstants.Spacing.rowPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .fill(.thinMaterial)
                } else {
                    Color.clear
                }
            }
        )
        .overlay(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                } else {
                    EmptyView()
                }
            }
        )
    }
}

// MARK: - Settings Shortcut Row

/// A standardized keyboard shortcut row for settings
/// Used for configuring global keyboard shortcuts
struct SettingsShortcutRow: View {
    let systemImage: String
    let title: LocalizedStringKey
    var subtitle: LocalizedStringKey?
    var standalone: Bool = true
    let recorder: KeyboardShortcuts.Name
    
    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: systemImage)
                .font(.title2)
                .foregroundColor(.blue)
                .frame(width: 28)
            
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.headline)
                if let subtitle {
                    Text(subtitle)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
            
            Spacer(minLength: 12)
            
            KeyboardShortcuts.Recorder(for: recorder)
                .environment(\.controlSize, .regular)
        }
        .padding(DesignConstants.Spacing.rowPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .fill(.thinMaterial)
                } else {
                    Color.clear
                }
            }
        )
        .overlay(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                } else {
                    EmptyView()
                }
            }
        )
    }
}

// MARK: - Info Tooltip Button

/// A small info button that shows a popover with help text
/// The popover supports multi-line text and will wrap at 300pt width
struct InfoTooltipButton: View {
    let text: LocalizedStringKey
    @State private var showingInfo = false

    var body: some View {
        Button(action: { showingInfo.toggle() }) {
            Image(systemName: "info.circle")
                .foregroundColor(.secondary)
                .font(.caption)
        }
        .buttonStyle(.plain)
        .popover(isPresented: $showingInfo, arrowEdge: .trailing) {
            VStack(alignment: .leading, spacing: 8) {
                Text(text)
                    .font(.caption)
                    .foregroundColor(.primary)
                    .lineSpacing(2)
                    .multilineTextAlignment(.leading)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(12)
            .frame(width: 300, alignment: .leading)
        }
    }
}

// MARK: - Settings Section Container

/// A container for grouping related settings
struct SettingsSectionContainer<Content: View>: View {
    let content: Content
    
    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }
    
    var body: some View {
        VStack(spacing: 0) {
            content
        }
        .background(
            RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                .fill(.thinMaterial)
        )
        .overlay(
            RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
        )
    }
}

// MARK: - Settings Value Row

/// A row displaying a label and value (not editable)
struct SettingsValueRow: View {
    let title: String
    let value: String
    var info: LocalizedStringKey?
    var standalone: Bool = true
    
    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Text(title)
                .font(.headline)
            
            Spacer(minLength: 12)
            
            Text(value)
                .font(.body)
                .foregroundColor(.secondary)
            
            if let info = info {
                InfoTooltipButton(text: info)
            }
        }
        .padding(DesignConstants.Spacing.rowPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .fill(.thinMaterial)
                } else {
                    Color.clear
                }
            }
        )
        .overlay(
            Group {
                if standalone {
                    RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                        .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                } else {
                    EmptyView()
                }
            }
        )
    }
}
