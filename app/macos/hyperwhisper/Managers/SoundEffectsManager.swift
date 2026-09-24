//
//  SoundEffectsManager.swift
//  hyperwhisper
//
//  Plays sound effects for recording start/stop events.
//  Uses AVAudioPlayer with pre-loaded sounds for instant playback.
//
//  Every AVAudioPlayer call waits on the CoreAudio HAL, which can stall for
//  seconds during a route change (HYPERWHISPER-KY). So the players are created,
//  prepared and played only on a private serial queue, never on the main thread.
//

import AVFoundation

/// The part of AVAudioPlayer this manager uses, so a test can inject a fake.
protocol SoundEffectPlayer: AnyObject {
    var volume: Float { get set }
    var currentTime: TimeInterval { get set }
    @discardableResult func prepareToPlay() -> Bool
    @discardableResult func play() -> Bool
}

extension AVAudioPlayer: SoundEffectPlayer {}

// Unchecked: the players are read and written only on `queue`.
final class SoundEffectsManager: @unchecked Sendable {

    static let shared = SoundEffectsManager()

    // Serial, so 2 sounds never touch the HAL at the same time.
    private let queue = DispatchQueue(label: "com.hyperwhisper.sound-effects", qos: .userInitiated)
    private var startPlayer: SoundEffectPlayer?
    private var stopPlayer: SoundEffectPlayer?

    private convenience init() {
        self.init(loader: { SoundEffectsManager.loadSound($0) })
    }

    /// Loads and prepares both players once, on `queue`. The queue is serial,
    /// so the load always runs before the first play.
    init(loader: @escaping @Sendable (String) -> SoundEffectPlayer?) {
        queue.async { [self] in
            startPlayer = loader("start1_quarter")
            stopPlayer = loader("stop2_quarter")
            startPlayer?.prepareToPlay()
            stopPlayer?.prepareToPlay()
        }
    }

    /// Returns at once; the sound plays a few ms later on `queue`.
    func playStartSound(volume: Double) {
        queue.async { [self] in play(startPlayer, volume: Float(volume)) }
    }

    /// Returns at once; the sound plays a few ms later on `queue`.
    func playStopSound(volume: Double) {
        queue.async { [self] in play(stopPlayer, volume: Float(volume)) }
    }

    private static func loadSound(_ name: String) -> SoundEffectPlayer? {
        let url = Bundle.main.url(forResource: name, withExtension: "wav", subdirectory: "Sounds")
            ?? Bundle.main.url(forResource: name, withExtension: "wav")
        guard let url else { return nil }
        return try? AVAudioPlayer(contentsOf: url)
    }

    // `play()` prepares an unprepared player itself, so no `prepareToPlay()` here.
    private func play(_ player: SoundEffectPlayer?, volume: Float) {
        guard let player else { return }
        player.volume = volume
        player.currentTime = 0
        player.play()
    }
}
