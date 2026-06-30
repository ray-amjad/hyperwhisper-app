// swift-tools-version: 5.9
// The swift-tools-version declares the minimum version of Swift required to build this package.
//
// PACKAGE MANIFEST
// This file defines all external dependencies for HyperWhisper.
// Swift Package Manager (SPM) will download and manage these dependencies automatically.
//
// Key Dependencies:
// - WhisperKit: Local on-device speech recognition using OpenAI's Whisper model
// - HotKey: Global keyboard shortcuts that work system-wide
// - KeychainAccess: Secure storage for API keys and sensitive data

import PackageDescription

let package = Package(
    // Package name - this should match your app name
    name: "hyperwhisper",
    
    // Platform requirements
    // macOS 14.0+ is required for latest SwiftUI features and WhisperKit compatibility
    platforms: [
        .macOS(.v14)
    ],
    
    // Products define the executables and libraries produced by this package
    products: [
        // Library product for use in the app
        .library(
            name: "HyperWhisperKit",
            targets: ["HyperWhisperKit"]
        ),
    ],
    
    // Dependencies declare other packages that this package depends on
    dependencies: [
        // MARK: - HotKey
        // Global keyboard shortcuts for macOS
        // Allows the app to respond to keyboard shortcuts even when not in focus
        // Repository: https://github.com/soffes/HotKey
        .package(
            url: "https://github.com/soffes/HotKey.git",
            from: "0.2.0"
        ),
        
        
        // MARK: - AsyncHTTPClient (Optional)
        // For making HTTP requests to OpenAI API
        // Part of Swift Server ecosystem
        .package(
            url: "https://github.com/swift-server/async-http-client.git",
            from: "1.19.0"
        ),

        // MARK: - ZIPFoundation
        // ZIP archive handling for downloading and extracting Whisper models
        // Repository: https://github.com/weichsel/ZIPFoundation
        .package(
            url: "https://github.com/weichsel/ZIPFoundation.git",
            from: "0.9.19"
        ),

        // MARK: - Sentry (Crash/Error Reporting)
        // Repository: https://github.com/getsentry/sentry-cocoa
        // Integrated optionally; code guards with `#if canImport(Sentry)`
        .package(
            url: "https://github.com/getsentry/sentry-cocoa.git",
            from: "8.55.0"
        ),

        // MARK: - Swift Atomics
        // Low-level atomic operations for thread-safe concurrent programming
        // Used for lock-free continuation guards in async/await bridging code
        // Repository: https://github.com/apple/swift-atomics
        .package(
            url: "https://github.com/apple/swift-atomics.git",
            from: "1.2.0"
        ),

        // MARK: - FlyingFox
        // Lightweight async/await native HTTP server. Powers the in-app
        // Local API Server (Settings → API Server).
        // Repository: https://github.com/swhitty/FlyingFox
        .package(
            url: "https://github.com/swhitty/FlyingFox.git",
            from: "0.21.0"
        ),
    ],
    
    // Targets are the basic building blocks of a package
    // A target can define a module or a test suite
    targets: [
        // MARK: - Main Library Target
        // This target contains the core functionality that can be imported
        .target(
            name: "HyperWhisperKit",
            dependencies: [
                // Link HotKey for global shortcuts
                .product(name: "HotKey", package: "HotKey"),
                
                // Link AsyncHTTPClient for API calls
                .product(name: "AsyncHTTPClient", package: "async-http-client"),

                // Link ZIPFoundation for unzip support
                .product(name: "ZIPFoundation", package: "ZIPFoundation"),

                // Link Sentry (optional) for error reporting
                .product(name: "Sentry", package: "sentry-cocoa"),

                // Link Atomics for thread-safe continuation guards
                .product(name: "Atomics", package: "swift-atomics"),

                // Link FlyingFox for the in-app Local API HTTP server
                .product(name: "FlyingFox", package: "FlyingFox"),
            ],
            
            // Path to source files (if not in default Sources/ directory)
            path: "Sources/HyperWhisperKit",
            
            // Resources to be bundled with the package
            resources: [
                // Copy any resource files needed
                // .process("Resources")
            ],
            
            // Swift compiler settings
            swiftSettings: [
                // Enable strict concurrency checking
                .enableExperimentalFeature("StrictConcurrency"),
                
                // Define compile-time flags
                .define("DEBUG", .when(configuration: .debug)),
                
                // Enable upcoming Swift features
                .enableUpcomingFeature("BareSlashRegexLiterals"),
            ]
        ),
        
        // MARK: - Test Target
        // Unit tests for the library
        .testTarget(
            name: "HyperWhisperKitTests",
            dependencies: ["HyperWhisperKit"],
            path: "Tests/HyperWhisperKitTests"
        ),
    ],
    
    // Swift language version
    swiftLanguageVersions: [.v5]
)

// MARK: - Configuration Notes

/*
 INTEGRATION GUIDE:
 
 1. Add this package to your Xcode project:
    - File > Add Package Dependencies
    - Add local package from this directory
 
 2. Import in your Swift files:
    ```swift
    import WhisperKit
    import HotKey
    ```
 
 3. WhisperKit Usage:
    ```swift
    // Initialize WhisperKit
    let whisperKit = try await WhisperKit()
    
    // Transcribe audio
    let result = try await whisperKit.transcribe(audioPath: audioURL)
    ```
 
 4. HotKey Usage:
    ```swift
    // Create a global hotkey
    let hotKey = HotKey(key: .space, modifiers: .command)
    hotKey.keyDownHandler = {
        // Handle hotkey press
    }
    ```
 
 TROUBLESHOOTING:
 
 - If WhisperKit fails to download models, ensure you have enough disk space
 - HotKey requires accessibility permissions in System Preferences
 
 PERFORMANCE CONSIDERATIONS:
 
 - WhisperKit models range from 39MB (tiny) to 1.5GB (large)
 - Larger models provide better accuracy but slower transcription
 - Consider downloading models in background and showing progress
 
 SECURITY NOTES:
 
 - Never commit API keys to source control
 - Use secure storage for all sensitive data
 - Enable App Sandbox and request only necessary entitlements
 */
