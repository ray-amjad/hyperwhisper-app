//
//  ResidencyGate.swift
//  hyperwhisperTests
//
//  Shared handshake for the residency tests. Lives on its own so
//  `ModelResidencyRegistryTests` and `ResidentRuntimeLifecycleTests` drive their
//  interleavings with the same primitive instead of two copies of it.
//

import Foundation
import Testing

/// A deterministic handshake between a test and a closure it is deliberately
/// blocking inside.
///
/// Deterministic rather than hopefully-long-enough: `signal()` buffers, so it is
/// safe to call before the waiter has started, and `open()` is permanent, so
/// every gate a test opens on its way out releases current *and* future waiters.
/// That last property is what keeps a failed `#expect` from parking a closure
/// forever.
///
/// ## Why it is not an `AsyncStream`
///
/// It was one, and `wait()` built a FRESH iterator over a SHARED single-consumer
/// stream on every call. Apple documents `AsyncStream`'s iterator as
/// `fatalError`-ing when two `next()` calls contend, and the tests already wire
/// both of an eviction round's evict closures to one gate
/// (`ModelResidencyRegistryTests`' two-victim case) — latent only because
/// `evict` frees its victims serially. Continuations in an array, handed out
/// under a lock, make the multi-waiter case honest instead of accidental.
///
/// ## Why every wait is bounded
///
/// The only `signal()` that releases some of these waits lives inside the code
/// under test, so a regression there is an UNBOUNDED wait. `app/macos` sets no
/// swift-testing `timeLimit` trait, swift-testing applies no implicit deadline,
/// and `macos-ci.yml` passes no `-test-timeouts-enabled` — so the outer backstop
/// is GitHub's 360-minute job default. A two-second assertion failure presented
/// as a six-hour infrastructure outage with no indication which test had
/// stopped. `wait()` therefore races a deadline that records a swift-testing
/// `Issue` naming the waiting call site and then permanently opens the gate, so
/// the suite FAILS in seconds and says where.
///
/// The deadline is a failsafe, never a synchronisation device: on the happy path
/// it is cancelled without having fired, and no assertion depends on it.
final class ResidencyGate: @unchecked Sendable {

    /// How long any single `wait()` may block before it is declared a hang.
    /// Generous next to the microseconds these handshakes actually take, and
    /// tiny next to the six hours it replaces.
    static let defaultTimeout: TimeInterval = 30

    private let mutex = NSLock()
    /// Buffered `signal()`s that no waiter has consumed yet.
    private var pendingSignals = 0
    /// Set by `open()`. Once open, every wait — current and future — completes.
    private var isOpen = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    /// Release one waiter (buffered if nobody is waiting yet).
    func signal() {
        mutex.lock()
        if waiters.isEmpty {
            pendingSignals += 1
            mutex.unlock()
            return
        }
        let waiter = waiters.removeFirst()
        mutex.unlock()
        waiter.resume()
    }

    /// Release every current and future waiter, permanently.
    func open() {
        mutex.lock()
        isOpen = true
        let released = waiters
        waiters.removeAll()
        mutex.unlock()
        for waiter in released {
            waiter.resume()
        }
    }

    /// Suspend until `signal()` or `open()` — or until `timeout` elapses, which
    /// fails the test rather than hanging it.
    func wait(
        timeout: TimeInterval = ResidencyGate.defaultTimeout,
        fileID: String = #fileID,
        line: Int = #line
    ) async {
        let deadline = Task {
            do {
                try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
            } catch {
                return  // Cancelled because the wait completed normally.
            }
            Issue.record("ResidencyGate.wait() at \(fileID):\(line) blocked for \(timeout)s — the signal it was waiting for never came. Opening the gate so the suite fails here instead of hanging.")
            self.open()
        }
        await suspendUntilReleased()
        deadline.cancel()
    }

    private func suspendUntilReleased() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            mutex.lock()
            if isOpen {
                mutex.unlock()
                continuation.resume()
                return
            }
            if pendingSignals > 0 {
                pendingSignals -= 1
                mutex.unlock()
                continuation.resume()
                return
            }
            waiters.append(continuation)
            mutex.unlock()
        }
    }
}
