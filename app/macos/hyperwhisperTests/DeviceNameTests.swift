//
//  DeviceNameTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #313: `PromptBuilder` used to fill the prompt's
//  `<COMPUTER>` field with `Host.current().name`, a blocking `.local` mDNS
//  resolve that cost ~35 s of MainActor time PER CALL inside the signed app and
//  ran twice per post-processing request.
//
//  There is deliberately NO timing assertion here. The stall only reproduces
//  inside the signed app bundle — from a test binary the old call returned in
//  ~4 ms, which is exactly why this shipped. A wall-clock #expect would have
//  been green on `main` before the fix and flaky on CI after it.
//
//  What these tests CAN prove, honestly, is the structural property that makes
//  the stall impossible:
//
//  1. `DeviceName.current` is the friendly computer name straight out of the
//     system configuration store — not a DNS/Bonjour name, so nothing resolved
//     it.
//  2. It is cached: every read is the same value.
//  3. `PromptBuilder` actually uses it. This is the one that fails if someone
//     puts `Host.current().name` back at that line — the old call produced
//     `<COMPUTER>host.local</COMPUTER>`, which is not `DeviceName.current`.
//

import CoreData
import Foundation
import SystemConfiguration
import Testing

@testable import HyperWhisper

@MainActor
struct DeviceNameTests {

    // MARK: - The cached value itself

    @Test func resolvesToTheSystemComputerNameWithoutResolvingAHost() {
        let name = DeviceName.current
        #expect(!name.isEmpty)

        // Independent oracle: read the dynamic store here, in the test, rather
        // than trusting the production helper's own copy of the answer.
        let expected = (SCDynamicStoreCopyComputerName(nil, nil) as String?)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if let expected, !expected.isEmpty {
            #expect(name == expected)
        } else {
            #expect(name == "Unknown")
        }
    }

    /// `Host.current().name` / `ProcessInfo.processInfo.hostName` return the
    /// Bonjour form, which on macOS always carries the `.local` suffix. The
    /// friendly name never does. This is the cheapest signal that the value did
    /// not come back from a name resolution.
    @Test func isNotADotLocalBonjourName() {
        #expect(!DeviceName.current.hasSuffix(".local"))
    }

    /// Cached, not re-resolved. Repeated reads must be byte-identical — a
    /// per-call lookup would still usually agree, but this pins the contract the
    /// call sites depend on (`makeContext` reads it twice per request).
    @Test func repeatedReadsAreIdentical() {
        let first = DeviceName.current
        for _ in 0..<1_000 {
            #expect(DeviceName.current == first)
        }
    }

    // MARK: - The call site that actually stalled

    /// The real regression guard. `PromptBuilder.systemInfo` renders
    /// `<COMPUTER>…</COMPUTER>` from `makeContext`'s `computerName`. Asserting
    /// the rendered value IS `DeviceName.current` fails the moment that line
    /// goes back to any `NSHost`/`hostName` accessor, because those spell the
    /// same machine differently (`foo.local`, not `Foo`).
    @Test func promptBuilderRendersTheCachedName() throws {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext

        let mode = Mode(context: context)
        mode.id = UUID()
        mode.name = "device-name-test"
        mode.preset = "hyper"

        // `ApplicationContext.none` keeps this off the accessibility APIs — the
        // same context the Local API's /post-process endpoint pins.
        let info = PromptBuilder.systemInfo(
            for: mode,
            vocabulary: [],
            applicationContext: ApplicationContext.none
        )

        #expect(info.contains("<COMPUTER>\(DeviceName.current)</COMPUTER>"))

        context.delete(mode)
    }
}
