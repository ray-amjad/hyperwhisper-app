//
//  PathResolver.swift
//  hyperwhisper
//
//  PATH RESOLVER
//  Robust path resolution utility using URL-based operations.
//  Handles relative path calculation, symlinks, and multi-root workspaces.
//
//  FEATURES:
//  - URL-based path operations (more robust than string manipulation)
//  - Symlink resolution with standardizedFileURL
//  - Multi-root workspace support
//  - Path validation and normalization
//  - Case-insensitive filesystem handling

import Foundation

// MARK: - Path Resolver

/// Utility for robust path resolution and manipulation
public struct PathResolver {
    
    // MARK: - Public Methods
    
    /// Compute relative path from a base directory to a target file
    /// - Parameters:
    ///   - base: The base directory URL
    ///   - target: The target file URL
    /// - Returns: Relative path string, or nil if target is not within base
    public static func computeRelativePath(from base: URL, to target: URL) -> String? {
        // Standardize URLs to resolve symlinks and clean up paths
        let standardizedBase = base.standardizedFileURL
        let standardizedTarget = target.standardizedFileURL
        
        // Ensure base is a directory
        var baseURL = standardizedBase
        if !baseURL.path.hasSuffix("/") {
            baseURL = URL(fileURLWithPath: baseURL.path + "/")
        }
        
        // Get path components
        let baseComponents = baseURL.pathComponents
        let targetComponents = standardizedTarget.pathComponents
        
        // Check if target is within base
        guard targetComponents.count >= baseComponents.count else {
            return nil
        }
        
        // Verify that target starts with base path
        for i in 0..<baseComponents.count {
            // Skip volume component on macOS (e.g., "/")
            if i == 0 && baseComponents[i] == "/" && targetComponents[i] == "/" {
                continue
            }
            
            // Case-insensitive comparison for macOS (HFS+ and APFS are case-insensitive by default)
            if baseComponents[i].lowercased() != targetComponents[i].lowercased() {
                return nil
            }
        }
        
        // Extract relative components
        let relativeComponents = Array(targetComponents[baseComponents.count...])
        
        // Join components with forward slash
        let relativePath = relativeComponents.joined(separator: "/")
        
        return relativePath.isEmpty ? "." : relativePath
    }
    
    /// Find the best matching root directory for a file from multiple roots
    /// Uses longest-prefix matching to handle nested project structures
    /// - Parameters:
    ///   - filePath: The file path to match
    ///   - roots: Array of potential root directories
    /// - Returns: The best matching root and its relative path, or nil
    public static func findBestMatchingRoot(
        for filePath: String,
        from roots: [String]
    ) -> (root: String, relativePath: String)? {
        let fileURL = URL(fileURLWithPath: filePath).standardizedFileURL
        
        var bestMatch: (root: String, relativePath: String, depth: Int)?
        
        for root in roots {
            let rootURL = URL(fileURLWithPath: root).standardizedFileURL
            
            if let relativePath = computeRelativePath(from: rootURL, to: fileURL) {
                // Count depth (number of path components) for longest-prefix matching
                let depth = rootURL.pathComponents.count
                
                // Update best match if this root is deeper (more specific)
                if bestMatch == nil || depth > bestMatch!.depth {
                    bestMatch = (root, relativePath, depth)
                }
            }
        }
        
        // Return best match without depth info
        return bestMatch.map { ($0.root, $0.relativePath) }
    }
    
    /// Normalize a path for consistent comparison
    /// - Parameter path: The path to normalize
    /// - Returns: Normalized path string
    public static func normalizePath(_ path: String) -> String {
        let url = URL(fileURLWithPath: path).standardizedFileURL
        var normalized = url.path
        
        // Remove trailing slash unless it's the root
        if normalized.count > 1 && normalized.hasSuffix("/") {
            normalized = String(normalized.dropLast())
        }
        
        return normalized
    }
    
    /// Check if a file path is within a directory
    /// - Parameters:
    ///   - filePath: The file path to check
    ///   - directory: The directory path
    /// - Returns: true if file is within directory
    public static func isPath(_ filePath: String, within directory: String) -> Bool {
        let fileURL = URL(fileURLWithPath: filePath).standardizedFileURL
        let dirURL = URL(fileURLWithPath: directory).standardizedFileURL
        
        return computeRelativePath(from: dirURL, to: fileURL) != nil
    }
    
    /// Resolve a potentially relative path against a base directory
    /// - Parameters:
    ///   - path: The path to resolve (may be relative or absolute)
    ///   - base: The base directory for relative paths
    /// - Returns: Absolute path
    public static func resolvePath(_ path: String, relativeTo base: String) -> String {
        if path.hasPrefix("/") {
            // Already absolute
            return path
        }
        
        let baseURL = URL(fileURLWithPath: base)
        let resolvedURL = baseURL.appendingPathComponent(path).standardizedFileURL
        return resolvedURL.path
    }
    
    /// Get common ancestor directory for multiple paths
    /// Useful for finding the project root from multiple file paths
    /// - Parameter paths: Array of file paths
    /// - Returns: Common ancestor directory, or nil if no common ancestor
    public static func findCommonAncestor(of paths: [String]) -> String? {
        guard !paths.isEmpty else { return nil }
        guard paths.count > 1 else { return URL(fileURLWithPath: paths[0]).deletingLastPathComponent().path }
        
        // Get all path components for each path
        let allComponents = paths.map { path in
            URL(fileURLWithPath: path).standardizedFileURL.pathComponents
        }
        
        // Find minimum component count
        let minCount = allComponents.map { $0.count }.min() ?? 0
        
        var commonComponents: [String] = []
        
        // Compare components at each level
        for i in 0..<minCount {
            let component = allComponents[0][i]
            
            // Check if all paths have the same component at this level
            let allMatch = allComponents.allSatisfy { components in
                components[i].lowercased() == component.lowercased()
            }
            
            if allMatch {
                commonComponents.append(component)
            } else {
                break
            }
        }
        
        // Build common path
        guard !commonComponents.isEmpty else { return nil }
        
        if commonComponents.count == 1 && commonComponents[0] == "/" {
            return "/"
        }
        
        return "/" + commonComponents.dropFirst().joined(separator: "/")
    }
    
    /// Check if a path exists and is a directory
    /// - Parameter path: The path to check
    /// - Returns: true if path exists and is a directory
    public static func isDirectory(_ path: String) -> Bool {
        var isDir: ObjCBool = false
        let exists = FileManager.default.fileExists(atPath: path, isDirectory: &isDir)
        return exists && isDir.boolValue
    }
    
    /// Check if a path exists and is a file
    /// - Parameter path: The path to check
    /// - Returns: true if path exists and is a file
    public static func isFile(_ path: String) -> Bool {
        var isDir: ObjCBool = false
        let exists = FileManager.default.fileExists(atPath: path, isDirectory: &isDir)
        return exists && !isDir.boolValue
    }
}

// MARK: - Multi-Root Workspace Support

extension PathResolver {
    
    /// Configuration for multi-root workspace resolution
    public struct MultiRootConfig {
        /// All configured project roots
        public let roots: [String]
        
        /// Primary/active root (if known)
        public let primaryRoot: String?
        
        /// Whether to prefer the primary root when multiple matches exist
        public let preferPrimaryRoot: Bool
        
        public init(
            roots: [String],
            primaryRoot: String? = nil,
            preferPrimaryRoot: Bool = true
        ) {
            self.roots = roots
            self.primaryRoot = primaryRoot
            self.preferPrimaryRoot = preferPrimaryRoot
        }
    }
    
    /// Resolve a file path in a multi-root workspace
    /// - Parameters:
    ///   - filePath: The file path to resolve
    ///   - config: Multi-root configuration
    /// - Returns: Tuple of (root, relativePath) or nil
    public static func resolveInMultiRootWorkspace(
        filePath: String,
        config: MultiRootConfig
    ) -> (root: String, relativePath: String)? {
        // Try primary root first if configured
        if let primaryRoot = config.primaryRoot,
           config.preferPrimaryRoot,
           let relativePath = computeRelativePath(
               from: URL(fileURLWithPath: primaryRoot),
               to: URL(fileURLWithPath: filePath)
           ) {
            return (primaryRoot, relativePath)
        }
        
        // Fall back to finding best match from all roots
        return findBestMatchingRoot(for: filePath, from: config.roots)
    }
}