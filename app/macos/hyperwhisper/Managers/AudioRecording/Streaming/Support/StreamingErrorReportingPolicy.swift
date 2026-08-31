//
//  StreamingErrorReportingPolicy.swift
//  hyperwhisper
//
//  Decides how the recording flow treats a streaming failure it has been handed:
//  whether it is worth a Sentry issue, and how it is worded for the user.
//
//  A free namespace rather than a member of `RecordingTranscriptionFlow`: that
//  type owns the audio engine, Core Data and the app state, which would make
//  these two one-line decisions untestable.
//

import Foundation

/// Splits streaming failures into "the app has a problem" and "the user's
/// account has a problem", and answers the two questions that split governs.
///
/// ## Why the split is worth making
///
/// The two look identical at this layer — both arrive as an `Error` on
/// `onError` — but nothing about them should be handled alike.
///
/// A dropped socket is a defect: it belongs in Sentry, and the user is told
/// "Streaming error: …" because the app, not the user, is what went wrong.
///
/// An exhausted balance is neither. It is the server working as designed, and
/// the second Sentry issue it used to produce (`StreamingTranscriptionClient`
/// already captures the fault itself, tagged `terminal`) described the same
/// event less accurately, under a title that reads as an outage. Framing it as
/// a "Streaming error" is wrong in the same way: "Streaming error: Insufficient
/// credits. Add more credits in Settings." leads with the app's failure and
/// buries the sentence that tells the user what to do.
enum StreamingErrorReportingPolicy {

    /// Whether the failure names something only the user can fix.
    ///
    /// `StreamingError` answers for itself — see `isTerminalForUser`, which
    /// reads the case rather than the localized text. Anything else falls back
    /// to matching the message, which is what the flow can see of a failure that
    /// never passed through the streaming client.
    static func isTerminalForUser(_ error: Error) -> Bool {
        if let streamingError = error as? StreamingError {
            return streamingError.isTerminalForUser
        }
        return StreamingProviderErrorPolicy.outcome(forProviderMessage: error.localizedDescription) == .terminal
    }

    /// Whether this failure is worth a Sentry issue from the recording flow.
    ///
    /// - Returns: `false` for a user-fixable account state, `true` for
    ///   everything else — including every failure this policy does not
    ///   recognise, so an unfamiliar fault keeps today's reporting rather than
    ///   quietly disappearing.
    static func shouldCaptureInSentry(_ error: Error) -> Bool {
        !isTerminalForUser(error)
    }

    /// The error that is safe to hand to Sentry.
    ///
    /// ## Why an error cannot always be reported as it arrives
    ///
    /// A failure on the streaming socket is a `URLError`, and a `URLError`
    /// carries the URL it failed on in its `userInfo`
    /// (`NSURLErrorFailingURLStringErrorKey`, `NSURLErrorFailingURLErrorKey`).
    /// For this socket that URL is
    /// `wss://…/ws/streaming-deepgram?license_key=…` — the query string is the
    /// credential itself, which is why
    /// `StreamingTranscriptionClient.startSession` logs only the host and the
    /// path and never the whole URL.
    ///
    /// `SentryService.beforeSend` cannot catch it. That hook redacts `event.extra`
    /// entries whose KEY contains `transcript`, `text` or `prompt`; an NSError's
    /// `userInfo` reaches the event through the exception mechanism, under keys
    /// that match none of those words.
    ///
    /// Rebuilding the error from its domain and code keeps both values that
    /// identify the fault — `NSURLErrorDomain` / `-1005` — and gives the licence
    /// key no route out of the machine.
    ///
    /// - Note: Only `NSURLErrorDomain` is rebuilt. A `StreamingError` is a Swift
    ///   enum with no `userInfo`, and its localized sentence is what makes the
    ///   Sentry issue title readable, so it is passed through untouched.
    static func sentrySafeError(_ error: Error) -> Error {
        let nsError = error as NSError
        guard nsError.domain == NSURLErrorDomain else { return error }
        return NSError(domain: nsError.domain, code: nsError.code, userInfo: nil)
    }

    /// The sentence to show the user.
    ///
    /// - Parameters:
    ///   - error: The failure being reported.
    ///   - context: The framing for a fault the app owns, e.g.
    ///     `"Streaming error: "`. Dropped for a user-fixable one, whose own
    ///     description already names both the problem and the fix and should
    ///     lead.
    static func userMessage(for error: Error, context: String) -> String {
        let description = error.localizedDescription
        return isTerminalForUser(error) ? description : context + description
    }
}
