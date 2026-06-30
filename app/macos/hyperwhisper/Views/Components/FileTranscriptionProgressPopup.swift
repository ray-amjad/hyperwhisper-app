//
//  FileTranscriptionProgressPopup.swift
//  hyperwhisper
//
//  FILE TRANSCRIPTION PROGRESS POPUP VIEW
//  SwiftUI view that displays file transcription progress in a floating modal.
//
//  VISUAL DESIGN:
//  - Matches RecordingDialog aesthetic (dark background, rounded corners)
//  - Compact size (~280x90px)
//  - Shows file name, progress bar, and cancel button
//
//  LAYOUT:
//  ┌──────────────────────────────────┐
//  │  📄 filename.mp3                 │
//  │  ████████░░░░░░░░░  45%          │
//  │              [Cancel]            │
//  └──────────────────────────────────┘
//

import SwiftUI

/// File transcription progress popup view
///
/// **Purpose:**
/// Displays the progress of file transcription in a floating modal popup.
/// Shows the file name, animated progress bar, and a cancel button.
///
/// **Visual Design:**
/// - Dark semi-transparent background matching RecordingDialog
/// - Rounded corners with subtle border
/// - Compact layout optimized for floating panel display
///
/// **Usage:**
/// This view is hosted in an NSPanel managed by FileTranscriptionPopupManager.
/// It observes a FileTranscriptionProgress instance for state updates.
struct FileTranscriptionProgressPopup: View {

    // MARK: - Properties

    /// Progress state to observe
    @ObservedObject var progress: FileTranscriptionProgress

    /// Callback when user clicks cancel
    let onCancel: () -> Void

    // MARK: - Size Configuration

    /// Popup width
    private let popupWidth: CGFloat = 280

    /// Popup height
    private let popupHeight: CGFloat = 90

    // MARK: - Body

    var body: some View {
        VStack(spacing: 12) {
            // HEADER: File name with icon
            fileNameHeader

            // PROGRESS BAR: Animated progress with percentage
            progressBar

            // CANCEL BUTTON: Subtle, centered
            cancelButton
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 16)
        .frame(width: popupWidth, height: popupHeight)
        .background(
            RoundedRectangle(cornerRadius: 12)
                .fill(Color.black.opacity(0.85))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .stroke(Color.white.opacity(0.1), lineWidth: 1)
        )
        .shadow(color: .black.opacity(0.3), radius: 20, y: 10)
    }

    // MARK: - Subviews

    /// File name header with document icon
    ///
    /// **Layout:**
    /// [📄 icon] [filename truncated to single line]
    private var fileNameHeader: some View {
        HStack(spacing: 6) {
            Image(systemName: "doc.fill")
                .font(.system(size: 12))
                .foregroundColor(.white.opacity(0.6))

            Text(progress.fileName)
                .font(.system(size: 12, weight: .medium))
                .foregroundColor(.white.opacity(0.9))
                .lineLimit(1)
                .truncationMode(.middle)

            Spacer()
        }
    }

    /// Progress bar with percentage
    ///
    /// **Layout:**
    /// [████████░░░░░░░░░] [45%]
    ///
    /// **Animation:**
    /// Progress bar animates smoothly with easeOutCubic easing.
    private var progressBar: some View {
        HStack(spacing: 8) {
            // Progress bar
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    // Background track
                    RoundedRectangle(cornerRadius: 3)
                        .fill(Color.white.opacity(0.15))
                        .frame(height: 6)

                    // Progress fill
                    RoundedRectangle(cornerRadius: 3)
                        .fill(progressGradient)
                        .frame(
                            width: max(0, min(geometry.size.width * CGFloat(progress.progress), geometry.size.width)),
                            height: 6
                        )
                }
            }
            .frame(height: 6)

            // Percentage text
            Text("\(Int(progress.progress * 100))%")
                .font(.system(size: 10, weight: .medium, design: .monospaced))
                .foregroundColor(.white.opacity(0.6))
                .frame(width: 32, alignment: .trailing)
        }
        .animation(.smooth(duration: 0.1), value: progress.progress)
    }

    /// Cancel button
    ///
    /// **Design:**
    /// Subtle capsule button that becomes more prominent on hover.
    private var cancelButton: some View {
        Button(action: onCancel) {
            Text("file.transcription.cancel".localized)
                .font(.system(size: 10, weight: .medium))
                .foregroundColor(.white.opacity(0.7))
                .padding(.horizontal, 12)
                .padding(.vertical, 4)
                .background(
                    Capsule()
                        .fill(Color.white.opacity(0.1))
                )
        }
        .buttonStyle(.plain)
        .contentShape(Capsule())
    }

    // MARK: - Computed Properties

    /// Gradient for progress bar fill
    ///
    /// **Design:**
    /// Uses a subtle gradient from accent color to a slightly brighter shade
    /// for visual depth.
    private var progressGradient: LinearGradient {
        LinearGradient(
            gradient: Gradient(colors: [
                Color(.controlAccentColor),
                Color(.controlAccentColor).opacity(0.8)
            ]),
            startPoint: .leading,
            endPoint: .trailing
        )
    }
}

// MARK: - Preview

#Preview {
    let progress = FileTranscriptionProgress()
    progress.fileName = "meeting_recording_2024.mp3"
    progress.modeName = "Default"
    progress.stage = .transcribing
    progress.progress = 0.45
    progress.isActive = true

    return FileTranscriptionProgressPopup(
        progress: progress,
        onCancel: { print("Cancelled") }
    )
    .padding()
    .background(Color.gray.opacity(0.3))
}
