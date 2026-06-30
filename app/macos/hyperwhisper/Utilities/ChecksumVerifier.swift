//
//  ChecksumVerifier.swift
//  hyperwhisper
//
//  Provides SHA256 checksum calculation and verification for downloaded model files.
//  Extracted from model managers to eliminate code duplication.
//
//  USAGE:
//  - Calculate checksum: try ChecksumVerifier.sha256(of: fileURL)
//  - Verify file: ChecksumVerifier.verify(file: fileURL, expectedChecksum: "abc123...")
//
//  PERFORMANCE:
//  - Reads files in 4MB chunks to minimize memory usage
//  - Uses FileHandle for efficient streaming
//  - Runs in autoreleasepool to manage temporary objects
//
//  THREAD SAFETY:
//  - All methods are nonisolated and can be called from any thread
//  - Suitable for background download verification tasks
//

import Foundation
import CryptoKit

/// Utilities for SHA256 checksum calculation and verification
enum ChecksumVerifier {

    // MARK: - Public API

    /// Calculate SHA256 checksum for a file
    /// - Parameter url: URL of the file to checksum
    /// - Returns: Hex string representation of the SHA256 hash
    /// - Throws: File I/O errors if the file cannot be read
    static func sha256(of url: URL) throws -> String {
        // CHECKSUM CALCULATION FLOW:
        // 1. Open file handle for reading
        // 2. Read file in 4MB chunks (balances memory vs performance)
        // 3. Update SHA256 hasher with each chunk
        // 4. Finalize and convert to hex string

        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }

        var hasher = SHA256()
        let chunkSize = 4 * 1024 * 1024 // 4MB chunks

        // AUTORELEASEPOOL OPTIMIZATION:
        // Reading large files creates many temporary Data objects.
        // Wrapping in autoreleasepool ensures they're freed promptly
        // instead of accumulating until the next drain.
        while autoreleasepool(invoking: {
            let data = handle.readData(ofLength: chunkSize)

            // Empty data signals EOF
            guard !data.isEmpty else { return false }

            hasher.update(data: data)
            return true // Continue reading
        }) {
            // Loop continues while closure returns true
        }

        // DIGEST CONVERSION:
        // SHA256.finalize() returns a digest of 32 bytes.
        // We convert each byte to a 2-character hex string (e.g., "a3", "0f")
        // and join them into a single 64-character lowercase hex string.
        let digest = hasher.finalize()
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    /// Verify that a file's checksum matches an expected value
    /// - Parameters:
    ///   - file: URL of the file to verify
    ///   - expectedChecksum: Expected SHA256 checksum (case-insensitive)
    /// - Returns: true if checksums match, false if they differ or calculation fails
    static func verify(file: URL, expectedChecksum: String) -> Bool {
        // VERIFICATION FLOW:
        // 1. Calculate actual checksum
        // 2. Normalize both strings (lowercase, trim whitespace)
        // 3. Compare for equality

        guard let actualChecksum = try? sha256(of: file) else {
            // Checksum calculation failed (file not readable, etc.)
            return false
        }

        // NORMALIZATION:
        // Accept checksums with varying case and whitespace
        // "ABC123" == "abc123" == " abc123 "
        let normalizedExpected = expectedChecksum.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let normalizedActual = actualChecksum.lowercased()

        return normalizedExpected == normalizedActual
    }

    /// Verify that a file's checksum matches, with detailed error information
    /// - Parameters:
    ///   - file: URL of the file to verify
    ///   - expectedChecksum: Expected SHA256 checksum (case-insensitive)
    /// - Returns: Result containing true on match, or error with details on mismatch
    static func verifyWithResult(file: URL, expectedChecksum: String) -> Result<Bool, ChecksumError> {
        // DETAILED VERIFICATION:
        // Similar to verify() but returns specific error information
        // for better logging and error messages

        let actualChecksum: String
        do {
            actualChecksum = try sha256(of: file)
        } catch {
            return .failure(.readError(error))
        }

        let normalizedExpected = expectedChecksum.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let normalizedActual = actualChecksum.lowercased()

        guard normalizedExpected == normalizedActual else {
            return .failure(.mismatch(
                expected: normalizedExpected,
                actual: normalizedActual
            ))
        }

        return .success(true)
    }
}

// MARK: - Error Types

/// Errors that can occur during checksum verification
enum ChecksumError: Error, LocalizedError {
    case readError(Error)
    case mismatch(expected: String, actual: String)

    var errorDescription: String? {
        switch self {
        case .readError(let error):
            return "Failed to read file for checksum: \(error.localizedDescription)"
        case .mismatch(let expected, let actual):
            return "Checksum mismatch. Expected: \(expected), Actual: \(actual)"
        }
    }
}
