//
//  SystemCapability.swift
//  hyperwhisper
//
//  Single source of truth for "can this machine run the embedded llama.cpp
//  local LLM runtime?". Detected at RUNTIME (not via the compile-time
//  `#if arch(arm64)` we used to rely on) so a process running under Rosetta
//  translation on Apple Silicon is correctly told to relaunch natively instead
//  of being misreported as permanently unsupported.
//

import Foundation
import Darwin

/// Hardware / runtime capability for local post-processing.
enum SystemCapability: Equatable {
    /// Native arm64 on Apple Silicon — local post-processing works now.
    case supported
    /// Apple Silicon hardware, but THIS process is running translated under
    /// Rosetta. The arm64 runtime can't load reliably; relaunching the app
    /// natively (it's a universal/arm64 build) fixes it.
    case needsNativeRelaunch
    /// Intel hardware (or any non-arm64 machine) — local post-processing can
    /// never run. Cloud post-processing providers remain available.
    case unsupported

    /// Resolved once at first access. Neither the CPU architecture nor the
    /// translation state can change within a process lifetime, so caching is safe.
    static let current: SystemCapability = {
        // `hw.optional.arm64` is 1 on Apple Silicon, 0 / absent on Intel.
        guard Self.sysctlFlag("hw.optional.arm64") == 1 else {
            return .unsupported
        }
        // `sysctl.proc_translated` is 1 when this process runs under Rosetta,
        // 0 when native, and absent (ENOENT) on Intel — but we only reach here
        // on Apple-Silicon hardware, where it's always present.
        if Self.sysctlFlag("sysctl.proc_translated") == 1 {
            return .needsNativeRelaunch
        }
        return .supported
    }()

    /// True for any Apple-Silicon machine (native OR translated). Use this to
    /// decide whether local post-processing should be *offered* at all.
    var isAppleSiliconHardware: Bool {
        self != .unsupported
    }

    /// True ONLY when the local runtime can actually load and run in this
    /// process. Use this as the gate before launching llama-server.
    var canRunLocalRuntime: Bool {
        self == .supported
    }

    /// Reads an integer sysctl by name, returning 0 when the sysctl is absent
    /// or the call fails (so a missing `proc_translated` reads as "not translated").
    private static func sysctlFlag(_ name: String) -> Int32 {
        var value: Int32 = 0
        var size = MemoryLayout<Int32>.size
        let result = sysctlbyname(name, &value, &size, nil, 0)
        return result == 0 ? value : 0
    }
}
