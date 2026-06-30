//
//  DesignConstants.swift
//  hyperwhisper
//
//  DESIGN CONSTANTS
//  Centralized design system for consistent UI across the application.
//  All views should use these constants for spacing, fonts, and styling.
//
//  USAGE:
//  - Use DesignConstants.Spacing for all padding and spacing values
//  - Use DesignConstants.Fonts for consistent typography
//  - Use DesignConstants.Colors for app-wide color palette
//  - Apply ViewModifiers for common styling patterns

import SwiftUI

// MARK: - Design Constants

/// Centralized design constants for consistent UI
enum DesignConstants {
    
    // MARK: - Spacing
    
    /// Standard spacing values used throughout the app
    enum Spacing {
        /// Extra small spacing (4pt) - used for minimal gaps
        static let xs: CGFloat = 4
        
        /// Small spacing (8pt) - used for compact layouts
        static let small: CGFloat = 8
        
        /// Medium spacing (12pt) - used for standard gaps between elements
        static let medium: CGFloat = 12
        
        /// Large spacing (20pt) - used for major section breaks
        static let large: CGFloat = 20
        
        /// Extra large spacing (24pt) - used for main content padding
        static let xl: CGFloat = 24
        
        /// Row padding (10pt) - standard internal padding for settings rows
        static let rowPadding: CGFloat = 10
        
        /// Section spacing (20pt) - standard gap between major sections
        static let sectionSpacing: CGFloat = 20
        
        /// Header padding (horizontal: 24pt, vertical: 20pt)
        static let headerPaddingH: CGFloat = 24
        static let headerPaddingV: CGFloat = 20
    }
    
    // MARK: - Corner Radius
    
    /// Standard corner radius values
    enum CornerRadius {
        /// Small radius (6pt) - for buttons and small elements
        static let small: CGFloat = 6
        
        /// Medium radius (10pt) - for cards and containers
        static let medium: CGFloat = 10
        
        /// Large radius (16pt) - for prominent cards
        static let large: CGFloat = 16
    }
    
    // MARK: - Typography
    
    /// Standard font configurations
    /// Note: Use SwiftUI's semantic fonts (.title, .headline, etc.) where possible
    enum Typography {
        /// Page title font
        static let pageTitle = Font.largeTitle.weight(.bold)
        
        /// Section title font  
        static let sectionTitle = Font.title.weight(.semibold)
        
        /// Row title font
        static let rowTitle = Font.headline
        
        /// Subtitle/description font
        static let subtitle = Font.body
        
        /// Caption font for secondary text
        static let caption = Font.caption
        
        /// Small system font for compact UI
        static let small = Font.system(size: 12)
        
        /// Button font
        static let button = Font.system(size: 14, weight: .medium)
    }
    
    // MARK: - Colors
    
    /// Standard color palette
    enum Colors {
        /// Gradient for page headers
        static let headerGradient = LinearGradient(
            colors: [Color.accentColor.opacity(0.03), Color.clear],
            startPoint: .top,
            endPoint: .bottom
        )
        
        /// Text gradient for titles
        static let titleGradient = LinearGradient(
            colors: [.primary, .primary.opacity(0.8)],
            startPoint: .leading,
            endPoint: .trailing
        )
        
        /// Standard background opacity for overlays
        static let overlayBackgroundOpacity = 0.05
        static let activeBackgroundOpacity = 0.1
    }
}

// MARK: - View Modifiers

/// Standard header style used across views
struct StandardHeaderStyle: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(.horizontal, DesignConstants.Spacing.headerPaddingH)
            .padding(.vertical, DesignConstants.Spacing.headerPaddingV)
            .background(DesignConstants.Colors.headerGradient)
    }
}

/// Standard settings row container style
struct SettingsRowContainerStyle: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(DesignConstants.Spacing.rowPadding)
            .background(RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                .fill(.thinMaterial))
            .overlay(RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                .stroke(Color(nsColor: .separatorColor), lineWidth: 1))
    }
}

/// Standard card style for content containers
struct CardStyle: ViewModifier {
    var padding: CGFloat = DesignConstants.Spacing.rowPadding
    
    func body(content: Content) -> some View {
        content
            .padding(padding)
            .background(RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                .fill(.thinMaterial))
            .overlay(RoundedRectangle(cornerRadius: DesignConstants.CornerRadius.medium, style: .continuous)
                .stroke(Color(nsColor: .separatorColor), lineWidth: 1))
    }
}

// MARK: - View Extensions

extension View {
    /// Apply standard header styling
    func standardHeaderStyle() -> some View {
        modifier(StandardHeaderStyle())
    }
    
    /// Apply standard settings row container styling
    func settingsRowContainerStyle() -> some View {
        modifier(SettingsRowContainerStyle())
    }
    
    /// Apply standard card styling
    func cardStyle(padding: CGFloat = DesignConstants.Spacing.rowPadding) -> some View {
        modifier(CardStyle(padding: padding))
    }
}

// MARK: - Shared Page Header Component

/// Reusable page header with consistent styling
struct PageHeader<TrailingContent: View>: View {
    let title: String
    let subtitle: String
    var actionLabel: String? = nil
    var actionIcon: String? = nil
    var action: (() -> Void)? = nil
    var helpURL: String? = nil
    var trailingContent: (() -> TrailingContent)?

    /// Initialize with optional action button
    init(
        title: String,
        subtitle: String,
        actionLabel: String? = nil,
        actionIcon: String? = nil,
        action: (() -> Void)? = nil,
        helpURL: String? = nil
    ) where TrailingContent == EmptyView {
        self.title = title
        self.subtitle = subtitle
        self.actionLabel = actionLabel
        self.actionIcon = actionIcon
        self.action = action
        self.helpURL = helpURL
        self.trailingContent = nil
    }

    /// Initialize with custom trailing content
    init(
        title: String,
        subtitle: String,
        helpURL: String? = nil,
        @ViewBuilder trailingContent: @escaping () -> TrailingContent
    ) {
        self.title = title
        self.subtitle = subtitle
        self.actionLabel = nil
        self.actionIcon = nil
        self.action = nil
        self.helpURL = helpURL
        self.trailingContent = trailingContent
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 6) {
                    Text(title)
                        .font(DesignConstants.Typography.pageTitle)
                        .foregroundStyle(DesignConstants.Colors.titleGradient)

                    if let helpURL = helpURL {
                        (Text(subtitle + " ")
                            .font(DesignConstants.Typography.subtitle)
                            .foregroundColor(.secondary)
                        + Text(localized: "page.header.learnMore")
                            .font(DesignConstants.Typography.subtitle)
                            .foregroundColor(.blue)
                            .underline())
                        .onTapGesture {
                            if let url = URL(string: helpURL) {
                                NSWorkspace.shared.open(url)
                            }
                        }
                    } else {
                        Text(subtitle)
                            .font(DesignConstants.Typography.subtitle)
                            .foregroundColor(.secondary)
                    }
                }

                Spacer()

                // Custom trailing content takes precedence
                if let trailingContent = trailingContent {
                    trailingContent()
                } else if let actionLabel = actionLabel, let action = action {
                    Button(action: action) {
                        if let icon = actionIcon {
                            Label(actionLabel, systemImage: icon)
                        } else {
                            Text(actionLabel)
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .font(DesignConstants.Typography.button)
                }
            }
        }
        .standardHeaderStyle()
    }
}
