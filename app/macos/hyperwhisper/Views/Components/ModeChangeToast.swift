//
//  ModeChangeToast.swift
//  hyperwhisper
//
//  MODE CHANGE TOAST VIEW
//  A compact, auto-dismissing pill notification that appears when the user
//  changes modes via the keyboard shortcut (Control+Shift+K).
//
//  DESIGN SPECIFICATIONS:
//  - Shape: Rounded pill (cornerRadius: height/2)
//  - Size: ~200x36px (compact pill)
//  - Background: Color.black.opacity(0.85)
//  - Border: Color.white.opacity(0.1)
//
//  LAYOUT: [✨ sparkles icon] Mode: {name}
//  - SF Symbol "sparkles" icon
//  - Mode label with the mode name
//
//  ANIMATION:
//  - Slide-down + fade-in on appear (0.2s spring)
//  - Toast manager handles dismiss timing (2 seconds)
//

import SwiftUI

// MARK: - Mode Change Toast View

/// A compact pill notification showing the newly selected mode
///
/// **What This Does:**
/// - Shows "Mode: {name}" in a small, non-intrusive pill
/// - Appears with slide-down animation
/// - Manager handles auto-dismiss after 2 seconds
///
/// **Design Philosophy:**
/// This is designed to be quick and unobtrusive - just a brief confirmation
/// that the mode change was registered. No interaction required.
struct ModeChangeToast: View {
    // MARK: - Properties

    /// The name of the newly selected mode
    let modeName: String

    // MARK: - State

    /// Animation state for appearing
    @State private var isVisible: Bool = false

    // MARK: - Size Configuration

    /// Toast dimensions - compact pill
    private let toastWidth: CGFloat = 200
    private let toastHeight: CGFloat = 36

    // MARK: - Body

    var body: some View {
        HStack(spacing: 8) {
            // Sparkles icon (indicates mode change/magic)
            Image(systemName: "sparkles")
                .font(.system(size: 12, weight: .semibold))
                .foregroundColor(.white.opacity(0.9))

            // Mode label
            Text("Mode: \(modeName)")
                .font(.system(size: 12, weight: .medium))
                .foregroundColor(.white.opacity(0.9))
                .lineLimit(1)
                .truncationMode(.tail)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .frame(width: toastWidth, height: toastHeight)
        .background(
            // BACKGROUND: Same style as other toasts in the app
            RoundedRectangle(cornerRadius: toastHeight / 2)
                .fill(Color.black.opacity(0.85))
        )
        .overlay(
            // BORDER: Subtle white border for definition
            RoundedRectangle(cornerRadius: toastHeight / 2)
                .stroke(Color.white.opacity(0.1), lineWidth: 1)
        )
        .shadow(color: .black.opacity(0.3), radius: 10, y: 4)
        // ANIMATION: Slide down from above + fade in
        .offset(y: isVisible ? 0 : -20)
        .opacity(isVisible ? 1.0 : 0.0)
        .onAppear {
            // Start appear animation
            withAnimation(.spring(response: 0.15, dampingFraction: 0.85)) {
                isVisible = true
            }
        }
    }
}

// MARK: - Preview

#Preview {
    VStack(spacing: 20) {
        // Preview: Default mode
        ModeChangeToast(modeName: "Default")

        // Preview: Custom mode name
        ModeChangeToast(modeName: "Meeting Notes")

        // Preview: Long mode name (should truncate)
        ModeChangeToast(modeName: "Very Long Mode Name That Should Truncate")
    }
    .padding()
    .background(Color.gray.opacity(0.3))
}
