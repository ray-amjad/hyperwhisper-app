//
//  HttpResponse+RetryAfter.swift
//  hyperwhisper
//
//  Shared `Retry-After` header parsing, used by both the unified
//  `RustRetry` loop and the bespoke per-provider poll loops that don't go
//  through it (Soniox, AssemblyAI).
//

import Foundation

extension HttpResponse {

    /// Parse the integer `Retry-After` header (case-insensitive).
    var retryAfterSeconds: Int? {
        guard let value = headers.first(where: {
            $0.name.caseInsensitiveCompare("Retry-After") == .orderedSame
        })?.value else { return nil }
        return Int(value.trimmingCharacters(in: .whitespaces))
    }
}
