//
//  AXCastTests.swift
//  hyperwhisperTests
//
//  Issue #743: an Accessibility reply of the wrong CF type must come back
//  nil, not trap.
//

import Testing
import Foundation
import ApplicationServices
@testable import HyperWhisper

struct AXCastTests {
    @Test func rejectsNonAXTypes() {
        let string: CFTypeRef = "AXTextField" as CFString
        let number: CFTypeRef = 42 as NSNumber
        #expect(AXCast.element(string) == nil)
        #expect(AXCast.value(string) == nil)
        #expect(AXCast.element(number) == nil)
        #expect(AXCast.value(number) == nil)
    }

    @Test func rejectsNil() {
        #expect(AXCast.element(nil) == nil)
        #expect(AXCast.value(nil) == nil)
    }

    @Test func acceptsElementOnlyAsElement() throws {
        let system = AXUIElementCreateSystemWide()
        let ref: CFTypeRef = system
        let element = try #require(AXCast.element(ref))
        #expect(CFEqual(element, system))
        #expect(AXCast.value(ref) == nil)
    }

    @Test func acceptsValueOnlyAsValue() throws {
        var range = CFRange(location: 3, length: 5)
        let created = AXValueCreate(.cfRange, &range)
        let axValue = try #require(created)
        let ref: CFTypeRef = axValue
        let value = try #require(AXCast.value(ref))
        var roundTrip = CFRange(location: 0, length: 0)
        let decoded = AXValueGetValue(value, .cfRange, &roundTrip)
        #expect(decoded)
        #expect(roundTrip.location == 3 && roundTrip.length == 5)
        #expect(AXCast.element(ref) == nil)
    }
}
