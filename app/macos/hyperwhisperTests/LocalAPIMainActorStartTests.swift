//
//  LocalAPIMainActorStartTests.swift
//  hyperwhisperTests
//
//  The Local API must never read the Keychain on the main actor (issue #655).
//
//  `LocalAPIAuth.loadOrCreateToken()` ends in `SecItemCopyMatching`, which
//  blocks indefinitely behind the consent panel macOS raises when the Keychain
//  item's ACL does not match the running binary's signature — after a rebuild,
//  a re-sign, or a migration between Developer ID and a development identity.
//  `LocalAPIServer.start()` called it inline, and `start()` is called from
//  `bootstrapAppServices()` *before* `setupGlobalHotkeys()`, so with the Local
//  API toggle on the whole app froze at launch with no window and no hotkeys.
//
//  None of that is reachable from this target. `start()` binds a real socket,
//  the Keychain in a test host is not the Keychain that raises the panel, and
//  the panel itself is a WindowServer affordance no unit test can drive. What
//  IS checkable is the wiring that keeps the blocking call off the main actor,
//  and the ordering the fix depends on — so these read the production source,
//  the last resort documented in `ProductionSource`.
//
//  Read the three as one statement: the blocking read is never called directly
//  (1), the token is still set before any socket is bound (2), and a start
//  still waiting on its token cannot bind after the user switched the toggle
//  off (3).
//
//  What no test at this seam can prove: that the app actually finishes
//  bootstrap while the consent panel is up. That needs a real Mac with a
//  mismatched ACL — see the PR body.
//

import Foundation
import Testing
@testable import HyperWhisper

struct LocalAPIMainActorStartTests {

    /// Nothing in the Local API calls the blocking Keychain read directly.
    ///
    /// The ban is on the *synchronous* entry points. `LocalAPIAuth.swift` is
    /// exempt because it declares them and wraps them; every other file in the
    /// tree must go through the `OffMainActor` wrappers, which hop the call
    /// onto a detached task before `SecItemCopyMatching` runs.
    ///
    /// The needles include the open paren deliberately:
    /// `LocalAPIAuth.loadOrCreateToken` is a prefix of
    /// `LocalAPIAuth.loadOrCreateTokenOffMainActor`, so a paren-less needle
    /// would flag the fix itself.
    @Test func theLocalApiNeverReadsTheKeychainOnTheMainActor() throws {
        let directory = Self.repoRoot.appendingPathComponent("app/macos/hyperwhisper/Managers/LocalAPI")
        let files = try Self.swiftFiles(under: directory)
        #expect(files.count >= 12, "the Local API source tree was not found where this test expects it")

        var offenders: [String] = []
        for file in files where file.lastPathComponent != "LocalAPIAuth.swift" {
            let source = try Self.contents(of: file)
            for (offset, line) in source.components(separatedBy: .newlines).enumerated() {
                // Prose about the ban is not a violation of it.
                guard !line.trimmingCharacters(in: .whitespaces).hasPrefix("//") else { continue }
                guard line.contains("LocalAPIAuth.loadOrCreateToken(")
                        || line.contains("LocalAPIAuth.regenerateToken(") else { continue }
                offenders.append("\(file.lastPathComponent):\(offset + 1)")
            }
        }

        #expect(offenders.isEmpty, """
            Blocking Keychain read on the main actor at \(offenders.joined(separator: ", ")). \
            Call LocalAPIAuth.loadOrCreateTokenOffMainActor() or \
            LocalAPIAuth.regenerateTokenOffMainActor() and await it instead — that is what keeps \
            SecItemCopyMatching off the main actor from issue #655. Do not exempt the file.
            """)
    }

    /// The token is still assigned before any socket is bound.
    ///
    /// This is the invariant the split had to preserve: moving the read off the
    /// main actor must not let the server start answering requests with an
    /// empty `bearerToken`, which would fail every authorized route. `start()`
    /// assigns and then calls `bindAndRun()`; `bindAndRun()` constructs the
    /// `HTTPServer` and never touches the token.
    @Test func theTokenIsAssignedBeforeAnySocketIsBound() throws {
        let prologue = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func start(",
            to: "private func bindAndRun"
        )
        #expect(
            prologue.contains("self.bearerToken = token"),
            "start() must assign the bearer token before it hands off to the bind step"
        )
        #expect(
            !prologue.contains("HTTPServer("),
            "start() must not bind a socket — that belongs in bindAndRun(), after the token arrives"
        )

        let bindStep = try ProductionSource.slice(
            of: Self.serverPath,
            from: "private func bindAndRun",
            to: "func stop("
        )
        #expect(
            bindStep.contains("HTTPServer("),
            "bindAndRun() is the step that constructs the HTTPServer"
        )
        #expect(
            !bindStep.contains("bearerToken ="),
            "bindAndRun() must not load or assign the token — it runs with the token already in hand"
        )
    }

    /// A start still waiting on its token is orphaned by `stop()`.
    ///
    /// Such a start has no `server` yet, so `stop()`'s own `guard server != nil`
    /// would return before clearing anything and the pending bind would land
    /// after the user switched the toggle off. Clearing `pendingStartID` has to
    /// come first, which is an ordering only the source shows.
    @Test func aStartWaitingOnItsTokenIsCancelledByStop() throws {
        let body = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func stop(",
            to: "func restart("
        )

        guard let orphan = body.range(of: "pendingStartID = nil") else {
            Issue.record("stop() must clear pendingStartID so an in-flight start cannot bind (issue #655)")
            return
        }
        guard let earlyReturn = body.range(of: "guard server != nil") else {
            Issue.record("stop()'s early return was renamed — update this anchor rather than deleting the check")
            return
        }

        #expect(
            orphan.lowerBound < earlyReturn.lowerBound,
            """
            stop() clears pendingStartID after its `guard server != nil` early return. A start still \
            waiting on the Keychain has no server yet, so the guard returns first and the orphan \
            never happens — the socket then binds after the user turned the Local API off (issue #655).
            """
        )
    }
}

// MARK: - Production-source fixtures

// The `#filePath` walk, the file read and the anchor slicing live in
// `ProductionSource`, shared with `LocalAPIBodyLimitTests` and
// `LocalAPIFilePathCapTests`. What is left here is only which paths and anchors
// name the regions of *this* file's subject.

extension LocalAPIMainActorStartTests {

    fileprivate static let serverPath =
        "app/macos/hyperwhisper/Managers/LocalAPI/LocalAPIServer.swift"

    fileprivate static var repoRoot: URL { ProductionSource.repoRoot }

    fileprivate static func contents(of url: URL) throws -> String {
        try ProductionSource.text(of: url)
    }

    fileprivate static func swiftFiles(under directory: URL) throws -> [URL] {
        try ProductionSource.swiftFiles(under: directory)
    }
}
