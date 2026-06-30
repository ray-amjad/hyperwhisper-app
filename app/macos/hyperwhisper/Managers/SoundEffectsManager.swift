//
//  SoundEffectsManager.swift
//  hyperwhisper
//
//  Plays sound effects for recording start/stop events.
//  Uses AVAudioPlayer with pre-loaded sounds for instant playback.
//

import AVFoundation

@MainActor
final class SoundEffectsManager {

    static let shared = SoundEffectsManager()

    private var startPlayer: AVAudioPlayer?
    private var stopPlayer: AVAudioPlayer?

    private init() {
        startPlayer = loadSound("start1_quarter")
        stopPlayer = loadSound("stop2_quarter")
    }

    func playStartSound(volume: Double) {
        play(startPlayer, volume: Float(volume))
    }

    func playStopSound(volume: Double) {
        play(stopPlayer, volume: Float(volume))
    }

    private func loadSound(_ name: String) -> AVAudioPlayer? {
        let url = Bundle.main.url(forResource: name, withExtension: "wav", subdirectory: "Sounds")
            ?? Bundle.main.url(forResource: name, withExtension: "wav")
        guard let url else { return nil }
        let player = try? AVAudioPlayer(contentsOf: url)
        player?.prepareToPlay()
        return player
    }

    private func play(_ player: AVAudioPlayer?, volume: Float) {
        guard let player else { return }
        player.prepareToPlay()
        player.volume = volume
        player.currentTime = 0
        player.play()
    }
}
