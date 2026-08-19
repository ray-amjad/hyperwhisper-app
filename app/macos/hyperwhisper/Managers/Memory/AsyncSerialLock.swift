//
//  AsyncSerialLock.swift
//  hyperwhisper
//
//  A FIFO async mutex, for serialising an owner's load/teardown sequence.
//

import Foundation

/// Mutual exclusion that holds ACROSS `await`, handing the lock on in FIFO order.
///
/// An `actor` on its own does not give you this. Actor methods are REENTRANT:
/// another call slips in at every suspension point of the one in progress. That
/// reentrancy is the whole of HYPERWHISPER-SQ's whisper.cpp arm — a teardown
/// suspended inside `releaseResources()` resumed after a reload had already
/// installed a fresh context, and tore the fresh one down. Holding this lock
/// across an entire load or an entire teardown makes that interleaving
/// impossible rather than merely detectable.
///
/// Deliberately NOT cancellation-aware: every waiter is resumed by the holder's
/// `unlock()`, which callers run on the throwing path as well as the returning
/// one, so nobody can be parked forever. A task cancelled while waiting still
/// takes the lock, and checks cancellation itself once it holds it.
actor AsyncSerialLock {

    private var isHeld = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    /// Suspends until the lock is free, then takes it. MUST be balanced by
    /// exactly one `unlock()` on every exit path, including throws.
    func lock() async {
        guard isHeld else {
            isHeld = true
            return
        }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            waiters.append(continuation)
        }
    }

    /// Hands the lock to the longest-waiting caller, or frees it when nobody is
    /// waiting. Ownership passes directly on a handoff, so `isHeld` stays true —
    /// a third caller arriving in between must still wait.
    func unlock() {
        if waiters.isEmpty {
            isHeld = false
        } else {
            waiters.removeFirst().resume()
        }
    }
}
