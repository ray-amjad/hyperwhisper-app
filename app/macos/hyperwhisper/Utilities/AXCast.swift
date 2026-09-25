//
//  AXCast.swift
//  hyperwhisper
//
//  Type-checked conversion of Accessibility `CFTypeRef` replies (issue #743).
//  The other app chooses the concrete type of an attribute value, so `as!`
//  traps on a wrong type, and `as?` to a CoreFoundation type always succeeds.
//  Only a type-id match makes the conversion safe.
//

import ApplicationServices

enum AXCast {
    /// Returns the value as an `AXUIElement`, or nil when it is nil or another CF type.
    static func element(_ ref: CFTypeRef?) -> AXUIElement? {
        guard let ref, CFGetTypeID(ref) == AXUIElementGetTypeID() else { return nil }
        return unsafeBitCast(ref, to: AXUIElement.self)
    }

    /// Returns the value as an `AXValue`, or nil when it is nil or another CF type.
    static func value(_ ref: CFTypeRef?) -> AXValue? {
        guard let ref, CFGetTypeID(ref) == AXValueGetTypeID() else { return nil }
        return unsafeBitCast(ref, to: AXValue.self)
    }
}
