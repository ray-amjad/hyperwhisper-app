//
//  AudioMimeTypeResolver.swift
//  hyperwhisper
//
//  Centralized helper for inferring audio MIME types from file URLs.
//

import Foundation

#if canImport(UniformTypeIdentifiers)
import UniformTypeIdentifiers
#endif

enum AudioMimeTypeResolver {
    private static let extensionMap: [String: String] = [
        "m4a": "audio/mp4",
        "mp4": "audio/mp4",
        "mp3": "audio/mpeg",
        "mpeg": "audio/mpeg",
        "mpga": "audio/mpeg",
        "wav": "audio/wav",
        "ogg": "audio/ogg",
        "oga": "audio/ogg",
        "opus": "audio/opus",
        "flac": "audio/flac",
        "webm": "audio/webm",
        "aac": "audio/aac",
        "caf": "audio/x-caf",
        "aif": "audio/aiff",
        "aiff": "audio/aiff",
        "aifc": "audio/aiff",
        "amr": "audio/amr"
    ]

    static func infer(for url: URL, fallback: String = "audio/mp4", overrides: [String: String] = [:]) -> String {
        let ext = url.pathExtension.lowercased()

        if let override = overrides[ext] {
            return override
        }

        #if canImport(UniformTypeIdentifiers)
        if #available(macOS 11.0, *) {
            if let typeIdentifier = try? url.resourceValues(forKeys: [.contentTypeKey]).contentType,
               let mime = typeIdentifier.preferredMIMEType {
                return mime
            }
        }
        #endif

        if let mapped = extensionMap[ext] {
            return mapped
        }

        return fallback
    }

    static func infer(fromFileName fileName: String, fallback: String = "audio/mp4", overrides: [String: String] = [:]) -> String {
        let ext = (fileName as NSString).pathExtension.lowercased()
        if ext.isEmpty {
            return fallback
        }
        if let override = overrides[ext] {
            return override
        }
        if let mapped = extensionMap[ext] {
            return mapped
        }
        return fallback
    }
}
