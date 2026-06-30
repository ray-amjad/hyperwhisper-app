//
//  MemoryFootprint.swift
//  hyperwhisper
//
//  Reads the process's own physical memory footprint — the same number
//  Activity Monitor / `footprint` / `vmmap` call "phys_footprint" — so the app
//  can sample and log its own memory instead of being blind to it.
//
//  Motivation: a 4.9 GB footprint peak was once only observable via `vmmap`
//  after the fact and was invisible in the app's own logs. Sampling phys_footprint
//  at model load/release/pressure transitions makes that visible in-app.
//

import Foundation
import Darwin

enum MemoryFootprint {

    /// Current physical footprint in bytes, or nil if the kernel query fails.
    static func currentBytes() -> UInt64? {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(
            MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size
        )
        let result = withUnsafeMutablePointer(to: &info) { ptr in
            ptr.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { intPtr in
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), intPtr, &count)
            }
        }
        guard result == KERN_SUCCESS else { return nil }
        return info.phys_footprint
    }

    /// Footprint as a whole-number megabytes string for logs, e.g. "1906".
    /// Returns "unknown" if the query fails so log lines stay well-formed.
    static func currentMB() -> String {
        guard let bytes = currentBytes() else { return "unknown" }
        return String(bytes / (1024 * 1024))
    }
}
