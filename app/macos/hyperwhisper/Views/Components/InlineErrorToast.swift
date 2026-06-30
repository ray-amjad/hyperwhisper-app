//
//  InlineErrorToast.swift
//  hyperwhisper
//
//  INLINE ERROR TOAST
//  A compact, auto-dismissing error pill that appears above the recording dialog.
//  Shows error message with countdown timer and optional settings button.
//
//  DESIGN SPECIFICATIONS:
//  - Shape: Rounded pill (same as RecordingDialog - cornerRadius: height/2)
//  - Size: ~280x40px (slightly wider than recording dialog's 200x40)
//  - Background: Same as recording dialog (Color.black.opacity(0.85))
//  - Border: Same subtle white border (Color.white.opacity(0.1))
//
//  LAYOUT: [⚠️ Error icon] [Error message...] [8] [Open Settings]
//  - Warning icon (yellow/orange)
//  - Truncated error message (single line)
//  - Countdown number (8→7→6→5→4→3→2→1)
//  - "Open Settings" button (optional, only for actionable errors)
//
//  BEHAVIOR:
//  1. Appears with slide-down + fade animation
//  2. Countdown from 8 seconds
//  3. Dismisses with slide-up + fade animation
//  4. Recording dialog stays open (does NOT close)
//

import SwiftUI

// MARK: - Inline Error Toast View

/// A compact error pill view that displays above the recording dialog
///
/// **What This Does:**
/// - Shows error message in a small, non-intrusive pill
/// - Auto-dismisses after countdown (default 8 seconds)
/// - Optionally shows "Open Settings" button for actionable errors
///
/// **Design Philosophy:**
/// Unlike the large modal ErrorToastManager (400x220px), this is designed to be
/// unobtrusive and temporary. The user sees the error but can continue working
/// immediately without clicking any buttons.
struct InlineErrorToast: View {
    // MARK: - Properties

    /// The error message to display (will be truncated if too long)
    let message: String

    /// Whether to show the "Open Settings" button
    /// Show for: API key errors, credit errors, authorization errors
    /// Hide for: network errors, rate limits, no speech detected
    let showSettingsButton: Bool

    /// Called when the toast should dismiss (countdown complete or user action)
    let onDismiss: () -> Void

    /// Called when user taps "Open Settings"
    let onOpenSettings: () -> Void

    // MARK: - State

    /// Countdown seconds remaining (starts at 8)
    /// 8 seconds gives users enough time to read the error message
    @State private var countdown: Int = 8

    /// Timer for countdown
    @State private var timer: Timer?

    /// Animation state for appearing/disappearing
    @State private var isVisible: Bool = false

    // MARK: - Size Configuration

    /// Toast dimensions - wider than recording dialog for better readability
    private let toastWidth: CGFloat = 360
    private let toastHeight: CGFloat = 40

    // MARK: - Body

    var body: some View {
        HStack(spacing: 8) {
            // Warning icon (yellow/orange)
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 12, weight: .semibold))
                .foregroundColor(.orange)

            // Error message (centered, single line)
            Text(message)
                .font(.system(size: 11, weight: .medium))
                .foregroundColor(.white.opacity(0.9))
                .lineLimit(1)
                .truncationMode(.tail)
                .frame(maxWidth: .infinity)

            // Countdown number
            Text("\(countdown)")
                .font(.system(size: 10, weight: .semibold, design: .monospaced))
                .foregroundColor(.white.opacity(0.6))
                .frame(width: 14)

            // Open Settings button (optional)
            if showSettingsButton {
                Button {
                    stopTimer()
                    onOpenSettings()
                } label: {
                    Text("common.open.settings".localized)
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundColor(.white)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(
                            Capsule()
                                .fill(Color.white.opacity(0.15))
                        )
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(width: toastWidth, height: toastHeight)
        .background(
            // BACKGROUND: Same style as RecordingDialog
            RoundedRectangle(cornerRadius: toastHeight / 2)
                .fill(Color.black.opacity(0.85))
        )
        .overlay(
            // BORDER: Same subtle white border as RecordingDialog
            RoundedRectangle(cornerRadius: toastHeight / 2)
                .stroke(Color.white.opacity(0.1), lineWidth: 1)
        )
        .shadow(color: .black.opacity(0.3), radius: 10, y: 4)
        // ANIMATION: Slide down from above + fade in
        .offset(y: isVisible ? 0 : -20)
        .opacity(isVisible ? 1.0 : 0.0)
        .onAppear {
            // Start appear animation
            withAnimation(.spring(response: 0.3, dampingFraction: 0.7)) {
                isVisible = true
            }
            // Start countdown timer
            startTimer()
        }
        .onDisappear {
            stopTimer()
        }
    }

    // MARK: - Timer Methods

    /// Start the countdown timer
    private func startTimer() {
        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { _ in
            if countdown > 1 {
                countdown -= 1
            } else {
                // Countdown complete - dismiss with animation
                dismissWithAnimation()
            }
        }
    }

    /// Stop the countdown timer
    private func stopTimer() {
        timer?.invalidate()
        timer = nil
    }

    /// Dismiss the toast with slide-up + fade animation
    private func dismissWithAnimation() {
        stopTimer()
        // ANIMATION: Slide up + fade out
        withAnimation(.easeOut(duration: 0.2)) {
            isVisible = false
        }
        // Call dismiss after animation completes
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
            onDismiss()
        }
    }
}

// MARK: - Preview

#Preview {
    VStack(spacing: 20) {
        // Preview: No Settings button (no speech detected)
        InlineErrorToast(
            message: "No speech detected",
            showSettingsButton: false,
            onDismiss: {},
            onOpenSettings: {}
        )

        // Preview: With Settings button (API key error)
        InlineErrorToast(
            message: "API key is required for OpenAI",
            showSettingsButton: true,
            onDismiss: {},
            onOpenSettings: {}
        )

        // Preview: Long message (should truncate)
        InlineErrorToast(
            message: "This is a very long error message that should be truncated because it won't fit",
            showSettingsButton: true,
            onDismiss: {},
            onOpenSettings: {}
        )
    }
    .padding()
    .background(Color.gray.opacity(0.3))
}
