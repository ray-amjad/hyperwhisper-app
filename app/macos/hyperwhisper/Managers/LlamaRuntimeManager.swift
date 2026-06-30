//
//  LlamaRuntimeManager.swift
//  hyperwhisper
//
//  Handles locating and managing the llama.cpp HTTP server runtime used for
//  local GGUF model inference. The server binary is bundled with the app
//  and supports standard GGUF format models for AI post-processing.
//

import Foundation
import os.log

@MainActor
final class LlamaRuntimeManager {

    // MARK: - Error Definitions

    enum Error: Swift.Error, LocalizedError {
        case executableNotFound
        case runtimeMissingDependencies([String])
        case copyFailed(String)

        var errorDescription: String? {
            switch self {
            case .executableNotFound:
                return "llama.runtime.error.executableNotFound".localized
            case .runtimeMissingDependencies(let missing):
                return "llama.runtime.error.missingDependencies".localized(arguments: missing.joined(separator: ", "))
            case .copyFailed(let reason):
                return "llama.runtime.error.copyFailed".localized(arguments: reason)
            }
        }
    }

    // MARK: - Properties

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "LlamaRuntime")
    private let fileManager: FileManager

    // RUNTIME CONFIGURATION:
    // The llama-server binary name and required shared libraries. Newer official
    // macOS builds embed the Metal shader, so ggml-metal.metal is optional.
    private let runtimeExecutableName = "llama-server"
    private let requiredRuntimeArtifacts = [
        "libllama.dylib",
        "libllama.0.dylib",
        "libggml.dylib",
        "libggml.0.dylib",
        "libggml-base.dylib",
        "libggml-base.0.dylib",
        "libggml-cpu.dylib",
        "libggml-cpu.0.dylib",
        "libggml-blas.dylib",
        "libggml-blas.0.dylib",
        "libggml-metal.dylib",
        "libggml-metal.0.dylib",
        "libmtmd.dylib",
        "libmtmd.0.dylib"
    ]

    // MARK: - Initialization

    init(fileManager: FileManager = .default) {
        self.fileManager = fileManager
    }

    // MARK: - Directory Management

    /// The directory where runtime files are stored in Application Support
    var runtimeDirectory: URL {
        let support = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return support
            .appendingPathComponent("hyperwhisper", isDirectory: true)
            .appendingPathComponent("runtime", isDirectory: true)
    }

    /// The full path to the executable in Application Support
    private var executableURL: URL {
        runtimeDirectory.appendingPathComponent(runtimeExecutableName, isDirectory: false)
    }


    // MARK: - Public Interface

    /// Prepares the llama-server executable for use
    /// - Parameter override: Optional override path for testing
    /// - Returns: URL to the executable ready to be launched
    func prepareExecutable(override: URL?) async throws -> URL {
        // MIGRATION PATH:
        // 1. Check for override path (for testing)
        // 2. Check for bundled executable in Runtime directory
        // 3. Check for executable in Application Support (persisted installs)
        // 4. Copy bundled executable to Application Support if needed

        // Step 1: Check override
        if let override, fileManager.isExecutableFile(atPath: override.path) {
            logger.info("Using override executable at \(override.path, privacy: .public)")
            return override
        }

        // Step 2: Check for bundled executable
        if let bundled = locateBundledExecutable(),
           fileManager.isExecutableFile(atPath: bundled.path) {
            // Validate that the required shared libraries are present alongside
            if validateRuntime(at: bundled) {
                logger.info("Using bundled llama-server at \(bundled.path, privacy: .public)")
                return bundled
            } else {
                // Bundled runtime is incomplete, try to copy to Application Support
                logger.warning("Bundled runtime missing dependencies, installing to Application Support")
            }
        }

        // Step 3: Check Application Support for existing installation
        if fileManager.isExecutableFile(atPath: executableURL.path) {
            if validateRuntime(at: executableURL) {
                logger.info("Using cached llama-server at \(self.executableURL.path, privacy: .public)")
                return executableURL
            }
            // Remove invalid installation
            logger.warning("Cached runtime incomplete, reinstalling...")
            try? fileManager.removeItem(at: runtimeDirectory)
        }

        // Step 4: Copy bundled runtime to Application Support
        if let bundled = locateBundledExecutable() {
            try copyBundledRuntime(from: bundled)
            if validateRuntime(at: executableURL) {
                logger.info("Installed llama-server to \(self.executableURL.path, privacy: .public)")
                return executableURL
            }
            let missing = missingArtifacts(at: executableURL)
            throw Error.runtimeMissingDependencies(missing)
        }

        throw Error.executableNotFound
    }

    // MARK: - Private Methods

    /// Locates the bundled executable in the app bundle
    private func locateBundledExecutable() -> URL? {
        // Check in Runtime subdirectory first
        if let bundled = Bundle.main.url(forResource: runtimeExecutableName,
                                        withExtension: nil,
                                        subdirectory: "Runtime"),
           fileManager.isExecutableFile(atPath: bundled.path) {
            return bundled
        }

        // Fallback to root resources
        if let root = Bundle.main.url(forResource: runtimeExecutableName, withExtension: nil),
           fileManager.isExecutableFile(atPath: root.path) {
            return root
        }

        return nil
    }

    /// Copies the bundled runtime files to Application Support
    private func copyBundledRuntime(from bundledExecutable: URL) throws {
        let bundleDirectory = bundledExecutable.deletingLastPathComponent()
        let parentDirectory = runtimeDirectory.deletingLastPathComponent()

        do {
            try fileManager.createDirectory(at: parentDirectory, withIntermediateDirectories: true)
            if fileManager.fileExists(atPath: runtimeDirectory.path) {
                try fileManager.removeItem(at: runtimeDirectory)
            }
            try fileManager.copyItem(at: bundleDirectory, to: runtimeDirectory)
            try ensureExecutablePermissions(in: runtimeDirectory)
        } catch {
            throw Error.copyFailed("llama.runtime.error.copy.executable".localized(arguments: error.localizedDescription))
        }
    }

    /// Validates that all required runtime artifacts are present
    private func validateRuntime(at executable: URL) -> Bool {
        missingArtifacts(at: executable).isEmpty
    }

    private func missingArtifacts(at executable: URL) -> [String] {
        let directory = executable.deletingLastPathComponent()
        return requiredRuntimeArtifacts.filter { artifact in
            let url = directory.appendingPathComponent(artifact)
            return !fileManager.fileExists(atPath: url.path)
        }
    }

    private func ensureExecutablePermissions(in directory: URL) throws {
        let executablePath = directory.appendingPathComponent(runtimeExecutableName).path
        try fileManager.setAttributes([
            .posixPermissions: NSNumber(value: Int16(0o755))
        ], ofItemAtPath: executablePath)

        guard let enumerator = fileManager.enumerator(
            at: directory,
            includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey],
            options: [.skipsHiddenFiles]
        ) else {
            return
        }

        for case let fileURL as URL in enumerator {
            let values = try fileURL.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else {
                continue
            }
            guard fileURL.pathExtension == "dylib" || fileURL.lastPathComponent == runtimeExecutableName else {
                continue
            }
            try fileManager.setAttributes([
                .posixPermissions: NSNumber(value: Int16(0o755))
            ], ofItemAtPath: fileURL.path)
        }
    }
}
