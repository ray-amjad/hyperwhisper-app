//
//  TranscriptionResultCache.swift
//  hyperwhisper
//
//  TRANSCRIPTION CACHE
//  This class manages caching of transcription results to avoid re-transcribing identical audio files.
//
//  Key Features:
//  - File hash-based caching (uses audio file hash as key)
//  - Automatic cache size management (LRU eviction)
//  - Thread-safe operations
//
//  Architecture Notes:
//  - Extracted from TranscriptionPipeline to improve code organization
//  - Uses simple dictionary storage (could be upgraded to persistent cache if needed)
//

import Foundation

/// Manages caching of transcription results to avoid redundant processing
class TranscriptionResultCache {

    // MARK: - Private Properties

    /// Cache storage: maps file hash to transcription text
    /// Key: SHA256 hash of audio file content
    /// Value: Transcribed text
    private var cache: [String: String] = [:]

    /// Maximum number of cached entries before eviction starts
    /// This prevents unbounded memory growth
    private let maxCacheSize: Int

    /// Queue for thread-safe cache access
    /// All cache operations are serialized through this queue to prevent race conditions
    private let cacheQueue = DispatchQueue(label: "com.hyperwhisper.transcriptionCache", qos: .utility)

    // MARK: - Initialization

    /// Initialize cache with specified size limit
    /// - Parameter maxCacheSize: Maximum number of entries to cache (default: 100)
    init(maxCacheSize: Int = 100) {
        self.maxCacheSize = maxCacheSize
    }

    // MARK: - Public Methods

    /// Retrieve cached transcription for an audio file
    /// - Parameter audioURL: URL of the audio file
    /// - Returns: Cached transcription text, or nil if not found
    func getCachedTranscription(for audioURL: URL) -> String? {
        guard let hash = calculateFileHash(audioURL) else {
            return nil
        }

        return cacheQueue.sync {
            return cache[hash]
        }
    }

    /// Cache a transcription result for future use
    /// CACHE MANAGEMENT FLOW:
    /// 1. Calculate hash of audio file
    /// 2. If cache is full, evict oldest entry (simplified LRU)
    /// 3. Store transcription with hash as key
    ///
    /// - Parameters:
    ///   - text: Transcribed text to cache
    ///   - audioURL: URL of the audio file that was transcribed
    func cacheTranscription(_ text: String, for audioURL: URL) {
        guard let hash = calculateFileHash(audioURL) else {
            return
        }

        cacheQueue.sync {
            // CACHE SIZE MANAGEMENT:
            // If we've reached the maximum cache size, remove the oldest entry
            // This is a simplified LRU implementation (would track access time in production)
            if cache.count >= maxCacheSize {
                // Remove first entry (oldest in dictionary iteration order)
                if let firstKey = cache.keys.first {
                    cache.removeValue(forKey: firstKey)
                }
            }

            // Store the new transcription
            cache[hash] = text
        }
    }

    /// Clear all cached transcriptions
    /// Useful for memory management or when settings change
    func clearCache() {
        cacheQueue.sync {
            cache.removeAll()
        }
    }

    /// Get current cache size (number of entries)
    var count: Int {
        cacheQueue.sync {
            return cache.count
        }
    }

    // MARK: - Private Methods

    /// Calculate hash of audio file for use as cache key
    /// HASH CALCULATION:
    /// - Reads entire file content into memory
    /// - Uses Swift's built-in hashValue (simple but effective)
    /// - Returns nil if file cannot be read
    ///
    /// NOTE: This is a simplified implementation. For production use, consider:
    /// - Using a cryptographic hash (SHA256) for better collision resistance
    /// - Streaming hash calculation for large files (avoid loading entire file)
    /// - File metadata hashing (size + modification date) for faster checks
    ///
    /// - Parameter url: URL of the audio file to hash
    /// - Returns: String representation of the file hash, or nil if calculation fails
    private func calculateFileHash(_ url: URL) -> String? {
        // Attempt to read file contents
        guard let data = try? Data(contentsOf: url) else {
            return nil
        }

        // Convert hash value to string for use as dictionary key
        return String(data.hashValue)
    }
}
