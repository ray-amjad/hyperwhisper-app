//
//  SentryService+Tracing.swift
//  hyperwhisper
//
//  Performance spans and the SpanProtocol bridge.
//  Part of `SentryService`; see SentryService.swift.
//

import Foundation
#if canImport(Sentry)
@_spi(Private) import Sentry
#endif

extension SentryService {

    // MARK: - Performance Spans

    /// Start a new transaction for a user-facing operation.
    /// Transactions are the top-level performance unit in Sentry.
    /// Use for major operations like "Transcribe Audio" or "Export Diagnostics".
    /// - Parameters:
    ///   - name: Human-readable name (e.g., "Transcribe Audio")
    ///   - operation: Category (e.g., "transcription", "ui", "export")
    /// - Returns: A span that must be finished when the operation completes
    @discardableResult
    static func startTransaction(name: String, operation: String) -> SpanProtocol? {
        #if canImport(Sentry)
        // A finished transaction is an event like any other, so it goes through
        // the same gate. Callers already handle a nil span.
        guard isReportingEnabled else { return nil }
        return SentrySDK.startTransaction(name: name, operation: operation, bindToScope: true)
        #else
        return nil
        #endif
    }

    /// Start a child span under the current transaction.
    /// Use for sub-operations within a transaction (e.g., "API Call", "Audio Conversion").
    /// - Parameters:
    ///   - operation: Category (e.g., "http", "file", "process")
    ///   - description: Human-readable description (e.g., "POST /transcribe")
    /// - Returns: A span that must be finished when the sub-operation completes
    @discardableResult
    static func startSpan(operation: String, description: String) -> SpanProtocol? {
        #if canImport(Sentry)
        return SentrySDK.span?.startChild(operation: operation, description: description)
        #else
        return nil
        #endif
    }

    /// Finish a span, recording its duration.
    /// Call this when the operation represented by the span completes.
    static func finishSpan(_ span: SpanProtocol?, status: SpanStatus = .ok) {
        #if canImport(Sentry)
        span?.status = status
        span?.finish()
        #endif
    }

    /// Measure an async operation and record it as a span.
    /// Automatically starts and finishes the span around the work closure.
    /// - Parameters:
    ///   - operation: Category (e.g., "transcription", "http")
    ///   - description: Human-readable description
    ///   - work: The async work to measure
    /// - Returns: The result of the work closure
    static func measureAsync<T>(
        operation: String,
        description: String,
        work: () async throws -> T
    ) async rethrows -> T {
        #if canImport(Sentry)
        let span = SentrySDK.span?.startChild(operation: operation, description: description)
        do {
            let result = try await work()
            span?.status = .ok
            span?.finish()
            return result
        } catch {
            span?.status = .internalError
            span?.finish()
            throw error
        }
        #else
        return try await work()
        #endif
    }

    /// Measure a synchronous operation and record it as a span.
    static func measure<T>(
        operation: String,
        description: String,
        work: () throws -> T
    ) rethrows -> T {
        #if canImport(Sentry)
        let span = SentrySDK.span?.startChild(operation: operation, description: description)
        do {
            let result = try work()
            span?.status = .ok
            span?.finish()
            return result
        } catch {
            span?.status = .internalError
            span?.finish()
            throw error
        }
        #else
        return try work()
        #endif
    }

    // MARK: - Custom Metrics
    // NOTE: Sentry metrics API (SentrySDK.metrics) requires explicit enablement
    // and may have limited availability. These functions are stubs that log locally
    // until metrics are configured. To enable, add to options:
    //   options.enableMetrics = true (if available in your SDK version)

}

// MARK: - SpanProtocol Extension

#if canImport(Sentry)
/// Protocol to abstract Sentry's Span type for easier testing and type erasure.
/// The actual Sentry Span conforms to this via the SDK.
public typealias SpanProtocol = Span
public typealias SpanStatus = SentrySpanStatus
#else
/// Stub protocol when Sentry is not available.
public protocol SpanProtocol {
    var status: SpanStatus { get set }
    func finish()
    func startChild(operation: String, description: String) -> SpanProtocol
}
public enum SpanStatus {
    case ok
    case internalError
    /// Work that was abandoned on purpose rather than succeeding or failing — e.g. a
    /// recording start superseded by a newer one. Mirrors `kSentrySpanStatusCancelled`.
    case cancelled
}
#endif
