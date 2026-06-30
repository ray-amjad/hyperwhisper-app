//
//  WaveformCAView.swift
//  hyperwhisper
//
//  CORE ANIMATION WAVEFORM
//  A high-performance waveform visualization using Core Animation instead of SwiftUI.
//
//  **Why Core Animation:**
//  SwiftUI's Canvas requires main thread work for every frame update. If the main thread
//  is busy (e.g., HistoryView scrolling), the waveform animation stutters.
//
//  Core Animation animations run on the render server (a separate process), so they
//  continue smoothly even when the main thread is blocked. Only amplitude changes
//  require main thread work, which is acceptable for 30 FPS updates.
//
//  **How It Works:**
//  1. Creates 25 CALayer bars with infinite keyframe animations on transform.scale.y
//  2. Each bar animates independently with slightly different timing for natural look
//  3. A container layer scales based on audio amplitude (more level = taller bars)
//  4. Animation speed increases with audio level for responsive feel
//
//  **Visual Design:**
//  Matches the existing CompactWaveformView style:
//  - 25 bars, 2px width, 2px spacing
//  - White color with 0.4 opacity
//  - Center-peaked envelope (bars in middle are taller)
//  - Smooth attack/release on amplitude changes
//

import AppKit
import SwiftUI

// MARK: - SwiftUI Wrapper

/// Core Animation-based waveform that continues animating even when main thread is busy.
/// Use this instead of CompactWaveformView for lag-free waveform rendering.
struct WaveformCARepresentable: NSViewRepresentable {
    /// Current audio level (0.0 to 1.0)
    var level: CGFloat

    func makeNSView(context: Context) -> WaveformNSView {
        WaveformNSView()
    }

    func updateNSView(_ nsView: WaveformNSView, context: Context) {
        nsView.setLevel(level)
    }
}

// MARK: - AppKit Implementation

/// NSView that renders waveform using Core Animation layers.
/// Animations run on render server, not main thread.
final class WaveformNSView: NSView {

    // MARK: - Configuration

    private let barCount = 25
    private let barWidth: CGFloat = 2
    private let barSpacing: CGFloat = 2
    private let barColor = NSColor.white.withAlphaComponent(0.4)

    // MARK: - Layer State

    private var containerLayer = CALayer()
    private var barLayers: [CALayer] = []

    // MARK: - Animation State

    /// Smoothed audio level for responsive but stable animation
    private var smoothedLevel: CGFloat = 0

    /// Precomputed center-peaked envelope (bars in middle are taller)
    private var envelope: [CGFloat] = []

    // MARK: - Initialization

    override init(frame: NSRect) {
        super.init(frame: frame)
        setupLayers()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setupLayers()
    }

    // MARK: - Layer Setup

    private func setupLayers() {
        wantsLayer = true
        layer?.masksToBounds = false

        // Container layer that will scale based on audio level
        containerLayer.masksToBounds = false
        layer?.addSublayer(containerLayer)

        // Precompute center-peaked envelope
        let center = CGFloat(barCount - 1) / 2
        envelope = (0..<barCount).map { i in
            let x = CGFloat(i)
            let dist = abs(x - center) / max(center, 1)
            let v = 1.0 - dist
            return v * v // Quadratic falloff
        }

        // Create bar layers with infinite wave animations
        for i in 0..<barCount {
            let bar = CALayer()
            bar.backgroundColor = barColor.cgColor

            // Anchor at center so scaling expands symmetrically (centered vertically)
            bar.anchorPoint = CGPoint(x: 0.5, y: 0.5)

            // Add infinite wave animation with varied timing for natural look
            let anim = CAKeyframeAnimation(keyPath: "transform.scale.y")

            // Wave pattern: oscillates between min and max heights
            // Each bar gets a slightly different pattern phase
            let phase = CGFloat(i) / CGFloat(barCount) * 2 * .pi
            let values: [CGFloat] = (0..<10).map { j in
                let t = CGFloat(j) / 10.0 * 2 * .pi + phase
                let wave = (sin(t) + 1.0) * 0.5 // 0..1
                let env = envelope[i]
                // Min height 0.15, max scales with envelope
                return 0.15 + env * 0.85 * (0.35 + 0.65 * wave)
            }
            anim.values = values

            // Varied duration for organic feel (1.5 - 2.0 seconds)
            // Slow enough to be calm, fast enough to be visible
            anim.duration = 1.5 + Double(i % 5) * 0.1

            anim.repeatCount = .infinity
            anim.calculationMode = .linear
            anim.isRemovedOnCompletion = false

            bar.add(anim, forKey: "wave")

            barLayers.append(bar)
            containerLayer.addSublayer(bar)
        }
    }

    // MARK: - Layout

    override func layout() {
        super.layout()
        layoutBars()
    }

    private func layoutBars() {
        guard bounds.width > 0, bounds.height > 0 else { return }

        // Calculate total width and center the bars horizontally
        let totalWidth = CGFloat(barCount) * (barWidth + barSpacing) - barSpacing
        let startX = (bounds.width - totalWidth) / 2
        let barHeight = bounds.height
        let centerY = bounds.height / 2

        // Disable implicit animations during layout
        CATransaction.begin()
        CATransaction.setDisableActions(true)

        // Set container bounds and position explicitly (don't use frame with non-default anchor)
        containerLayer.bounds = CGRect(origin: .zero, size: bounds.size)
        containerLayer.anchorPoint = CGPoint(x: 0.5, y: 0.5)
        containerLayer.position = CGPoint(x: bounds.midX, y: bounds.midY)

        for (i, bar) in barLayers.enumerated() {
            let x = startX + CGFloat(i) * (barWidth + barSpacing)
            // CENTERING FIX: Use bounds + position instead of frame
            // Setting frame with non-default anchorPoint can cause unexpected positioning.
            // By setting bounds (size) and position (center point) separately,
            // we ensure the bar is truly centered at centerY and scales symmetrically.
            bar.bounds = CGRect(origin: .zero, size: CGSize(width: barWidth, height: barHeight))
            bar.position = CGPoint(x: x + barWidth / 2, y: centerY)
        }

        CATransaction.commit()
    }

    // MARK: - Audio Level Updates

    /// Update the waveform amplitude. Call this at ~30 FPS with audio level.
    /// - Parameter level: Audio level from 0.0 (silence) to 1.0 (maximum)
    func setLevel(_ level: CGFloat) {
        // NOISE FLOOR DEADBAND
        // Background noise typically sits around 0.05-0.15 normalized level.
        // Treat anything below this threshold as silence to prevent jittery motion
        // from minor ambient sound fluctuations.
        let noiseFloor: CGFloat = 0.08
        let effectiveLevel = level < noiseFloor ? 0 : level

        // Attack/release smoothing - symmetric coefficients for consistent motion
        // Using same value for both prevents the "fast decay, slow rise" asymmetry
        let smoothingFactor: CGFloat = 0.10

        if effectiveLevel > smoothedLevel {
            smoothedLevel = smoothedLevel * (1 - smoothingFactor) + effectiveLevel * smoothingFactor
        } else {
            smoothedLevel = smoothedLevel * (1 - smoothingFactor) + effectiveLevel * smoothingFactor
        }

        let amp = min(max(smoothedLevel, 0), 1)

        // Scale container layer based on amplitude
        // At silence (amp=0): 25% height
        // At max (amp=1): 100% height
        let scale = 0.25 + 0.75 * amp

        // CONSTANT animation speed regardless of amplitude
        // This prevents the visual illusion where smaller bars (low amplitude)
        // appear to oscillate faster than larger bars (high amplitude)
        let speed: Float = 2.0

        // Apply without implicit animation (we have explicit animations running)
        CATransaction.begin()
        CATransaction.setDisableActions(true)

        containerLayer.transform = CATransform3DMakeScale(1, scale, 1)

        for bar in barLayers {
            bar.speed = speed
        }

        CATransaction.commit()
    }
}
