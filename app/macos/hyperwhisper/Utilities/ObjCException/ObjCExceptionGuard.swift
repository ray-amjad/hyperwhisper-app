//
//  ObjCExceptionGuard.swift
//  hyperwhisper
//
//  The Swift face of `ObjCExceptionCatcher` (issue #2577).
//

import Foundation

/// Turns an Objective-C exception into a Swift error. Wrap only the one framework
/// call that can raise (an `installTap`), not a block of Swift: an exception that
/// unwinds through Swift frames skips their cleanup, so keep the wrapped body tiny.
enum ObjCExceptionGuard {
    /// Run `body`; throw an `NSError` in `ObjCExceptionErrorDomain` if it raised.
    static func run(_ body: () -> Void) throws {
        try ObjCExceptionCatcher.catchException(body)
    }
}
