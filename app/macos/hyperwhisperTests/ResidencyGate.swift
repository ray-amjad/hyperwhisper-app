//
//  ResidencyGate.swift
//  hyperwhisperTests
//
//  Shared handshake for the residency tests. Lives on its own so
//  `ModelResidencyRegistryTests` and `ResidentRuntimeLifecycleTests` drive their
//  interleavings with the same primitive instead of two copies of it.
//

import Foundation

/// A deterministic handshake between a test and a closure it is deliberately
/// blocking inside.
///
/// Built on `AsyncStream` rather than `Task.sleep` so the ordering is exact
/// instead of hopefully-long-enough: `signal()` buffers, so it is safe to call
/// before the waiter has started, and `open()` is permanent, so every gate a
/// test opens on its way out releases current *and* future waiters. That last
/// property is what keeps a failed `#expect` from parking a closure forever and
/// hanging the whole suite in CI.
struct ResidencyGate {
    private let stream: AsyncStream<Void>
    private let continuation: AsyncStream<Void>.Continuation

    init() {
        let made = AsyncStream.makeStream(of: Void.self)
        self.stream = made.stream
        self.continuation = made.continuation
    }

    /// Release one waiter (buffered if nobody is waiting yet).
    func signal() {
        continuation.yield(())
    }

    /// Release every current and future waiter, permanently.
    func open() {
        continuation.finish()
    }

    /// Suspend until `signal()` or `open()`.
    func wait() async {
        var iterator = stream.makeAsyncIterator()
        _ = await iterator.next()
    }
}
