//
//  LlamaProcessIdentity.swift
//  hyperwhisper
//
//  Verifies that a PID still belongs to the llama-server process HyperWhisper
//  launched before sending termination signals.
//

import Foundation

#if os(macOS)
import Darwin
#endif

struct LlamaProcessStartTime: Codable, Equatable, Sendable {
    let seconds: Int64
    let microseconds: Int32
}

struct LlamaServerPIDRecord: Codable, Equatable, Sendable {
    let pid: Int32
    let executablePath: String
    let startTime: LlamaProcessStartTime
}

enum LlamaPIDFileContents: Equatable, Sendable {
    case record(LlamaServerPIDRecord)
    case legacyPID(Int32)
    case invalid
}

enum LlamaProcessIdentity {
    static let executableName = "llama-server"

    static func canonicalizedPath(_ path: String) -> String {
        URL(fileURLWithPath: path)
            .resolvingSymlinksInPath()
            .standardizedFileURL
            .path
    }

    static func parsePIDFileData(_ data: Data) -> LlamaPIDFileContents {
        if let record = try? JSONDecoder().decode(LlamaServerPIDRecord.self, from: data),
           record.pid > 0,
           !record.executablePath.isEmpty {
            return .record(record)
        }

        guard let string = String(data: data, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines),
              let pid = Int32(string),
              pid > 0 else {
            return .invalid
        }

        return .legacyPID(pid)
    }

    static func encodePIDRecord(_ record: LlamaServerPIDRecord) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(record)
    }

    #if os(macOS)
    static func recordForLiveProcess(pid: Int32) -> LlamaServerPIDRecord? {
        guard let identity = liveProcessIdentity(pid: pid) else {
            return nil
        }
        return LlamaServerPIDRecord(
            pid: identity.pid,
            executablePath: identity.executablePath,
            startTime: identity.startTime
        )
    }

    static func liveProcessMatches(_ record: LlamaServerPIDRecord) -> Bool {
        guard let identity = liveProcessIdentity(pid: record.pid) else {
            return false
        }

        return identity.executablePath == canonicalizedPath(record.executablePath)
            && identity.startTime == record.startTime
    }

    static func liveProcessIdentity(pid: Int32) -> LlamaServerPIDRecord? {
        guard pid > 0 else { return nil }

        var pathBuffer = [CChar](repeating: 0, count: 4096)
        let pathLength = proc_pidpath(pid, &pathBuffer, UInt32(pathBuffer.count))
        guard pathLength > 0 else {
            return nil
        }

        var info = proc_bsdinfo()
        let infoSize = MemoryLayout<proc_bsdinfo>.stride
        let result = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: CChar.self, capacity: infoSize) { rebound in
                proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, rebound, Int32(infoSize))
            }
        }
        guard result == Int32(infoSize) else {
            return nil
        }

        return LlamaServerPIDRecord(
            pid: pid,
            executablePath: canonicalizedPath(String(cString: pathBuffer)),
            startTime: LlamaProcessStartTime(
                seconds: Int64(info.pbi_start_tvsec),
                microseconds: Int32(info.pbi_start_tvusec)
            )
        )
    }

    static func hyperWhisperRuntimeExecutablePaths() -> Set<String> {
        var paths: Set<String> = []

        if let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first {
            paths.insert(canonicalizedPath(
                appSupport
                    .appendingPathComponent("hyperwhisper", isDirectory: true)
                    .appendingPathComponent("runtime", isDirectory: true)
                    .appendingPathComponent(executableName)
                    .path
            ))
        }

        if let bundled = Bundle.main.url(forResource: executableName, withExtension: nil, subdirectory: "Runtime") {
            paths.insert(canonicalizedPath(bundled.path))
        }

        if let resource = Bundle.main.url(forResource: executableName, withExtension: nil) {
            paths.insert(canonicalizedPath(resource.path))
        }

        return paths
    }

    static func isKnownHyperWhisperLlamaServerPath(_ path: String) -> Bool {
        let canonicalPath = canonicalizedPath(path)
        guard URL(fileURLWithPath: canonicalPath).lastPathComponent == executableName else {
            return false
        }

        // Match only this install's exact runtime executable paths (the
        // Application Support runtime dir and this bundle's Runtime binary).
        // A substring match on "/HyperWhisper.app/Contents/Resources/Runtime/…"
        // would also match a sibling/beta HyperWhisper.app install, causing the
        // orphan sweep to SIGTERM another running copy mid-inference.
        return hyperWhisperRuntimeExecutablePaths().contains(canonicalPath)
    }

    static func isTrackedHyperWhisperLlamaServerPath(_ path: String) -> Bool {
        let canonicalPath = canonicalizedPath(path)
        guard URL(fileURLWithPath: canonicalPath).lastPathComponent == executableName else {
            return false
        }

        if hyperWhisperRuntimeExecutablePaths().contains(canonicalPath) {
            return true
        }

        // PID-file cleanup validates the exact process identity before signaling,
        // so it can still clean up a process launched before the app bundle moved.
        return canonicalPath.contains("/HyperWhisper.app/Contents/Resources/Runtime/\(executableName)")
    }

    static func allRunningProcessIDs() -> [Int32] {
        let byteCount = proc_listpids(UInt32(PROC_ALL_PIDS), 0, nil, 0)
        guard byteCount > 0 else { return [] }

        let pidCapacity = Int(byteCount) / MemoryLayout<Int32>.stride
        var pids = [Int32](repeating: 0, count: pidCapacity)
        let bytesWritten = pids.withUnsafeMutableBufferPointer { buffer in
            proc_listpids(
                UInt32(PROC_ALL_PIDS),
                0,
                buffer.baseAddress,
                Int32(buffer.count * MemoryLayout<Int32>.stride)
            )
        }
        guard bytesWritten > 0 else { return [] }

        let count = min(Int(bytesWritten) / MemoryLayout<Int32>.stride, pids.count)
        return pids.prefix(count).filter { $0 > 0 }
    }
    #endif
}
