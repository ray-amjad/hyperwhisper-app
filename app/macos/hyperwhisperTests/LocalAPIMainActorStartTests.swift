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
//  source, the last resort documented in `ProductionSource`. The three rules
//  that could be lifted into pure functions were — `serverIsLiveOrStarting`,
//  `nextTokenOwner` and `regenerationOutcome` — and all three are tested by
//  calling them.
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
//  the bind before it failed with (12).
//
//  One line of that fix then turned out to be a defect of its own, and tests
//  13-16 are about it. A regeneration discharges what it adopted WITHOUT
//  touching the socket (13, 14, 15), and the `stop()` that used to stand in
//  front of the rebind cannot come back (16). A live server authorizes against
//  `bearerToken` on every request, so the credential the regeneration has just
//  published is already in force and only the discovery file is stale; stopping
//  and rebinding in the same tick instead re-asked the kernel for the very port
//  the old, still-draining socket was holding. One click on Regenerate, and the
//  Local API never came back — no port file, nothing listening, and a raw kqueue
//  error printed into the Settings pane (issue #641).
//
//  That click had a second half, and tests 17-22 are about that one. The bind
//  attempt is the third actor in this file with state to own, and it was the one
//  with no identity at all: `bindAndRun()` spawns a run task and a listening
//  waiter, and either can resume long after the attempt that spawned it was
//  replaced by another bind or retired by a `stop()`. A superseded attempt could
//  therefore publish a port for a socket nobody owns — or, the line that made
//  #641 PERMANENT rather than merely ugly, clear `server`, cancel the live
//  `runTask` and delete the discovery file on its way past, killing the bind that
//  had just replaced it. So there is a generation now: minted once per attempt
//  and never mistaken for its successor (17, 18), carried into the run task's
//  failure path (19), checked before the waiter publishes anything (20) and
//  before it retries anything (21) — and retired by `stop()`, so nothing binds a
//  socket back up behind a switch the user has turned off (22). That last one
//  matters because toggling off and on is the only recovery the issue leaves
//  users, and it reaches the same race by the same road.
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

    // MARK: - 13-16. A regeneration republishes the discovery file; it does not rebind

    /// A regeneration that finds the server listening rewrites the discovery
    /// file and leaves the socket alone.
    ///
    /// **This is issue #641 itself.** The old code ran `stop(); bindAndRun()`
    /// here. `stop()` fires the real shutdown into a detached task and returns
    /// in the same tick, so the bind that followed it immediately asked the
    /// kernel for the persisted port the old socket was still draining —
    /// EADDRINUSE, two recoveries racing onto the main actor, and from one click
    /// a pane stuck on "Server enabled / Starting…" with `SocketError. kqueue
    /// kevent(9): Bad file descriptor` printed into it, no `local-api.json`, and
    /// nothing listening.
    ///
    /// None of that work was ever needed. `authorized()` reads `bearerToken` off
    /// the live instance on every request and the routes capture `[weak self]`
    /// rather than a token, so the new credential is in force the moment it is
    /// assigned; `writePortFile(port:)` reads the token at call time. The file
    /// is the only stale thing, so the file is the whole job — and the port
    /// survives, which is what every MCP client and Shortcut is pointed at.
    ///
    /// Called rather than scraped, which is the rule `ProductionSource` states:
    /// the decision is liftable, so it was lifted.
    @Test func aRegenerationOnALiveServerRepublishesTheFileInsteadOfRebinding() {
        #expect(
            LocalAPIServer.regenerationOutcome(isRunning: true, hasServer: true) == .republishDiscoveryFile,
            """
            A regeneration on a running server must rewrite local-api.json and nothing else. Any \
            outcome that tears the socket down re-binds the port the closing socket still holds, \
            which is the single click that killed the Local API for good in issue #641.
            """
        )
    }

    /// A regeneration that lands while a bind is in flight binds nothing.
    ///
    /// `server != nil` with `isRunning == false` is a `bindAndRun()` whose
    /// `waitUntilListening()` has not resolved yet. That waiter writes the
    /// discovery file itself when it lands, and reads `bearerToken` *then* —
    /// which is already the regenerated one. Binding a second `HTTPServer`
    /// beside it would leak a socket and hand the first one EADDRINUSE on the
    /// persisted port, which is the same collision as #641 by another route.
    @Test func aRegenerationDuringABindInFlightBindsNothing() {
        #expect(
            LocalAPIServer.regenerationOutcome(isRunning: false, hasServer: true) == .awaitBindInFlight,
            """
            A regeneration that completes while a bind is still in flight must do nothing at all. \
            The in-flight bind's own waiter publishes the port and writes local-api.json with the \
            token it reads at that moment, which is the new one; a second bind here is a leaked \
            HTTPServer racing the first for the same port.
            """
        )
    }

    /// A regeneration that adopted a pending start still binds.
    ///
    /// The anti-vacuity case, and a live #655 regression guard. A regeneration
    /// claims ownership synchronously on the click, which supersedes a `start()`
    /// whose Keychain read is still in flight — that start will therefore not
    /// bind itself. Nothing is bound and nothing is binding, so the duty the
    /// regeneration adopted at `pendingBindOwner = isLiveOrStarting ? owner : nil`
    /// is a real socket, and it has to be discharged here or the Local API the
    /// user switched on a moment ago never comes up at all.
    ///
    /// Without this assertion an "always republish" implementation would pass
    /// the two above and still ship that regression.
    @Test func aRegenerationThatAdoptedAPendingStartStillBinds() {
        #expect(
            LocalAPIServer.regenerationOutcome(isRunning: false, hasServer: false) == .bind,
            """
            A regeneration that superseded a pending start() must still bind. That start will not \
            bind itself — the claim taken on the Regenerate click withdrew its right to — so \
            skipping the bind here leaves the server the user just switched on permanently down \
            (issue #655).
            """
        )
    }

    /// A regeneration never stops a live server.
    ///
    /// The regression guard for the exact two-line pair, because the three
    /// assertions above constrain the *decision* and not the code that acts on
    /// it: a `regenerationOutcome` that answers `.republishDiscoveryFile` is no
    /// use if a future edit puts `self.stop()` back above the switch.
    ///
    /// `slice` strips comments, so the prose in `regenerateBearerToken()` that
    /// explains the ban does not satisfy the ban.
    @Test func aRegenerationNeverStopsALiveServer() throws {
        let body = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func regenerateBearerToken(",
            to: "func handleSystemWillSleep("
        )

        #expect(
            !body.contains("self.stop()"),
            """
            regenerateBearerToken() stops the server. stop() hands the real shutdown to a detached \
            task and returns in the same tick, so whatever binds after it races the closing socket \
            for the persisted port — and it deletes local-api.json on the way past. That pair is \
            issue #641: one click on Regenerate, and the Local API is down until the user toggles \
            it off and on. A live server needs no rebind at all; rewrite the discovery file.
            """
        )
        #expect(
            body.contains("Self.regenerationOutcome("),
            """
            regenerateBearerToken() must route its three end states through regenerationOutcome(), \
            which is the decision the tests above call directly. Inlining the branches here puts \
            them back out of reach of everything but a source scrape.
            """
        )
        #expect(
            body.contains("self.writePortFile(port: self.listeningPort)"),
            """
            A regeneration on a live server must republish local-api.json on the port it is already \
            listening on. Without that write the file keeps advertising the token the user has just \
            invalidated, and every MCP client that reads it gets a 401.
            """
        )
    }

    // MARK: - 17-22. A superseded bind attempt publishes nothing and destroys nothing

    /// Only the attempt whose generation is current owns the server.
    ///
    /// Called rather than scraped, which is the rule `ProductionSource` states.
    /// The predicate is an equality and has to stay one: every guard added below
    /// is only as good as this answer, and a `!= 0` or a Bool-flag rewrite makes
    /// all four of them vacuously true the moment any bind has ever been made.
    @Test func aSupersededBindAttemptOwnsNothing() {
        #expect(
            LocalAPIServer.bindAttemptStillOwnsServer(attempt: 0, current: 0),
            "the first attempt owns the server it just bound"
        )
        #expect(
            LocalAPIServer.bindAttemptStillOwnsServer(attempt: 7, current: 7),
            "an attempt nothing has superseded still owns the server"
        )
        #expect(
            LocalAPIServer.bindAttemptStillOwnsServer(attempt: .max, current: .max),
            "ownership is an equality, so it holds at the wrap point too"
        )
        #expect(
            !LocalAPIServer.bindAttemptStillOwnsServer(attempt: 7, current: 8),
            """
            an attempt that a later bind superseded must own nothing. This is the one that matters: \
            it is the stale run task and the stale waiter of issue #641, and letting either through \
            is what cleared the server that had just replaced them.
            """
        )
        #expect(
            !LocalAPIServer.bindAttemptStillOwnsServer(attempt: 8, current: 7),
            """
            the comparison must be an equality, not a `<=`. Nothing should ever hold an id ahead of \
            the current generation, and a predicate that tolerates one is not reading an identity.
            """
        )
        #expect(
            !LocalAPIServer.bindAttemptStillOwnsServer(attempt: .max, current: 0),
            "an attempt superseded across the wrap point owns nothing either"
        )
        #expect(
            !LocalAPIServer.bindAttemptStillOwnsServer(attempt: 0, current: .max),
            "and the same the other way round"
        )
    }

    /// Minting a bind generation always changes it.
    ///
    /// Called rather than scraped, and the anti-vacuity test for every guard in
    /// this group. A mint that returned `current` — or clamped with a `max(…)` —
    /// would leave `bindAttemptStillOwnsServer` answering `true` for every stale
    /// attempt in the process, and each of the four guards below would read as
    /// present and do nothing at all.
    ///
    /// `.max` is in here because the addition has to WRAP. A trapping `+ 1` would
    /// crash rather than roll over, and a crash is not a recovery.
    @Test func mintingABindGenerationAlwaysChangesIt() {
        #expect(
            LocalAPIServer.nextBindGeneration(after: 0) == 1,
            "the first mint must not collide with the unclaimed initial value"
        )
        #expect(
            LocalAPIServer.nextBindGeneration(after: .max) == 0,
            """
            the mint must wrap rather than trap. Trapping addition turns the eighteen-quintillionth \
            bind into a crash; wrapping only has to guarantee that the id CHANGES.
            """
        )
        let generations: [UInt64] = [0, 1, .max]
        for generation in generations {
            #expect(
                !LocalAPIServer.bindAttemptStillOwnsServer(
                    attempt: generation,
                    current: LocalAPIServer.nextBindGeneration(after: generation)
                ),
                """
                a mint must supersede the attempt it was minted after — at 0, at 1 and at the wrap \
                point alike. If it does not, every guard written against this predicate passes for \
                the stale attempt it exists to stop, and issue #641 is back with the guards still \
                in the file.
                """
            )
        }
    }

    /// A stale run failure cannot clear the server that replaced it.
    ///
    /// **This is the line that made issue #641 permanent.** One failed bind has
    /// two independent reactions racing onto the main actor: the run task's
    /// `catch`, and the waiter's. When the waiter won and installed a second
    /// `HTTPServer`, `handleRunFailure()` landed afterwards knowing nothing about
    /// which server it was reporting on — and set `self.server = nil`, cancelled
    /// the live `runTask` and deleted the discovery file, killing the bind that
    /// was about to rescue the situation.
    ///
    /// Both halves are asserted, because either alone is satisfiable without the
    /// fix: the run task has to hand its attempt over, and `handleRunFailure()`
    /// has to check it BEFORE the damage rather than after. A membership check
    /// would pass for a guard placed under the teardown, which is no guard.
    @Test func aStaleRunFailureCannotClearTheLiveServer() throws {
        let bindStep = try ProductionSource.slice(
            of: Self.serverPath,
            from: "private func bindAndRun",
            to: "func stop("
        )
        #expect(
            bindStep.contains("handleRunFailure(error, from: attempt)"),
            """
            the run task must tell handleRunFailure() which bind attempt it belongs to. Without the \
            attempt there is nothing for the guard below to compare, and a run task that fails after \
            its bind was superseded tears down the server that replaced it (issue #641).
            """
        )

        let failureBody = try ProductionSource.slice(
            of: Self.serverPath,
            from: "private func handleRunFailure(",
            to: "private static func extractPort("
        )
        guard let ownership = failureBody.range(of: "bindAttemptStillOwnsServer") else {
            Issue.record("""
                handleRunFailure() must check that the attempt reporting the failure still owns the \
                server before it touches anything. Unguarded, it is the single line that turned one \
                EADDRINUSE into a Local API that never came back (issue #641).
                """)
            return
        }
        for damage in [
            "self.lastError",
            "self.server = nil",
            "self.runTask?.cancel()",
            "self.deletePortFile()"
        ] {
            guard let site = failureBody.range(of: damage) else {
                Issue.record("""
                    handleRunFailure() no longer contains an expected teardown line — update this \
                    anchor rather than deleting the check.
                    """)
                continue
            }
            #expect(
                ownership.lowerBound < site.lowerBound,
                """
                handleRunFailure() tears state down before it checks whether the attempt reporting \
                the failure still owns it. A guard below the damage is not a guard: the server the \
                retry had just installed is already nil, its run task already cancelled and \
                local-api.json already deleted by the time the check runs (issue #641).
                """
            )
        }
    }

    /// A superseded waiter publishes nothing.
    ///
    /// The success side, which is the half that is easy to miss: a waiter is
    /// suspended inside `waitUntilListening()` while a `stop()`, a retry or a
    /// regeneration's bind replaces it, then resolves and writes a port,
    /// `isRunning`, a cleared `lastError`, local-api.json and the persisted-port
    /// default — all for a socket nobody owns, and all over the live attempt's
    /// own. Behind a toggle the user switched off it is worse still: a listener
    /// and a discovery file reappear with the switch showing off.
    ///
    /// The first assertion is what keeps the check ON the main actor. A read
    /// taken before the hop and acted on after it is the same read-then-act
    /// window the guard exists to close.
    @Test func aSupersededWaiterPublishesNothing() throws {
        let listeningPath = try ProductionSource.slice(
            of: Self.serverPath,
            from: "try await httpServer.waitUntilListening()",
            to: "} catch {"
        )

        guard let hop = listeningPath.range(of: "await MainActor.run") else {
            Issue.record("the waiter's main-actor hop was renamed — update this anchor rather than deleting the check")
            return
        }
        guard let ownership = listeningPath.range(of: "bindAttemptStillOwnsServer") else {
            Issue.record("""
                the waiter must check that its bind attempt still owns the server before it \
                publishes anything. Without it, a waiter for a bind that was replaced — or stopped \
                — advertises a dead socket in local-api.json and flips isRunning back on \
                (issue #641).
                """)
            return
        }
        #expect(
            hop.lowerBound < ownership.lowerBound,
            """
            the ownership check is read before the main-actor hop. bindGeneration is main-actor \
            state, and a check taken off the actor and acted on after it re-opens the very \
            read-then-act window this guard exists to close.
            """
        )
        for publication in [
            "self.listeningPort = port",
            "self.isRunning = port > 0",
            "self.lastError = nil",
            "self.writePortFile(port: port)",
            "LocalAPIServerPersistedPortKey"
        ] {
            guard let site = listeningPath.range(of: publication) else {
                Issue.record("""
                    the waiter no longer contains an expected publication line — update this anchor \
                    rather than deleting the check.
                    """)
                continue
            }
            #expect(
                ownership.lowerBound < site.lowerBound,
                """
                the waiter publishes before it checks whether its bind attempt still owns the \
                server. A guard after the publish is not a guard — the stale port, the stale \
                isRunning, the cleared lastError and the rewritten local-api.json have all already \
                landed on top of the live attempt's own (issue #641).
                """
            )
        }
    }

    /// A superseded waiter neither retries nor records an error.
    ///
    /// The failure side. Unguarded it wipes `LocalAPIServerPersistedPortKey` out
    /// from under the attempt that had just written it — which costs the stable
    /// port on the next launch — re-enters `bindAndRun()` after a `stop()`, so a
    /// socket comes up behind a switch the user turned off, and writes a
    /// `lastError` for a bind nobody is waiting for.
    ///
    /// Ownership and `preferredPort` are read in ONE hop on purpose: the existing
    /// code already hopped for `preferredPort`, and folding the predicate into
    /// that hop keeps the count at one and makes the two facts consistent with
    /// each other.
    @Test func aSupersededWaiterDoesNotRetryOrRecordAnError() throws {
        let waiter = try ProductionSource.slice(
            of: Self.serverPath,
            from: "try await httpServer.waitUntilListening()",
            to: "func stop("
        )

        guard let ownership = waiter.range(of: "guard stillOwns") else {
            Issue.record("""
                the waiter's catch must guard on ownership before it reacts to the failure at all. \
                Without it a superseded attempt rebinds after a stop() and wipes the persisted-port \
                preference on the way (issue #641).
                """)
            return
        }
        for reaction in [
            "UserDefaults.standard.removeObject",
            "self.server = nil",
            "self.bindAndRun()",
            "self.lastError = error.localizedDescription"
        ] {
            guard let site = waiter.range(of: reaction) else {
                Issue.record("""
                    the waiter's failure path no longer contains an expected line — update this \
                    anchor rather than deleting the check.
                    """)
                continue
            }
            #expect(
                ownership.lowerBound < site.lowerBound,
                """
                the waiter's catch acts on a failed bind before it checks whether that bind is still \
                the one that owns the server. A guard below any of these is not a guard: the \
                preference is already wiped, the rebind already issued behind a toggle the user \
                switched off, or the error already on screen for an attempt nobody is waiting for \
                (issue #641).
                """
            )
        }
    }

    /// Stopping the server invalidates the bind that is still in flight.
    ///
    /// The toggle-off case, and the reason this is not only about the Regenerate
    /// button. `stop()` clears `server`, `isRunning` and the discovery file in one
    /// synchronous block, but a waiter suspended inside `waitUntilListening()`
    /// knows nothing about it — it lands afterwards and publishes a port, or
    /// re-enters `bindAndRun()`, leaving a listener and a local-api.json behind a
    /// switch the user has turned off. Bumping the generation withdraws its right
    /// to do either.
    ///
    /// Position against the guard is load-bearing: a bump above `guard
    /// isLiveOrStarting` would fire on an idempotent no-op stop and retire a bind
    /// that nothing had stopped. Position against the teardown lines below is
    /// NOT: everything after the guard is one synchronous main-actor block with
    /// no await in it, so asserting an order within it would only be brittle.
    /// Hence membership there, and a position here.
    @Test func stoppingTheServerInvalidatesTheBindInFlight() throws {
        let body = try ProductionSource.slice(
            of: Self.serverPath,
            from: "func stop(",
            to: "func restart("
        )

        guard let earlyReturn = body.range(of: "guard isLiveOrStarting else") else {
            Issue.record("stop()'s early return was renamed — update this anchor rather than deleting the check")
            return
        }
        guard let invalidation = body.range(of: "claimBindGeneration()") else {
            Issue.record("""
                stop() must retire the current bind generation. Without it, a waiter suspended in \
                waitUntilListening() when the user switched the Local API off lands on the far side \
                of the toggle and publishes a port — or rebinds — behind a switch that reads off \
                (issue #641).
                """)
            return
        }
        #expect(
            earlyReturn.lowerBound < invalidation.lowerBound,
            """
            stop() retires the bind generation ABOVE its early return, so an idempotent no-op stop \
            invalidates a bind attempt nothing asked it to stop — the next successful bind would \
            then refuse to publish its own port.
            """
        )
        #expect(
            body.contains("pendingBindOwner = nil"),
            """
            stop() must still orphan a start that is only pending. The generation covers a bind \
            already in flight; pendingBindOwner covers one that has not reached bindAndRun() yet, \
            and dropping either leaves half the window open (issue #655).
            """
        )
        #expect(
            !body.contains("tokenOwner"),
            """
            stop() touches tokenOwner. Switching the server off does not invalidate the token the \
            Settings pane is showing, and the bind generation exists as its own counter precisely so \
            that this withdrawal cannot be smuggled into the token identity (issue #655).
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
