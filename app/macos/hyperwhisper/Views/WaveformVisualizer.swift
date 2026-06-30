//
//  WaveformVisualizer.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 18/08/2025.
//
//  WAVEFORM VISUALIZER
//  Generates and displays audio waveform visualization from audio files.
//  Used in the history view to show visual representation of recordings.
//
//  PERFORMANCE OPTIMIZATION:
//  Uses sparse sampling instead of loading entire audio files into memory.
//  - Seeks to 100 evenly-spaced positions in the audio file
//  - Reads only ~256 samples at each position
//  - Caches results in NSCache for instant repeat access
//  This approach is dramatically faster than loading full audio buffers,
//  especially for long recordings (hours of audio load in ~10ms).
//

import SwiftUI
import AVFoundation
import os

/// Logger for WaveformVisualizer
private let waveformLogger = Logger(subsystem: "com.hyperwhisper.app", category: "WaveformVisualizer")

// MARK: - Waveform Cache

/// In-memory cache for computed waveform samples.
/// Uses NSCache for automatic memory pressure eviction.
/// Keyed by audio file path - survives view lifecycle but not app restart.
actor WaveformCache {
    static let shared = WaveformCache()
    private let cache = NSCache<NSString, NSArray>()

    private init() {
        // Allow up to 100 waveforms in cache (each is ~400 bytes)
        cache.countLimit = 100
    }

    func get(_ path: String) -> [Float]? {
        cache.object(forKey: path as NSString) as? [Float]
    }

    func set(_ path: String, samples: [Float]) {
        cache.setObject(samples as NSArray, forKey: path as NSString)
    }
}

// MARK: - Waveform Visualizer View

struct WaveformVisualizer: View {
    let audioFilePath: String?
    @State private var waveformSamples: [Float] = []
    @State private var isLoading = true
    @State private var duration: TimeInterval = 0
    
    // Display configuration
    private let sampleCount = 100 // Number of samples to display
    private let barWidth: CGFloat = 2
    private let barSpacing: CGFloat = 1
    
    var body: some View {
        GeometryReader { geometry in
            if isLoading {
                // Loading state
                HStack(spacing: barSpacing) {
                    ForEach(0..<20, id: \.self) { _ in
                        RoundedRectangle(cornerRadius: 1)
                            .fill(Color.secondary.opacity(0.2))
                            .frame(width: barWidth)
                    }
                }
                .frame(maxHeight: .infinity)
            } else if !waveformSamples.isEmpty {
                // Waveform display
                HStack(alignment: .center, spacing: barSpacing) {
                    ForEach(0..<waveformSamples.count, id: \.self) { index in
                        WaveformBar(
                            amplitude: CGFloat(waveformSamples[index]),
                            maxHeight: geometry.size.height
                        )
                    }
                }
            } else {
                // No audio data
                HStack(spacing: barSpacing) {
                    ForEach(0..<20, id: \.self) { _ in
                        RoundedRectangle(cornerRadius: 1)
                            .fill(Color.secondary.opacity(0.1))
                            .frame(width: barWidth, height: 4)
                    }
                }
                .frame(maxHeight: .infinity)
            }
        }
        .onAppear {
            loadWaveform()
        }
        .onChange(of: audioFilePath) {
            loadWaveform()
        }
    }
    
    // MARK: - Waveform Loading
    
    private func loadWaveform() {
        guard let audioFilePath = audioFilePath,
              !audioFilePath.isEmpty else {
            isLoading = false
            return
        }
        
        Task {
            await generateWaveform(from: audioFilePath)
        }
    }
    
    // SPARSE SAMPLING WAVEFORM GENERATION
    // Instead of loading the entire audio file into memory (which can be 100MB+ for long recordings),
    // we seek to 100 evenly-spaced positions and read only ~256 samples at each.
    // This reduces memory usage to ~10KB and makes loading near-instant for any file size.
    //
    // How it works:
    // 1. Check cache first - if we've already computed this waveform, use it instantly
    // 2. Open the audio file and get total frame count
    // 3. For each of 100 bars, calculate the position in the file
    // 4. Seek to that position and read a small buffer (256 samples)
    // 5. Calculate average amplitude from that buffer
    // 6. Cache the result for future views
    private func generateWaveform(from path: String) async {
        // STEP 1: Check cache first for instant retrieval
        if let cached = await WaveformCache.shared.get(path) {
            await MainActor.run {
                self.waveformSamples = cached
                self.isLoading = false
            }
            return
        }

        let url = URL(fileURLWithPath: path)

        // Check if file exists
        guard FileManager.default.fileExists(atPath: path) else {
            await MainActor.run {
                self.isLoading = false
                self.waveformSamples = []
            }
            return
        }

        do {
            // STEP 2: Open audio file (does not load into memory)
            let audioFile = try AVAudioFile(forReading: url)
            let totalFrames = audioFile.length
            let format = audioFile.processingFormat

            // Calculate duration for display
            let duration = Double(totalFrames) / format.sampleRate

            // STEP 3: Create a small reusable buffer for sparse sampling
            // 256 samples is enough to get a representative amplitude at each position
            let samplesPerRead: AVAudioFrameCount = 256
            guard let smallBuffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: samplesPerRead) else {
                await MainActor.run {
                    self.isLoading = false
                }
                return
            }

            var samples: [Float] = []
            samples.reserveCapacity(sampleCount)

            // STEP 4: Sample at 100 evenly-spaced positions
            for i in 0..<sampleCount {
                // Calculate seek position for this bar
                let position = AVAudioFramePosition((Double(i) / Double(sampleCount)) * Double(totalFrames))

                // Clamp position to valid range (leave room for buffer read)
                let clampedPosition = min(position, max(0, totalFrames - AVAudioFramePosition(samplesPerRead)))
                audioFile.framePosition = clampedPosition

                // Read small buffer at this position
                smallBuffer.frameLength = 0 // Reset buffer
                let framesToRead = min(samplesPerRead, AVAudioFrameCount(totalFrames - clampedPosition))

                do {
                    try audioFile.read(into: smallBuffer, frameCount: framesToRead)
                } catch {
                    // If read fails at this position, use zero amplitude
                    samples.append(0)
                    continue
                }

                // STEP 5: Calculate average amplitude from this small buffer
                if let channelData = smallBuffer.floatChannelData, smallBuffer.frameLength > 0 {
                    var sum: Float = 0
                    let frameCount = Int(smallBuffer.frameLength)

                    // Average across all samples in the buffer (first channel only for speed)
                    for j in 0..<frameCount {
                        sum += abs(channelData[0][j])
                    }

                    let avg = sum / Float(frameCount)
                    // Normalize with 3x gain for better visual representation
                    samples.append(min(1.0, avg * 3.0))
                } else {
                    samples.append(0)
                }
            }

            // STEP 6: Cache the result for instant future access
            await WaveformCache.shared.set(path, samples: samples)

            // Update UI on main thread
            await MainActor.run {
                self.waveformSamples = samples
                self.duration = duration
                self.isLoading = false
            }

        } catch {
            waveformLogger.error("Error loading audio file for waveform: \(error, privacy: .public)")
            await MainActor.run {
                self.isLoading = false
                self.waveformSamples = []
            }
        }
    }
}

// MARK: - Waveform Bar Component

private struct WaveformBar: View {
    let amplitude: CGFloat
    let maxHeight: CGFloat
    
    var body: some View {
        VStack {
            Spacer(minLength: 0)
            
            RoundedRectangle(cornerRadius: 1)
                .fill(
                    LinearGradient(
                        colors: [
                            Color.accentColor.opacity(0.8),
                            Color.accentColor.opacity(0.4)
                        ],
                        startPoint: .bottom,
                        endPoint: .top
                    )
                )
                .frame(
                    width: 2,
                    height: max(2, amplitude * maxHeight)
                )
            
            Spacer(minLength: 0)
        }
    }
}

// MARK: - Preview

#Preview {
    VStack(spacing: 20) {
        // With audio file
        WaveformVisualizer(audioFilePath: "/path/to/audio.wav")
            .frame(height: 40)
            .padding()
        
        // Without audio file
        WaveformVisualizer(audioFilePath: nil)
            .frame(height: 40)
            .padding()
    }
    .frame(width: 400)
}