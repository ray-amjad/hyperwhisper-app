//
//  DeviceIdentifierGenerator.swift
//  hyperwhisper
//
//  DEVICE IDENTIFIER GENERATION
//  Generates unique, stable device identifiers for license activation and trial tracking.
//
//  STRATEGY (in priority order):
//  1. Hardware Serial Number (IOKit) - Most stable, survives OS reinstalls
//  2. Stored UUID (UserDefaults) - Persists across app launches
//  3. New UUID (generated once) - Last resort fallback
//
//  SECURITY:
//  - All identifiers are hashed with SHA256 before storage/transmission
//  - Prevents reverse engineering of device information
//  - Ensures privacy while maintaining uniqueness
//
//  USAGE:
//  - Trial users: Device ID identifies them for credit allocation
//  - License activation: Device ID binds license to specific hardware
//  - HyperWhisper Cloud: Device ID used when no license present
//

import Foundation
import CryptoKit
import IOKit

/// Generates unique device identifiers for license and trial tracking
///
/// This class is responsible for creating a stable device identifier that:
/// 1. Uniquely identifies the Mac hardware
/// 2. Persists across app reinstalls (if hardware serial available)
/// 3. Remains consistent for the lifetime of the installation
/// 4. Protects user privacy through SHA256 hashing
///
/// The identifier is used for:
/// - Trial user tracking (device-based credits)
/// - License activation (binding license to hardware)
/// - HyperWhisper Cloud API calls (when no license present)
class DeviceIdentifierGenerator {

    // MARK: - Properties

    /// UserDefaults key for storing the generated device ID
    private static let deviceIdKey = "com.hyperwhisper.license.deviceId"

    /// Cached device identifier to avoid repeated generation
    /// Once generated, we use the same ID throughout the app lifecycle
    private static var cachedDeviceId: String?

    /// Serializes access to `cachedDeviceId` and the UserDefaults read/write.
    /// `generate()` is reachable off the main actor (e.g. from
    /// `LicenseNetworkService.validateLicense`, a non-isolated async method whose
    /// body runs on the cooperative pool), so the check-then-set must be atomic.
    private static let lock = NSLock()

    // MARK: - Public API

    /// Generates or retrieves the device identifier
    ///
    /// GENERATION STRATEGY:
    /// 1. Check in-memory cache (fastest)
    /// 2. Try hardware serial number (IOKit) - most stable
    /// 3. Check UserDefaults for stored UUID
    /// 4. Generate new UUID and store it (fallback)
    ///
    /// All identifiers are hashed with SHA256 for privacy
    ///
    /// - Returns: A SHA256 hash of the device identifier
    static func generate() -> String {
        // Serialize the check-then-set so concurrent first-use callers can't
        // race on cachedDeviceId or the UserDefaults write.
        lock.lock()
        defer { lock.unlock() }

        // STEP 1: Check in-memory cache
        // If we've already generated an ID this session, reuse it
        if let cached = cachedDeviceId {
            return cached
        }

        // STEP 2: Try to get hardware serial number (most stable)
        // This survives OS reinstalls and is unique per Mac
        if let hardwareId = getHardwareSerialNumber() {
            let hashed = hashString(hardwareId)
            cachedDeviceId = hashed

            // Store in UserDefaults for consistency
            UserDefaults.standard.set(hashed, forKey: deviceIdKey)

            return hashed
        }

        // STEP 3: Check if we have a stored UUID in UserDefaults
        // This persists across app launches but not OS reinstalls
        if let storedId = UserDefaults.standard.string(forKey: deviceIdKey) {
            cachedDeviceId = storedId
            return storedId
        }

        // STEP 4: Generate new UUID and store it (fallback)
        // This is used when hardware serial is unavailable (e.g., VMs)
        let newId = UUID().uuidString
        let hashed = hashString(newId)
        UserDefaults.standard.set(hashed, forKey: deviceIdKey)
        cachedDeviceId = hashed

        return hashed
    }

    /// Clears the cached device identifier
    /// Used primarily for testing or troubleshooting
    static func clearCache() {
        lock.lock()
        defer { lock.unlock() }
        cachedDeviceId = nil
    }

    // MARK: - Private Helpers

    /// Gets the hardware serial number using IOKit
    ///
    /// This method queries the IOPlatformExpertDevice to get the Mac's
    /// serial number, which is unique and stable across OS reinstalls.
    ///
    /// IOKIT PROCESS:
    /// 1. Get IOPlatformExpertDevice service
    /// 2. Query kIOPlatformSerialNumberKey property
    /// 3. Release the service handle
    /// 4. Return the serial number string
    ///
    /// - Returns: The hardware serial number, or nil if unavailable
    private static func getHardwareSerialNumber() -> String? {
        // Get the platform expert service (contains hardware info)
        let platformExpert = IOServiceGetMatchingService(
            kIOMainPortDefault,
            IOServiceMatching("IOPlatformExpertDevice")
        )

        // Ensure we got a valid service handle
        guard platformExpert != 0 else { return nil }

        // CRITICAL: Always release the service when done
        // Failing to release causes resource leaks
        defer { IOObjectRelease(platformExpert) }

        // Query the serial number property
        // This is set by the firmware and identifies the physical Mac
        if let serialNumber = IORegistryEntryCreateCFProperty(
            platformExpert,
            kIOPlatformSerialNumberKey as CFString,
            kCFAllocatorDefault,
            0
        )?.takeRetainedValue() as? String {
            return serialNumber
        }

        return nil
    }

    /// Hashes a string using SHA256 for privacy
    ///
    /// This ensures that:
    /// 1. Raw hardware identifiers are never stored or transmitted
    /// 2. The identifier remains unique (SHA256 collision probability is negligible)
    /// 3. User privacy is protected (serial numbers can't be reverse engineered)
    ///
    /// - Parameter input: The string to hash (serial number or UUID)
    /// - Returns: Hexadecimal SHA256 hash
    private static func hashString(_ input: String) -> String {
        // Convert string to data for hashing
        let data = Data(input.utf8)

        // Compute SHA256 hash
        let hashed = SHA256.hash(data: data)

        // Convert hash bytes to hexadecimal string
        // Format: "a1b2c3d4..." (64 characters for 256 bits)
        return hashed.compactMap { String(format: "%02x", $0) }.joined()
    }
}
