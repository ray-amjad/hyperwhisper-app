//
//  CustomProgressView.swift
//  hyperwhisper
//
//  Custom progress view to replace ProgressView and avoid layout warnings
//

import SwiftUI

/// Custom progress view with smooth animations and clean design
/// Replaces the default ProgressView to avoid SwiftUI layout warnings
struct CustomProgressView: View {
    // MARK: - Properties
    
    /// Progress value from 0.0 to 1.0
    let progress: Double
    
    /// Whether to show percentage text
    let showPercentage: Bool
    
    /// Optional label text
    let label: String?
    
    /// Track color
    var trackColor: Color = Color(.separatorColor).opacity(0.3)
    
    /// Progress bar color
    var progressColor: Color = Color(.controlAccentColor)
    
    /// Bar height
    var height: CGFloat = 6
    
    // MARK: - Initializers
    
    init(progress: Double, showPercentage: Bool = false, label: String? = nil) {
        self.progress = min(max(progress, 0), 1) // Clamp to 0...1
        self.showPercentage = showPercentage
        self.label = label
    }
    
    // MARK: - Body
    
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            // Optional label
            if let label = label {
                Text(label)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(Color(.secondaryLabelColor))
            }
            
            // Progress bar
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    // Background track
                    RoundedRectangle(cornerRadius: height / 2)
                        .fill(trackColor)
                        .frame(height: height)
                    
                    // Progress fill
                    RoundedRectangle(cornerRadius: height / 2)
                        .fill(progressColor)
                        .frame(
                            width: max(0, min(geometry.size.width * progress, geometry.size.width)),
                            height: height
                        )
                }
            }
            .frame(height: height)
            
            // Percentage indicator
            if showPercentage {
                HStack {
                    Spacer()
                    Text("\(Int(progress * 100))%")
                        .font(.system(size: 11, weight: .medium, design: .monospaced))
                        .foregroundColor(Color(.secondaryLabelColor))
                }
            }
        }
        // Smooth animation on progress changes
        .animation(.smooth, value: progress)
    }
}

/// Circular progress view variant for compact displays
struct CircularProgressView: View {
    let progress: Double
    let size: CGFloat
    let lineWidth: CGFloat
    
    init(progress: Double, size: CGFloat = 20, lineWidth: CGFloat = 2) {
        self.progress = min(max(progress, 0), 1)
        self.size = size
        self.lineWidth = lineWidth
    }
    
    var body: some View {
        ZStack {
            // Background circle
            Circle()
                .stroke(
                    Color(.separatorColor).opacity(0.3),
                    lineWidth: lineWidth
                )
            
            // Progress arc
            Circle()
                .trim(from: 0, to: progress)
                .stroke(
                    Color(.controlAccentColor),
                    style: StrokeStyle(
                        lineWidth: lineWidth,
                        lineCap: .round
                    )
                )
                .rotationEffect(Angle(degrees: -90))
        }
        .frame(width: size, height: size)
        .animation(.smooth, value: progress)
    }
}

/// Indeterminate progress view for operations without known progress
struct IndeterminateProgressView: View {
    @State private var isAnimating = false
    
    let height: CGFloat
    let color: Color
    
    init(height: CGFloat = 6, color: Color = Color(.controlAccentColor)) {
        self.height = height
        self.color = color
    }
    
    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                // Background track
                RoundedRectangle(cornerRadius: height / 2)
                    .fill(Color(.separatorColor).opacity(0.3))
                    .frame(height: height)
                
                // Moving indicator
                RoundedRectangle(cornerRadius: height / 2)
                    .fill(color)
                    .frame(width: geometry.size.width * 0.3, height: height)
                    .offset(x: isAnimating ? geometry.size.width * 0.7 : 0)
            }
        }
        .frame(height: height)
        .onAppear {
            withAnimation(
                .linear(duration: 1.5)
                .repeatForever(autoreverses: true)
            ) {
                isAnimating = true
            }
        }
    }
}

// MARK: - Preview

struct CustomProgressView_Previews: PreviewProvider {
    static var previews: some View {
        VStack(spacing: 20) {
            // Basic progress
            CustomProgressView(progress: 0.3)
                .padding()
            
            // With percentage
            CustomProgressView(progress: 0.6, showPercentage: true)
                .padding()
            
            // With label and percentage
            CustomProgressView(
                progress: 0.8,
                showPercentage: true,
                label: "Downloading model..."
            )
            .padding()
            
            // Circular variant
            HStack(spacing: 20) {
                CircularProgressView(progress: 0.25)
                CircularProgressView(progress: 0.5)
                CircularProgressView(progress: 0.75)
                CircularProgressView(progress: 1.0)
            }
            .padding()
            
            // Indeterminate
            IndeterminateProgressView()
                .padding()
        }
        .frame(width: 300)
        .padding()
    }
}