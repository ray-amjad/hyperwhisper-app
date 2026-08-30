//
//  CloudAuthRecoveryTrace.swift
//  hyperwhisper
//
//  Diagnostics for the HyperWhisper Cloud once-only licence repair.
//
//  WHY THIS EXISTS — HYPERWHISPER-T2
//  ---------------------------------
//  `TranscriptionPipeline.transcribeWithDetails failed` reports every cloud
//  auth refusal as one issue: `error_class=auth`, `errorKind=unauthorized`,
//  `retryable=false`, provider "HyperWhisper Cloud". That is all it says.
//
//  `performTranscribeRequestWithLicenseRecovery` has several terminal paths and
//  most of them used to end in the same bare `throw requestError`, so the report
//  could not tell them apart:
//
//    - the client never believed it was licensed, so no repair was tried
//    - `/license/validate` refused the identity
//    - the re-resolved identity came back unlicensed
//    - the server-side auth-cache refresh failed
//    - the retry send was refused a second time
//
//  Those are different faults with different fixes. This type names which one
//  happened, and carries the HTTP status of each refusal so a 401 (credential
//  missing or expired) is separable from a 403 (credential known and refused).
//
//  THE SCRUBBER TRAP — WHY NOTHING HERE IS SPELLED "auth"
//  -----------------------------------------------------
//  Sentry's stock `@password:filter` data scrubber matches on the VALUE of an
//  extra, not only its key. Measured on a live HYPERWHISPER-T2 event: the
//  `errorCategory` extra (value "auth") and the `errorKind` extra (value
//  "unauthorized") both arrive as "[Filtered]" with `_meta.rule_id` set to
//  `@password:filter`, while the `error_class` TAG carrying the same "auth"
//  string survives intact.
//
//  So two rules hold for this file, and breaking either makes the whole trace
//  silently useless while the event still arrives and nobody notices:
//
//    1. No key and no VALUE here may contain "auth" or "unauthorized". That is
//       why the prefix is `hwcloud_recovery_` and why the slug for a repeated
//       refusal is `retry_send_refused_again`.
//    2. The outcome also goes on a TAG. Tags survive the scrubber, and a tag is
//       the only form you can search and group by in Sentry.
//
//  PRIVACY
//  -------
//  Nothing here is user content. The licence identifier is a secret and is NEVER
//  recorded — only whether it changed, as a Bool. Every field is a fixed slug, a
//  Bool, an HTTP status, a count or a duration in ms.
//
//  Scope extras, not breadcrumbs: `SentryService.beforeSend` sets
//  `event.breadcrumbs = nil`, so a breadcrumb never leaves the machine. Scope
//  extras survive and ride along with the pipeline's own capture.
//

import Foundation

/// Records which branch of the HyperWhisper Cloud licence repair produced the
/// outcome, and publishes it as Sentry scope extras plus one tag.
///
/// ONE INSTANCE PER TRANSCRIPTION, NOT PER SEND. `CloudAudioFormatRecovery` can
/// call `performTranscribeRequestWithLicenseRecovery` twice for a single
/// transcription (the original upload, then a WAV re-encode after a 415), so a
/// trace built inside that function would let the second attempt's defaults
/// overwrite the first attempt's evidence on the same scope keys. The provider
/// builds one and passes it into both.
///
/// Reference type on purpose: the recovery function hands it around its own
/// branches, and both format attempts must write to the same snapshot.
final class CloudAuthRecoveryTrace {

    /// Which terminal path a recovery attempt took. These are the answer to
    /// HYPERWHISPER-T2, so the strings are a stable contract — a Sentry search
    /// saved against one of them must keep working. Rename nothing here without
    /// expecting the saved searches to go quiet.
    ///
    /// No raw value may contain "auth" or "unauthorized": see the scrubber note
    /// at the top of this file.
    enum Outcome: String {
        /// The first send worked. No repair was needed.
        case succeededFirstSend = "succeeded_first_send"
        /// The send failed, but not with an auth refusal. Recovery was not this
        /// path's business and the error left untouched.
        case otherErrorFirstSend = "other_error_first_send"
        /// Refused, but `isLicensed` was false, so the repair was skipped. The
        /// client believed it had nothing to repair.
        case notLicensedNoRetry = "not_licensed_no_retry"
        /// A live `/license/validate` said the identity is not valid.
        case revalidationInvalid = "revalidation_invalid"
        /// Validation passed, but the freshly resolved identity is not licensed.
        case reresolvedIdentityUnlicensed = "reresolved_identity_unlicensed"
        /// The server-side credential cache would not take the repaired identity.
        case serverCacheRefreshFailed = "server_cache_refresh_failed"
        /// The repair completed and the retry send was refused again. This is
        /// the branch that means the server, not the client, is the problem.
        case retrySendRefusedAgain = "retry_send_refused_again"
        /// The repair completed but the retry send failed for another reason
        /// (a 415 that the format recovery above then handles, say).
        case retrySendFailedOther = "retry_send_failed_other"
        /// The repair worked and the retry send succeeded.
        case succeededAfterRepair = "succeeded_after_repair"
        /// The user or the app cancelled mid-attempt.
        case cancelled = "cancelled"
    }

    /// Written to an HTTP-status field that has no value yet. A real status can
    /// never be 0, so a report never has to tell "absent" from "not reached".
    static let statusAbsent = 0

    /// Written to a slug field that has no value yet.
    static let slugNone = "none"

    /// `state` while an attempt is still running. An event carrying this stalled
    /// DURING a cloud send.
    static let stateRunning = "running"

    /// `state` once every attempt reached a terminal outcome.
    static let stateFinished = "finished"

    static let extraPrefix = "hwcloud_recovery_"

    /// The tag the outcome is published under. A tag, because the scrubber
    /// leaves tags alone and because only a tag is searchable.
    static let outcomeTagKey = "hwcloud_recovery_outcome"

    /// The whole published state of one transcription's recovery.
    ///
    /// Every publish writes EVERY field. `SentryService.setExtras` only adds and
    /// overwrites keys — it never removes one — so a payload that omitted a key
    /// would leave the PREVIOUS transcription's value on the scope, presented as
    /// this one's. A full key set makes that impossible.
    ///
    /// It cannot make the scope self-cleaning, though: these keys outlive the
    /// transcription and ride along on the next captured event of any kind. That
    /// is what `state` and `id` are for — an unrelated event carrying
    /// `state=finished` and an `id` that matches nothing else in the report is
    /// stale context, not this event's context.
    private struct Snapshot {
        var state: String = CloudAuthRecoveryTrace.stateRunning
        /// Terminal outcomes in order, joined by "+". A single-send
        /// transcription has one; a 415 format retry has two, and neither is
        /// lost.
        var outcomes: [String] = []
        var sendAttempts: Int = 0
        var firstStatus: Int = CloudAuthRecoveryTrace.statusAbsent
        var retryStatus: Int = CloudAuthRecoveryTrace.statusAbsent
        var licensedAtSend: Bool = false
        var revalidationValid: Bool = false
        var reresolvedLicensed: Bool = false
        /// Whether the re-resolved identifier DIFFERS from the one that was
        /// refused. The identifier itself is a secret and is never recorded.
        var identityChanged: Bool = false
        var elapsedMs: Int = 0
    }

    /// Distinguishes this transcription's block from a previous one still
    /// pinned to the global scope. Not user data — a fresh UUID per instance.
    private let id = UUID().uuidString
    private let start = Date()
    private let lock = NSLock()
    private var snapshot = Snapshot()

    init() {}

    // MARK: Reading back for the os.log lines

    /// The first refusal's status as a log-safe string, so the local os.log line
    /// and the Sentry extra always agree on one value.
    var firstStatusForLog: String {
        lock.lock()
        defer { lock.unlock() }
        return snapshot.firstStatus == Self.statusAbsent ? "unknown" : String(snapshot.firstStatus)
    }

    var retryStatusForLog: String {
        lock.lock()
        defer { lock.unlock() }
        return snapshot.retryStatus == Self.statusAbsent ? "unknown" : String(snapshot.retryStatus)
    }

    // MARK: Recording

    /// Opens a send attempt. Called once per entry into the recovery, so the
    /// 415 re-encode shows up as a second attempt rather than as a reset.
    func beginAttempt(licensedAtSend: Bool) {
        mutate {
            $0.state = Self.stateRunning
            $0.sendAttempts += 1
            $0.licensedAtSend = licensedAtSend
        }
    }

    /// First-writer-wins: attempt 1's 401 must not be erased by attempt 2
    /// finding no status to record.
    func recordFirstStatus(_ status: Int?) {
        guard let status else { return }
        mutate {
            if $0.firstStatus == Self.statusAbsent {
                $0.firstStatus = status
            }
        }
    }

    func recordRetryStatus(_ status: Int?) {
        guard let status else { return }
        mutate {
            if $0.retryStatus == Self.statusAbsent {
                $0.retryStatus = status
            }
        }
    }

    func recordRevalidation(isValid: Bool) {
        mutate { $0.revalidationValid = isValid }
    }

    func recordReresolvedIdentity(isLicensed: Bool, changed: Bool) {
        mutate {
            $0.reresolvedLicensed = isLicensed
            $0.identityChanged = changed
        }
    }

    /// Appends the terminal outcome of one send attempt and publishes. Call
    /// exactly once per attempt, on every path out of the recovery.
    func finish(_ outcome: Outcome) {
        mutate {
            $0.state = Self.stateFinished
            $0.outcomes.append(outcome.rawValue)
            $0.elapsedMs = Int((Date().timeIntervalSince(self.start) * 1_000).rounded())
        }
    }

    // MARK: Publishing

    private func mutate(_ change: (inout Snapshot) -> Void) {
        lock.lock()
        change(&snapshot)
        let current = snapshot
        lock.unlock()
        publish(current)
    }

    private func outcomeSlug(for values: Snapshot) -> String {
        values.outcomes.isEmpty ? Self.slugNone : values.outcomes.joined(separator: "+")
    }

    private func payload(for values: Snapshot) -> [String: Any] {
        [
            "\(Self.extraPrefix)id": id,
            "\(Self.extraPrefix)state": values.state,
            "\(Self.extraPrefix)outcome": outcomeSlug(for: values),
            "\(Self.extraPrefix)send_attempts": values.sendAttempts,
            "\(Self.extraPrefix)first_status": values.firstStatus,
            "\(Self.extraPrefix)retry_status": values.retryStatus,
            "\(Self.extraPrefix)licensed_at_send": values.licensedAtSend,
            "\(Self.extraPrefix)revalidation_valid": values.revalidationValid,
            "\(Self.extraPrefix)reresolved_licensed": values.reresolvedLicensed,
            "\(Self.extraPrefix)identity_changed": values.identityChanged,
            "\(Self.extraPrefix)elapsed_ms": values.elapsedMs
        ]
    }

    /// Scope extras and tags leave the machine, so they obey the error-logging
    /// opt-in.
    private func publish(_ values: Snapshot) {
        guard AppLogger.isErrorLoggingEnabled else { return }
        SentryService.setExtras(payload(for: values))
        SentryService.setTag(Self.outcomeTagKey, outcomeSlug(for: values))
    }
}
