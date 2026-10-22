//
//  StallProbe1866.swift
//  hyperwhisperTests
//
//  TEMPORARY (#1866 proof, reverted before merge): freezes this whole test
//  process for 45 s, once, the way a loaded CI runner did, so the CI run shows
//  SimpleRecorderMeterReadTests passing through such a stall.
//

import Foundation
import os

enum StallProbe1866 {
    private static let fired = OSAllocatedUnfairLock(initialState: false)

    /// SIGSTOP this process from a child shell, then SIGCONT it 45 s later.
    static func stallProcessOnce() {
        let first = fired.withLock { done -> Bool in
            defer { done = true }
            return !done
        }
        guard first else { return }
        let pid = ProcessInfo.processInfo.processIdentifier
        let shell = Process()
        shell.executableURL = URL(fileURLWithPath: "/bin/sh")
        shell.arguments = ["-c", "sleep 0.05; kill -STOP \(pid); sleep 45; kill -CONT \(pid); echo STALL1866-DONE >&2"]
        try? shell.run()
        NSLog("STALL1866: stopping process \(pid) for 45 s")
    }
}
