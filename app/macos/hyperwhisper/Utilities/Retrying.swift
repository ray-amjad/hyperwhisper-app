//
//  Retrying.swift
//  hyperwhisper
//
//  Shared retry logic used across transcription flows.

import Foundation

/// Configuration for retry behavior
struct RetryConfiguration {
    let maxAttempts: Int
    let initialDelay: TimeInterval
    let maxDelay: TimeInterval
    let backoffMultiplier: Double
    let jitterRange: ClosedRange<Double>

    // NOTE (M3-B.4): the `.transcription` preset was removed once every cloud STT
    // provider migrated to the Rust shared core's `nextRetry`-driven retry loop
    // (`RustRetry.perform`). The remaining presets (`.postProcessing`, `.cloud`)
    // still back native callers — see below.

    /// Configuration for AI post-processing retries (fewer attempts)
    static let postProcessing = RetryConfiguration(
        maxAttempts: 3,
        initialDelay: 1.0,
        maxDelay: 10.0,
        backoffMultiplier: 2.0,
        jitterRange: 0...0.2
    )

    /// Configuration for cloud provider retries (matches Windows MaxRetries=3)
    static let cloud = RetryConfiguration(
        maxAttempts: 3,
        initialDelay: 2.0,
        maxDelay: 30.0,
        backoffMultiplier: 2.0,
        jitterRange: 0...0.2
    )

    /// Upper bound, in seconds, that a single honored `Retry-After` may sleep
    /// inside a status-poll loop. A hostile or misconfigured server can return a
    /// very large `Retry-After` (e.g. 300s); clamping each honored sleep to this
    /// value — combined with a per-provider total deadline at the call site —
    /// keeps the poll loop from hanging far past its documented budget.
    static let maxPollRetryAfterSeconds = 10

    /// Calculate delay for a given attempt number
    func delay(for attempt: Int) -> TimeInterval {
        let baseDelay = initialDelay * pow(backoffMultiplier, Double(attempt - 1))
        let jitter = Double.random(in: jitterRange) * baseDelay
        return min(baseDelay + jitter, maxDelay)
    }
}

/// Parse the integer Retry-After header from an HTTP response.
/// Reusable across all cloud providers.
func parseRetryAfter(from response: HTTPURLResponse) -> Int? {
    guard let value = response.value(forHTTPHeaderField: "Retry-After"),
          let seconds = Int(value) else { return nil }
    return seconds
}

/// Generic retry helper with exponential backoff
/// Used by both TranscriptionPipeline and CloudWhisperProvider
func performWithRetry<T>(
    config: RetryConfiguration,
    operation: @escaping (Int) async throws -> T
) async throws -> T {
    var lastError: Error = TranscriptionError.transientNetwork(details: nil)

    for attempt in 1...config.maxAttempts {
        do {
            return try await operation(attempt)
        } catch let cancel as CancellationError {
            // Never retry cancellations
            throw cancel
        } catch {
            lastError = error

            // Check if error is retryable
            if let transcriptionError = error as? TranscriptionError,
               !transcriptionError.isRetryable {
                throw error
            }

            // Don't sleep after the last attempt
            if attempt < config.maxAttempts {
                let delay = config.delay(for: attempt)
                AppLogger.network.debug("Retry delay: \(String(format: "%.1f", delay), privacy: .public) seconds")
                try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            }
        }
    }

    // All retries exhausted
    throw lastError
}

