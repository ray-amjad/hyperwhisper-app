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
//  source, the last resort documented in `ProductionSource`. The rules that
//  could be lifted into pure functions were — `serverIsLiveOrStarting`,
//  `nextTokenOwner` and `regenerationOutcome` — and are tested by calling them.
//
//  Read them as one statement about the window the fix opened. The blocking
//  read is never called directly (1). The token is still assigned before any
//  socket is bound (2). Whoever claimed `bearerToken` last owns it, and a
//  superseded continuation publishes nothing and binds nothing (3, 4, 5) — so a
//  Regenerate click cannot have the credential it just retired re-published by
//  a start whose Keychain read was still in flight. Regenerations, and only
//  regenerations, are serialized over the one Keychain item (6), and a
//  regeneration rebinds with the token it already holds rather than reading the
//  item a second time (7), and retires the old credential before it waits
//  rather than after (8). And every hook that used to read `server == nil` as
//  "not starting" now asks the one predicate that knows better (9, 10),
//  including `stop()`, which must reach `deletePortFile()` for a start that
//  never got a socket (11) — and a bind that finally succeeds retires the error
//  the bind before it failed with (12). A regeneration on a live server
//  rewrites local-api.json and never stops or rebinds the socket (13, 14):
//  stop() returns before the old socket closes, so the rebind that followed it
//  failed on the same port and left nothing listening (issue #641).
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

    /// Nothing in the macOS app calls the blocking Keychain read directly.
    ///
    /// The ban is on the *synchronous* entry points, and it is the whole app
    /// target, not the Local API directory. `LocalAPIAuth` is `internal`, so
    /// every file in the target can reach `loadOrCreateToken()` — and the files
    /// that would hurt most are the ones OUTSIDE `Managers/LocalAPI`:
    /// `hyperwhisperApp.swift` and `AppDelegate.swift` are the bootstrap that
    /// issue #655 froze, and `APIServerSettingsSection.swift` is the Settings
    /// pane whose buttons drive this server. A call added in any of those brings
    /// the freeze straight back, and while this walk covered only
    /// `Managers/LocalAPI` it did so with this guard still green.
    ///
    /// `Managers/LocalAPI/LocalAPIAuth.swift` is the one exemption, by path
    /// rather than by filename, because it declares the synchronous entry points
    /// and wraps them; everything else must go through the `OffMainActor`
    /// wrappers, which hop the call onto a detached task before
    /// `SecItemCopyMatching` runs.
    ///
    /// The needles include the open paren deliberately:
    /// `LocalAPIAuth.loadOrCreateToken` is a prefix of
    /// `LocalAPIAuth.loadOrCreateTokenOffMainActor`, so a paren-less needle
    /// would flag the fix itself.
    @Test func theLocalApiNeverReadsTheKeychainOnTheMainActor() throws {
        let appSourceRoot = Self.repoRoot.appendingPathComponent("app/macos/hyperwhisper")
        let files = try Self.swiftFiles(under: appSourceRoot)
        // Two anti-vacuity checks, because a walk that finds nothing passes.
        // The count catches a moved directory; the membership catches a walk
        // that somehow reaches the tree but misses the subject of the ban.
        #expect(files.count >= 200, "the macOS app source tree was not found where this test expects it")
        #expect(
            files.contains { $0.path.hasSuffix("Managers/LocalAPI/LocalAPIServer.swift") },
            "the widened walk must still cover the Local API itself"
        )

        var offenders: [String] = []
        for file in files where !file.path.hasSuffix("Managers/LocalAPI/LocalAPIAuth.swift") {
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

    // MARK: - 2. The token arrives before the socket

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

    // MARK: - 3-5. Whoever claimed the token last owns it, and only they publish

    /// Each claim supersedes the last one.
    ///
    /// The one assertion in this group that calls production code rather than
    /// reading it, which is the rule `ProductionSource` states. The whole
    /// ownership discipline rests on a claim never colliding with the claim
    /// before it, including at the wrap point — an id that repeated would let a
    /// superseded continuation pass the very guard written to stop it.
    @Test func eachTokenClaimSupersedesTheLastOne() {
        #expect(
            LocalAPIServer.nextTokenOwner(after: 0) != 0,
            "the first claim must not collide with the unclaimed initial value"
        )
        #expect(
            LocalAPIServer.nextTokenOwner(after: 7) == 8,
            "claims are monotonic, so a later claim is always distinguishable from an earlier one"
        )
        #expect(
            LocalAPIServer.nextTokenOwner(after: .max) != .max,
            """
            the claim must still change at the wrap point. If it did not, the operation holding \
            UInt64.max would keep the right to publish a token a later operation had already \
            retired — the exact defect the claim exists to prevent (issue #655).
            """
        )
    }

    /// A start's continuation may publish only if it still owns the token.
    ///
    /// **This is the defect round 2 of the review was called for.** A Regenerate
    /// click lands while a `start()` token read is still in flight. It sets
    /// `bearerToken = ""` synchronously, because the button promises the old
    /// credential is dead on the click. Then the start's read returns — and
    /// unless that continuation re-reads who owns the token, it re-publishes the
    /// credential the user just invalidated, and `bindAndRun()` writes that
    /// credential into local-api.json for the whole of the regeneration's own
    /// indefinite Keychain wait.
    ///
    /// Ordering cannot fix it, and round 1's serial chain made it worse: the
    /// regeneration was queued BEHIND the read it was supposed to supersede, so
    /// the stale continuation was guaranteed to resume first. What fixes it is a
    /// claim taken synchronously on the click and re-read by the continuation
    /// before it touches `bearerToken` — so the assertions here are positional
    /// on BOTH sides of the await.
    @Test func aSupersededStartCannotPublishTheTokenARegenerationRetired() throws {
        let prologue = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func start(",
            to: "private func bindAndRun"
        )

        guard let claim = prologue.range(of: "claimTokenOwnership()") else {
            Issue.record("""
                start() must claim ownership of bearerToken before it issues its Keychain read. \
                Without a claim there is nothing for the continuation to check, and a Regenerate \
                click that lands during the read is silently undone (issue #655).
                """)
            return
        }
        guard let read = prologue.range(of: "loadOrCreateTokenOffMainActor()") else {
            Issue.record("start()'s off-main-actor read was renamed — update this anchor rather than deleting the check")
            return
        }
        guard let ownershipCheck = prologue.range(of: "self.tokenOwner == owner") else {
            Issue.record("""
                start()'s continuation must re-read tokenOwner and compare it to its OWN claim. \
                Without that compare, a read that resumes after a Regenerate click re-publishes the \
                retired credential and bindAndRun() advertises it in local-api.json (issue #655).
                """)
            return
        }
        guard let publish = prologue.range(of: "self.bearerToken = token") else {
            Issue.record("start() must publish the token it loaded (issue #655)")
            return
        }

        #expect(
            claim.lowerBound < read.lowerBound,
            """
            start() claims ownership of the token AFTER it issues the Keychain read. A Regenerate \
            click that lands in between then claims first and is immediately superseded by the \
            start it was supposed to supersede — the claim has to be synchronous, on the click.
            """
        )
        #expect(
            read.upperBound <= ownershipCheck.lowerBound,
            """
            start() checks ownership BEFORE it awaits the Keychain, which proves nothing: the whole \
            hazard is a claim that lands during the await. The check has to be re-read on the far \
            side of the suspension.
            """
        )
        #expect(
            ownershipCheck.lowerBound < publish.lowerBound,
            """
            start()'s continuation assigns bearerToken before it checks whether it still owns it, \
            so a token a Regenerate click already retired is republished (issue #655, round 2).
            """
        )
        #expect(
            !prologue.contains("tokenOwner != 0"),
            """
            start() tests that SOME claim exists rather than that the claim is its own. That is the \
            flag-instead-of-identity shape the tokenOwner field warns about: it passes for a start \
            a regeneration has already superseded.
            """
        )
    }

    /// Only the operation that owns the bind performs it.
    ///
    /// `tokenOwner` says who may publish; `pendingBindOwner` says who may bind,
    /// and carries the same id so the two questions cannot drift apart. A
    /// superseded start must not bind — but something has to, or a server the
    /// user just switched on never comes up, so a regeneration that lands on a
    /// pending start adopts the duty under its OWN id.
    @Test func onlyTheOperationThatOwnsTheBindPerformsIt() throws {
        let prologue = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func start(",
            to: "private func bindAndRun"
        )

        guard let mint = prologue.range(of: "pendingBindOwner = owner") else {
            Issue.record("start() must record that it owes this server a bind (issue #655)")
            return
        }
        guard let check = prologue.range(of: "self.pendingBindOwner == owner") else {
            Issue.record("""
                start()'s continuation must compare pendingBindOwner to its OWN claim before it \
                binds. A `!= nil` check, or no check, lets a superseded start bind on behalf of the \
                operation that superseded it, with the token that operation retired (issue #655).
                """)
            return
        }
        guard let bind = prologue.range(of: "self.bindAndRun()") else {
            Issue.record("start()'s hand-off to the bind step was renamed — update this anchor rather than deleting the check")
            return
        }
        #expect(mint.lowerBound < check.lowerBound, "start() must mint the bind claim before the continuation compares against it")
        #expect(
            check.lowerBound < bind.lowerBound,
            "start() binds before it checks that the bind is still its to perform (issue #655)"
        )
        #expect(
            !prologue.contains("pendingBindOwner != nil"),
            "start() must compare the bind claim to its own id, not merely test that one exists"
        )

        let regenerateBody = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func regenerateBearerToken(",
            to: "func handleSystemWillSleep("
        )
        #expect(
            regenerateBody.contains("pendingBindOwner = isLiveOrStarting ? owner : nil"),
            """
            regenerateBearerToken() must adopt the bind duty of whatever was live or on its way up \
            when the click landed. The claim it takes supersedes any pending start, so that start \
            will not bind itself — without this line the Local API the user just switched on never \
            comes up at all (issue #655, round 2).
            """
        )
        #expect(
            regenerateBody.contains("self.pendingBindOwner == owner"),
            "regenerateBearerToken() must bind only if the duty is still its own — a stop() in between withdraws it"
        )
    }

    // MARK: - 6-8. One Keychain item; a narrow chain, and a claim

    /// Regenerations are serialized over the one Keychain item. Starts are not,
    /// and that is deliberate.
    ///
    /// `regenerateToken()` is a Keychain delete followed by a create and a
    /// write over a single item, and `offMainActor` promises no serialization —
    /// its own doc says so. Two overlapping runs race, and the loser's
    /// `SecItemAdd` returns `errSecDuplicateItem`, which `loadOrCreateToken`
    /// logs and swallows: it still returns the token it generated, so the caller
    /// publishes a token the Keychain does not hold. That is what the chain is
    /// for, and dropping the call back to a bare `Task { }` restores it.
    ///
    /// `start()` must NOT join that chain. Its `loadOrCreateToken()` writes only
    /// when the item is ABSENT, which is the one case that raises no consent
    /// panel and can mint no duplicate; otherwise a start is a pure read and
    /// corrupts nothing. Putting it on the chain bought that nothing and cost
    /// the thing issue #655 is about — a start that waits on the chain inherits
    /// every earlier Keychain call's wait, so one `SecItemCopyMatching` stuck
    /// behind the consent panel holds up every later start for the process
    /// lifetime and toggling the Local API off and on cannot even try again.
    @Test func regenerationsAreSerializedAndAStartIsDeliberatelyNot() throws {
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
            regenerateBody.contains("enqueueRegeneration {"),
            "regenerateBearerToken() must queue its Keychain work on the regeneration chain"
        )
        #expect(
            !regenerateBody.contains("Task {"),
            """
            regenerateBearerToken() opens a bare Task. Two rapid clicks then run two concurrent \
            delete+read+write sequences over one Keychain item, and the loser's swallowed \
            errSecDuplicateItem leaves the UI publishing a token the Keychain does not hold.
            """
        )
        #expect(
            startBody.contains("loadOrCreateTokenOffMainActor()"),
            "start() must still read the token through the off-main-actor wrapper (issue #655)"
        )
        #expect(
            !startBody.contains("enqueueRegeneration"),
            """
            start() queues its token read on the regeneration chain. It then inherits every earlier \
            Keychain call's wait, so one SecItemCopyMatching stuck behind the consent panel holds up \
            every later start for the process lifetime — and the user's natural recovery, toggling \
            the Local API off and on, cannot even issue a fresh attempt. Ordering a start against a \
            regeneration is tokenOwner's job (issue #655, round 2).
            """
        )

        let checks = regenerateBody.components(separatedBy: "self.tokenOwner == owner").count - 1
        #expect(
            checks >= 2,
            """
            regenerateBearerToken() must check ownership on BOTH sides of its Keychain call. \
            Checking only afterwards means a regeneration superseded while it sat in the queue \
            still spends a delete-and-read on a token nobody may publish — and behind the consent \
            panel that is another panel, for nothing (issue #655).
            """
        )
        guard let firstCheck = regenerateBody.range(of: "self.tokenOwner == owner"),
              let keychain = regenerateBody.range(of: "regenerateTokenOffMainActor()") else {
            Issue.record("regenerateBearerToken()'s ownership check or Keychain call was renamed — update these anchors")
            return
        }
        #expect(
            firstCheck.lowerBound < keychain.lowerBound,
            "the first ownership check must precede the Keychain call, so superseded queued work costs no Keychain call at all"
        )
    }

    /// A regeneration rebinds with the token it already holds.
    ///
    /// `regenerateBearerToken()` has the fresh token in hand when it wants the
    /// server back. Going through `restart()` sends it back into `start()`,
    /// which performs a SECOND full `loadOrCreateTokenOffMainActor()` read of
    /// the same Keychain item — doubling the number of `SecItemCopyMatching`
    /// calls that can meet the consent panel this whole fix exists to get off
    /// the main actor. `bindAndRun()` was split out of `start()` precisely so a
    /// caller holding the token can re-enter the bind step; this is that caller.
    @Test func aRegenerationRebindsWithTheTokenItAlreadyHolds() throws {
        let body = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func regenerateBearerToken(",
            to: "func handleSystemWillSleep("
        )

        #expect(
            body.contains("self.bindAndRun()"),
            "regenerateBearerToken() must re-enter the bind step directly with the token it already holds"
        )
        #expect(
            !body.contains("self.restart()"),
            """
            regenerateBearerToken() calls restart(), which goes back through start() and reads the \
            same Keychain item a second time with the fresh token already in hand — a second \
            SecItemCopyMatching that can meet the consent panel, on the one path that has no need \
            of it (issue #655, round 2).
            """
        )
        #expect(
            !body.contains("loadOrCreateTokenOffMainActor"),
            "regenerateBearerToken() must not issue a second load of the item it has just regenerated"
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

    // MARK: - 9-12. A pending start is visible to every lifecycle hook

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
    /// `pendingBindOwner` first would make the predicate false again for a
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
        guard let orphan = body.range(of: "pendingBindOwner = nil") else {
            Issue.record("stop() must clear pendingBindOwner so an in-flight start cannot bind (issue #655)")
            return
        }
        #expect(
            earlyReturn.lowerBound < orphan.lowerBound,
            """
            stop() clears pendingBindOwner BEFORE the guard that reads it. For a start that is \
            pending with no socket, isLiveOrStarting is then false, the guard returns, and stop() \
            skips deletePortFile() — the stale local-api.json is back.
            """
        )
        #expect(
            !body.contains("claimTokenOwnership()"),
            """
            stop() claims token ownership. Switching the server off does not invalidate the token \
            the Settings pane is showing, and claiming here would silence a regeneration the user \
            asked for moments earlier — stop() withdraws the bind duty and nothing else (issue #655).
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
    ///
    /// That last sentence is the claim, so it is the claim that has to go red.
    /// "Before the retry" did not do it — a clear at the top of `bindAndRun()`
    /// is before the retry too, and the version of this test that asserted only
    /// that passed for the placement its own prose argues against. What pins
    /// the clear inside the listening branch is its position BETWEEN two
    /// statements that are only reached when the socket came up: the
    /// `isRunning` assignment above it and the discovery-file write below it.
    @Test func aSuccessfulBindRetiresTheEarlierBindError() throws {
        let bindStep = try ProductionSource.slice(
            of: Self.serverPath,
            from: "private func bindAndRun",
            to: "func stop("
        )

        guard let listening = bindStep.range(of: "self.isRunning = port > 0") else {
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
        guard let write = bindStep.range(of: "self.writePortFile(port: port)") else {
            Issue.record("the discovery-file write was renamed — update this anchor rather than deleting the check")
            return
        }
        guard let retry = bindStep.range(of: "self.bindAndRun()") else {
            Issue.record("the EADDRINUSE retry must re-enter bindAndRun() — update this anchor if it was renamed")
            return
        }
        #expect(
            listening.lowerBound < cleared.lowerBound,
            """
            lastError is cleared at the TOP of bindAndRun() rather than on the listening path. \
            handleRunFailure() sets lastError from the run task and can land AFTER the retry has \
            re-entered, so a clear up there is overwritten by the very failure being retried and \
            the stale "Address already in use" survives the successful rebind (issue #655).
            """
        )
        #expect(
            cleared.lowerBound < write.lowerBound,
            """
            the clear must sit inside the listening branch, above the discovery-file write. Below \
            it — or outside the branch — it is no longer gated on the bind having succeeded, and a \
            failed bind would quietly retire its own error message.
            """
        )
        #expect(
            cleared.lowerBound < retry.lowerBound,
            "lastError must be cleared on the listening path, which precedes the retry path in this function"
        )
    }

    // MARK: - 13-14. A regeneration republishes the port file; it does not rebind

    /// Called, not scraped: the three states the Keychain wait can return to.
    @Test func aRegenerationRebindsOnlyWhenNothingIsBound() {
        #expect(
            LocalAPIServer.regenerationOutcome(isRunning: true, hasServer: true) == .republishPortFile,
            """
            a regeneration on a listening server must only rewrite local-api.json. Rebinding the \
            port the closing socket still holds is issue #641: one click, and nothing is listening.
            """
        )
        #expect(
            LocalAPIServer.regenerationOutcome(isRunning: true, hasServer: false) == .republishPortFile,
            "a running server is republished, never rebound"
        )
        #expect(
            LocalAPIServer.regenerationOutcome(isRunning: false, hasServer: true) == .awaitBindInFlight,
            "a bind in flight writes the port file itself; a second bind would leak an HTTPServer on the same port"
        )
        #expect(
            LocalAPIServer.regenerationOutcome(isRunning: false, hasServer: false) == .bind,
            """
            a regeneration that superseded a pending start() must still bind — that start will not \
            bind itself, so the server the user just switched on would never come up (issue #655).
            """
        )
    }

    /// The decision above is only worth something if the code acts on it:
    /// no `stop()` may come back in front of it.
    @Test func aRegenerationNeverStopsTheServer() throws {
        let body = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func regenerateBearerToken(",
            to: "func handleSystemWillSleep("
        )

        #expect(
            !body.contains("stop()"),
            """
            regenerateBearerToken() stops the server. stop() deletes local-api.json and returns \
            before the old socket closes, so any bind after it races that socket for the same \
            port (issue #641).
            """
        )
        #expect(
            body.contains("Self.regenerationOutcome("),
            "regenerateBearerToken() must act on regenerationOutcome(), the decision the test above calls"
        )
        let republish = try ProductionSource.switchArm(
            named: "case .republishPortFile:",
            in: body,
            of: "LocalAPIServer.swift"
        )
        #expect(
            republish.contains("self.writePortFile(port: self.listeningPort)")
                && !republish.contains("bindAndRun()"),
            "a live server must get local-api.json rewritten on the port it already holds, and no rebind"
        )
        // The last arm: switchArm runs to the end of `body`, so cut it at the
        // switch's closing brace.
        let awaitArm = try ProductionSource.switchArm(
            named: "case .awaitBindInFlight:",
            in: body,
            of: "LocalAPIServer.swift"
        )
        let awaitArmBody = awaitArm.prefix(while: { $0 != "}" })
        #expect(
            !awaitArmBody.contains("bindAndRun()") && !awaitArmBody.contains("writePortFile"),
            "a bind in flight writes the port file itself; a second bind or write here races it"
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
