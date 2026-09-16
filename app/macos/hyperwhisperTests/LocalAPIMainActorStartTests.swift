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
//  and the orderings the fix depends on — so most of these read the production
//  source, the last resort documented in `ProductionSource`. The one predicate
//  that could be lifted into a pure function was, and is tested by calling it.
//
//  Read them as one statement about the window the fix opened. The blocking
//  read is never called directly (1). The token is still assigned before any
//  socket is bound (2), and the continuation that assigns it is gated on its
//  OWN identity, not on a flag (3), so a newer start cannot be bound by an
//  older one's token. Nothing else may run a Keychain token operation beside
//  those (4, 5) — one item, one chain — and a regeneration retires the old
//  credential before it waits rather than after (5). And every hook that used
//  to read `server == nil` as "not starting" now asks the one predicate that
//  knows better (6, 7, 8), including `stop()`, which must reach
//  `deletePortFile()` for a start that never got a socket (9).
//
//  What no test at this seam can prove: that the app actually finishes
//  bootstrap while the consent panel is up. That needs a real Mac with a
//  mismatched ACL — see the PR body.
//

import Foundation
import Testing
@testable import HyperWhisper

struct LocalAPIMainActorStartTests {

    // MARK: - 1. The blocking read is never called directly

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

    // MARK: - 2-3. The token arrives before the socket, for the right start

    /// The token is still assigned before any socket is bound.
    ///
    /// This is the invariant the split had to preserve: moving the read off the
    /// main actor must not let the server start answering requests with an
    /// empty `bearerToken`, which would fail every authorized route. `start()`
    /// assigns and *then* calls `bindAndRun()`; `bindAndRun()` constructs the
    /// `HTTPServer` and never touches the token.
    ///
    /// The claim is positional, because the membership version of it was not a
    /// claim at all: swapping the two statements so `start()` binds first and
    /// assigns afterwards leaves both lines present, in the same slice, and the
    /// ordering this test is named for untested.
    @Test func theTokenIsAssignedBeforeAnySocketIsBound() throws {
        let prologue = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func start(",
            to: "private func bindAndRun"
        )

        guard let assignment = prologue.range(of: "self.bearerToken = token") else {
            Issue.record("start() must assign the bearer token before it hands off to the bind step (issue #655)")
            return
        }
        guard let handoff = prologue.range(of: "self.bindAndRun()") else {
            Issue.record("start()'s hand-off to the bind step was renamed — update this anchor rather than deleting the check")
            return
        }
        #expect(
            assignment.lowerBound < handoff.lowerBound,
            """
            start() hands off to bindAndRun() BEFORE it assigns the bearer token. The socket would \
            then accept connections while `bearerToken` is still the empty string, and \
            hw_localapi::authorize denies every request against an empty expected token — so every \
            authorized route would 401 until the assignment landed.
            """
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

    /// The continuation checks its own identity, not merely that one exists.
    ///
    /// `restart()` is `stop(); start()`, so two starts can have their token
    /// loads in flight in sequence. A `pendingStartID != nil` test — or no test
    /// at all — lets the OLDER load see the NEWER start's marker, clear it, and
    /// bind with a token the newer start never asked for. That is the
    /// flag-instead-of-identity bug the field on `pendingStartID` warns about,
    /// and until this test existed, weakening the comparison to `!= nil` or
    /// deleting it left every test in this file green.
    @Test func theStartContinuationIsGuardedByItsOwnIdentity() throws {
        let prologue = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func start(",
            to: "private func bindAndRun"
        )

        guard let mint = prologue.range(of: "pendingStartID = id") else {
            Issue.record("start() must record the identity of the start whose token load is in flight (issue #655)")
            return
        }
        guard let check = prologue.range(of: "self.pendingStartID == id") else {
            Issue.record("""
                start()'s continuation must re-read pendingStartID and compare it to its OWN id. A \
                `!= nil` check, or no check, lets an older start's token load bind on behalf of a \
                newer one — see the comment on the pendingStartID field (issue #655).
                """)
            return
        }
        #expect(
            mint.lowerBound < check.lowerBound,
            "start() must mint the identity before the continuation compares against it"
        )
    }

    // MARK: - 4-5. One Keychain item, one chain

    /// Every Keychain token operation is queued on the one chain.
    ///
    /// `loadOrCreateToken()` and `regenerateToken()` are read-modify-write
    /// sequences over a single Keychain item, and `offMainActor` promises no
    /// serialization — its own doc says so. Two overlapping runs race, and the
    /// loser's `SecItemAdd` returns `errSecDuplicateItem`, which
    /// `loadOrCreateToken` logs and swallows: it still returns the token it
    /// generated, so the caller publishes a token the Keychain does not hold.
    /// Dropping either call back to a bare `Task { }` restores exactly that.
    @Test func everyKeychainTokenOperationIsQueuedOnOneChain() throws {
        let startBody = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func start(",
            to: "private func bindAndRun"
        )
        let regenerateBody = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func regenerateBearerToken(",
            to: "func handleSystemWillSleep("
        )

        #expect(
            startBody.contains("enqueueTokenWork {"),
            "start() must queue its token load on the shared chain, not fire it independently"
        )
        #expect(
            regenerateBody.contains("enqueueTokenWork {"),
            "regenerateBearerToken() must queue its Keychain work on the shared chain"
        )
        #expect(
            !startBody.contains("Task {"),
            """
            start() opens a bare Task for its token load. That is the unserialized shape: it can run \
            its Keychain read while a regeneration is inside its own delete+read+write over the same \
            item. Route it through enqueueTokenWork (issue #655).
            """
        )
        #expect(
            !regenerateBody.contains("Task {"),
            """
            regenerateBearerToken() opens a bare Task. Two rapid clicks then run two concurrent \
            delete+read+write sequences over one Keychain item, and the loser's swallowed \
            errSecDuplicateItem leaves the UI publishing a token the Keychain does not hold.
            """
        )
    }

    /// A regeneration retires the old credential before it waits, not after.
    ///
    /// The Settings button's help text promises the current token is
    /// invalidated immediately. While `bearerToken` still held the old value
    /// across the await, a running server kept authorizing with it for the
    /// whole Keychain wait — indefinitely, when that wait is the consent panel
    /// this whole issue is about. `hw_localapi::authorize` denies everything
    /// against an empty expected token (`auth.rs`,
    /// `an_empty_expected_token_never_authorizes`), so clearing it on the click
    /// is what makes the promise true.
    @Test func regenerateRetiresTheOldTokenBeforeItAwaitsTheKeychain() throws {
        let body = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func regenerateBearerToken(",
            to: "func handleSystemWillSleep("
        )

        guard let invalidate = body.range(of: "bearerToken = \"\"") else {
            Issue.record("""
                regenerateBearerToken() must clear bearerToken synchronously, before any suspension. \
                Otherwise the server it is regenerating for keeps accepting the old token for the \
                whole Keychain wait (issue #655 review, Codex P1).
                """)
            return
        }
        guard let keychainWait = body.range(of: "regenerateTokenOffMainActor()") else {
            Issue.record("regenerateBearerToken() must go through the off-main-actor wrapper — update this anchor if it was renamed")
            return
        }
        #expect(
            invalidate.lowerBound < keychainWait.lowerBound,
            """
            regenerateBearerToken() awaits the Keychain before it retires the old token, so the \
            supposedly-invalidated credential stays accepted for the length of that wait.
            """
        )
    }

    // MARK: - 6-9. A pending start is visible to every lifecycle hook

    /// The "live or on its way up" predicate counts a pending start.
    ///
    /// The only assertion in this file that calls production code rather than
    /// reading it — the rule `ProductionSource` states, applied the one place
    /// it could be. `hasPendingStart` is the clause the whole of group B turns
    /// on: before the token load moved off the main actor there was no state in
    /// which the server had no socket and was still starting.
    @Test func theLiveOrStartingPredicateCountsAPendingStart() {
        #expect(
            !LocalAPIServer.serverIsLiveOrStarting(isRunning: false, hasServer: false, hasPendingStart: false),
            "a server with no socket, not running and with nothing pending is not live"
        )
        #expect(
            LocalAPIServer.serverIsLiveOrStarting(isRunning: true, hasServer: false, hasPendingStart: false),
            "a running server is live"
        )
        #expect(
            LocalAPIServer.serverIsLiveOrStarting(isRunning: false, hasServer: true, hasPendingStart: false),
            "a server that has bound an HTTPServer but not yet reported listening is on its way up"
        )
        #expect(
            LocalAPIServer.serverIsLiveOrStarting(isRunning: false, hasServer: false, hasPendingStart: true),
            """
            a start still waiting on its Keychain read is on its way up. Dropping this clause is the \
            whole of issue #655's aftermath: sleep would not stop it, and stop() would not delete \
            the discovery file for it.
            """
        )
        #expect(
            LocalAPIServer.serverIsLiveOrStarting(isRunning: true, hasServer: true, hasPendingStart: true),
            "all three at once is still live"
        )
    }

    /// Sleep and wake both ask the predicate, not `isRunning`.
    ///
    /// `handleSystemWillSleep()` gated on `isRunning`, so it never reached
    /// `stop()` for a start that was only pending — the start survived sleep
    /// and bound a socket on the far side, which is the one thing the hook
    /// exists to prevent.
    @Test func aPendingStartCountsAsLiveForEveryLifecycleHook() throws {
        let sleepBody = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func handleSystemWillSleep(",
            to: "func handleSystemDidWake("
        )
        let wakeBody = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func handleSystemDidWake(",
            to: "private func registerRoutes"
        )

        #expect(
            sleepBody.contains("guard isLiveOrStarting else"),
            """
            handleSystemWillSleep() must gate on isLiveOrStarting. On `isRunning` it never reaches \
            stop() for a start that is still waiting on its Keychain read, and that start binds a \
            socket after the machine has gone to sleep (issue #655).
            """
        )
        #expect(
            !sleepBody.contains("guard isRunning else"),
            "handleSystemWillSleep() must not gate on isRunning alone — that is the bug"
        )
        #expect(
            wakeBody.contains("!isLiveOrStarting"),
            "handleSystemDidWake() must ask the same predicate, so a start already pending is not started twice"
        )
    }

    /// A start still waiting on its token is orphaned by `stop()` — and
    /// `stop()` still runs its cleanup for it.
    ///
    /// Such a start has no `server` yet. `guard server != nil` therefore
    /// returned before everything below it: the pending bind landed after the
    /// user switched the toggle off, and `deletePortFile()` never ran, so a
    /// quit in that window left a stale local-api.json advertising the previous
    /// launch's port and token.
    ///
    /// The ordering is the inverse of the one round 1 asserted, and
    /// deliberately so. With the guard asking `isLiveOrStarting`, clearing
    /// `pendingStartID` first would make the predicate false again for a
    /// pending-only stop and put the early return straight back.
    @Test func aStartWaitingOnItsTokenIsCancelledByStop() throws {
        let body = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func stop(",
            to: "func restart("
        )

        guard let earlyReturn = body.range(of: "guard isLiveOrStarting else") else {
            Issue.record("""
                stop()'s early return must gate on isLiveOrStarting. On `server != nil` a start that \
                is still waiting on its token returns before the orphan below AND before \
                deletePortFile() (issue #655).
                """)
            return
        }
        guard let orphan = body.range(of: "pendingStartID = nil") else {
            Issue.record("stop() must clear pendingStartID so an in-flight start cannot bind (issue #655)")
            return
        }
        #expect(
            earlyReturn.lowerBound < orphan.lowerBound,
            """
            stop() clears pendingStartID BEFORE the guard that reads it. For a start that is pending \
            with no socket, isLiveOrStarting is then false, the guard returns, and stop() skips \
            deletePortFile() — the stale local-api.json is back.
            """
        )
        #expect(
            body.contains("deletePortFile()"),
            "stop() must delete the discovery file — a pending-only stop is exactly the case that used to skip it"
        )
    }

    /// A bind that succeeds retires the error the previous one failed with.
    ///
    /// The EADDRINUSE fallback re-enters `bindAndRun()` rather than `start()`,
    /// so it never passes the `lastError = nil` that `start()` performs. The
    /// clear belongs on the listening path and not at the top of
    /// `bindAndRun()`: `handleRunFailure()` sets `lastError` from the run task
    /// and can land *after* the retry has re-entered, so a clear at the top
    /// would be overwritten by the very failure being retried.
    @Test func aSuccessfulBindRetiresTheEarlierBindError() throws {
        let bindStep = try ProductionSource.slice(
            of: Self.serverPath,
            from: "private func bindAndRun",
            to: "func stop("
        )

        guard bindStep.contains("self.isRunning = port > 0") else {
            Issue.record("bindAndRun()'s listening branch was renamed — update this anchor rather than deleting the check")
            return
        }
        guard let cleared = bindStep.range(of: "self.lastError = nil") else {
            Issue.record("""
                A successful bind must clear lastError. The EADDRINUSE retry re-enters bindAndRun() \
                and skips start()'s own clear, so a healthy ephemeral rebind leaves "Address already \
                in use" on screen beside a running server (issue #655).
                """)
            return
        }
        guard let retry = bindStep.range(of: "self.bindAndRun()") else {
            Issue.record("the EADDRINUSE retry must re-enter bindAndRun() — update this anchor if it was renamed")
            return
        }
        #expect(
            cleared.lowerBound < retry.lowerBound,
            "lastError must be cleared on the listening path, which precedes the retry path in this function"
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
