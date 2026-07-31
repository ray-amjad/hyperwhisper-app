//
//  StreamingLiveText.swift
//  hyperwhisper
//
//  DEDICATED HIGH-FREQUENCY STREAMING TEXT OBJECT
//  This class isolates the rapidly-updating streaming transcript preview text from
//  AppState to prevent main app view tree invalidation on every streaming delta.
//
//  **The Problem (HYPERWHISPER-F7):**
//  `AppState.streamingText` used to be `@Published` directly on `AppState`. During a
//  streaming session, interim + final deltas arrive multiple times per second from the
//  active provider (Deepgram / ElevenLabs / xAI / HyperWhisper Cloud / on-device
//  Parakeet / Nemotron), each replacing the string wholesale with a growing payload for
//  the length of the session. Because SwiftUI's `@EnvironmentObject` dependency
//  tracking is coarse-grained (any `@Published` change on the object invalidates every
//  view holding that object, whether or not it reads the changed property), every delta
//  forced a full re-evaluation of the main window's view graph — including
//  `MainAppView`'s `NavigationSplitView`/sidebar `ScrollView` and the active tab
//  (e.g. `HistoryView`'s `List`) — not just the two views that actually render the
//  streaming preview (`RecordingDialog`, `StreamingPreviewBubble`). Under memory
//  pressure this pile-up of synchronous graph updates surfaced as multi-second main
//  thread "App Hang"s (Sentry HYPERWHISPER-F7).
//
//  **The Solution:**
//  Same pattern already used for `AudioRecordingManager.liveMetrics` (30 FPS
//  audioLevel/recordingDuration) — move the high-frequency value into its own
//  `ObservableObject` so only the views that actually need it observe it directly.
//  `AppState.streamingText` remains available as a computed passthrough so existing
//  read/write call sites are unaffected, but it no longer republishes through
//  `AppState`'s `objectWillChange`.
//
//  **Usage:**
//  - Inject via `.environmentObject(appState.liveStreamingText)`
//  - Only RecordingDialog and StreamingPreviewBubble should observe this directly
//  - Other views should continue to observe AppState for stable streaming state
//    (isStreaming, streamingConnectionState, etc.)
//

import Foundation
import Combine

/// Dedicated object for the high-frequency streaming transcript preview text.
/// Isolated from `AppState` so main app views don't invalidate on every streaming delta.
@MainActor
final class StreamingLiveText: ObservableObject {

    /// Latest streaming transcript text — interim delta, final delta, or the
    /// accumulated preview shown in the floating bubble. Updated multiple times per
    /// second while a streaming session is active.
    @Published var text: String = ""
}
