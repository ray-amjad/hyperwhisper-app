//
//  OffMainActor.swift
//  hyperwhisper
//
//  One bridge for running a blocking synchronous call off the main actor.
//

import Foundation

/// Run a blocking synchronous `body` off the main actor and await its result.
///
/// **Why this exists (Sentry HYPERWHISPER-F7 and HYPERWHISPER-HP, "App hanging for at
/// least 10000 ms"):** the app is full of synchronous system calls that are cheap until
/// they are not — CoreAudio property reads are `mach_msg` round trips to `coreaudiod`,
/// which can take many seconds during an audio route change or wake-from-sleep, and
/// filesystem calls stall on a cold iCloud/Dropbox provider. Every caller of those is a
/// `@MainActor` type, so the whole app froze for the wait.
///
/// **Why `nonisolated` is not the fix.** This is the mistake this helper exists to stop
/// people making. A `nonisolated` method called from `@MainActor` code still runs
/// synchronously on the caller's thread — `nonisolated` only removes the actor hop, it
/// does not add a thread hop. `Task.detached` below is what actually leaves the main
/// thread. Marking a blocking method `nonisolated` and calling it directly from the main
/// actor changes nothing at all.
///
/// **Why detached and not a continuation over a queue.** Detached requires the result to
/// be `Sendable`, which every use here satisfies, and it pins the blocking work at
/// `.userInitiated` instead of inheriting the caller's priority. Work that has to hand
/// back a *non-Sendable* object cannot use this and needs a checked continuation instead,
/// whose `resume(returning:)` takes its value as `sending` — see
/// `SimpleRecorder.makeLiveRecorder(url:settings:)`, which also needs a dedicated serial
/// queue so two recorder starts are never inside `record()` at once.
///
/// **No serialization guarantee.** Two callers can be inside the blocking API at the same
/// time. That is fine for property reads and for last-write-wins system writes; it is not
/// fine for anything that must not overlap, which is exactly why the recorder start does
/// not use this.
///
/// Written as one shared bridge because the same six-line `Task.detached { … }.value`
/// block had been copied into `CrashRecoveryManager`, `HistoryView`,
/// `RecordingTranscriptionFlow+StopRecording` and the CoreAudio device path, each with its
/// own re-derivation of the reasoning above.
///
/// - Parameters:
///   - priority: Priority for the detached work. Defaults to `.userInitiated` because
///     every current caller is on a path the user is waiting on.
///   - body: The blocking work. Must not touch main-actor state — capture what it needs
///     as `Sendable` values before calling.
/// - Returns: Whatever `body` returned.
func offMainActor<T: Sendable>(
    priority: TaskPriority = .userInitiated,
    _ body: @escaping @Sendable () -> T
) async -> T {
    await Task.detached(priority: priority) {
        body()
    }.value
}
