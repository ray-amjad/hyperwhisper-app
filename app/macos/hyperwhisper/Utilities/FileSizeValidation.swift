//
//  FileSizeValidation.swift
//  hyperwhisper
//
//  Lightweight file-size helper used by cloud providers for pre-upload validation.

import Foundation

extension URL {
    /// Returns file size in bytes via filesystem metadata — O(1), no file read.
    func fileSize() throws -> Int64 {
        let attrs = try FileManager.default.attributesOfItem(atPath: self.path)
        guard let size = attrs[.size] as? Int64 else {
            throw CocoaError(.fileReadUnknown)
        }
        return size
    }
}
