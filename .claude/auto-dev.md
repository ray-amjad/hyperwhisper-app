# Auto-dev gate for ray-amjad/hyperwhisper-app

This file is the merge gate for the auto-dev consumer routine, and for any `task-lifecycle` run that
names this repo. The routine prompt says which issue to pick and how to report. This file says what
may merge with no human, and what proof it needs. One copy, in git, next to the code it governs.

A change to this file is a change to what ships without review. Review it like one.

## 1. Facts

| Fact | Value |
|---|---|
| Repo | `ray-amjad/hyperwhisper-app` |
| Default branch | `main` |
| App directory | one head per platform: `app/macos` (Swift), `app/windows` (WPF, .NET), `app/linux` (Avalonia); `nextjs/` (web), `hyperwhisper-cloud/` (cloud), `mintlify-help/` (docs) |
| Package manager | per head; npm in `nextjs/` |
| Production | the web app and the cloud service, on Vercel and Fly. The desktop apps reach users only through a release workflow |
| Deploy on merge | web and cloud: yes, on merge. Desktop heads: no. A merge to a desktop head ships at the next `*-release` workflow |
| Staging | `cloud-deploy-staging` exists for the cloud service. None for the web app or the desktop heads |
| CI the gate reads | every workflow run on the PR head sha. One CI per head: `linux-ci`, `windows-ci`, `macos-ci`, `nextjs-ci`, `cloud-ci`, plus `shared-core-tests`, `binding-drift`, `static-gates`, `drizzle-journal`. Only the runs the PR head triggers count. |
| Branch prefix | `percy/fix-<issue>-<slug>` |
| Bot author | `app/dream-team-bot` |
| Queue | every open issue. `percy-queue` issues with a priority label sort first; an issue with no label is not excluded |
| Producers | #hw-find-issues |
| Consumer | #hw-fix-issues |

State lives in issue comments, with a hidden HTML marker on the first line: `auto-dev:start`,
`auto-dev:pr`, `auto-dev:merged`, `auto-dev:too-big`, `auto-dev:done`, `auto-dev:failed`. The routine
prompt owns how the markers are written and read.

## 2. Blast radius: the pick-time test

An issue is small only when every line is true:

- The fix changes no database migration and no schema.
- The fix changes no dependency version and no lockfile.
- The fix changes no CI file and no build file.
- The fix does not touch auth, sessions, permissions, billing, payments, Stripe or secrets, unless
  the issue IS a bug in one of those and its `## Evidence` section proves the bug with real output.
- The issue's `## Where` section names real files, and its `## Evidence` section is not empty.
- The issue's `## Done when` line is a command or a test you can run.
- You expect a diff under about 50 changed lines, in about 1 to 3 files.
- The fix touches ONE platform head only, or the web app only, or the docs only. The heads are separate implementations, so the same bug in two heads is two fixes.
- The fix touches no `shared-core-rs`, no rust core, and no UniFFI binding.
- The fix changes no Local API wire contract: no request shape, response shape, error code, or enum a client reads.
- The fix changes no cloud routing, provider dispatch or model id.

This test runs once, when the issue is picked. It does not run again after review. A review round
or a verify round can grow a small fix, and the review rounds are what catch a wrong fix, not the
diff size. PR #2035 on agentic-coding-school stalled for this reason on 2026-09-29: a verify-found
regression fix grew a 2-file change to 5 files, and the gate re-applied the size limit.

## 3. Tiers

Every issue that passed §2 is in one of two tiers. Decide the tier from the DIFF, after the build,
not from the issue title.

**Cosmetic.** Every changed file is in a cosmetic path (below), and the diff changes no logic. Copy,
class names, spacing, colour, icons, animation or transition values, and the layout of a component
that already exists are cosmetic. A new state, a new branch in code, a new prop that changes
behaviour, a data shape, an event handler, a fetch, a redirect, or a change to what a user CAN do is
logic, whatever file it sits in.

Cosmetic paths:
- Windows: `app/windows/HyperWhisper/**/*.xaml` and its string resources
- Linux: `app/linux/HyperWhisper.Linux/**/*.axaml` and `app/linux/HyperWhisper.Linux.Localization/**`
- macOS: SwiftUI view files under `app/macos/hyperwhisper/` and `.strings` files
- Web: `nextjs/src/**` markup and class names, `nextjs/src/content/**`
- Docs: `mintlify-help/**`

**Logic.** Everything else that passed §2.

## 4. Proof each tier needs

| Tier | Review | CI | Proof of the change |
|---|---|---|---|
| Cosmetic | two rounds, Claude + Codex | green | a before/after screenshot of every affected page or screen, `main` beside the branch, same viewport, same state |
| Logic | two rounds, Claude + Codex | green | the `verify` skill's run on the changed flow, outcome `passed` |

Cosmetic proof: capture on the cheapest matching head. Linux: a Namespace box (`namespace` + `hyperwhisper-desktop` skills). Windows: Ray's dev box (`windows-dev-box` skill). macOS: a rented Namespace Mac. Build `main` and the branch, launch each, capture the changed screen, and destroy a rented machine in the same turn. Web: a local boot and Playwright at 1280×800. Docs: no screenshot; run lychee and markdownlint on the diff instead. Publish each pair with `publish-media` and put the links in the
PR's `## Verified` section, one line per page. Upload the same files to the thread with
`slack-upload`. A cosmetic PR with no screenshot pair is not proved.

Logic proof follows the `verify` skill and the `task-lifecycle` verify round without change.

## 5. Merge conditions

Check them in order. Stop at the first failure. Merge only when all six are true.

1. The issue passed §2 at pick time.
2. Both review rounds finished. No CONFIRMED finding from Reviewer Claude and no [P1] from Reviewer
   Codex about lines this diff changed is left unfixed. A PLAUSIBLE finding, a [P2], or a CONFIRMED
   finding declined as out of scope with the reason written in the PR body does not block. Each one
   goes in the Slack reply.
3. The proof in §4 for the PR's tier is present.
   Logic tier: `failed`, `could_not_boot` and `ran_wrong` block. `unreachable_state` blocks ONLY on
   the flow the diff changed. A flow the diff did not change that is unreachable behind a hard-coded
   flag or a dead route does not block. Name the flag and the file in the PR body and in the reply.
   PR #2043 on agentic-coding-school stalled on 2026-09-29 for a renderer behind a flag hard-coded
   `false`, while the changed flow passed. That stall is the reason for this line.
   Cosmetic tier: a missing screenshot pair blocks.
4. Every workflow run on the PR head sha is `completed` with conclusion `success`. Read it with
   `gh api "repos/ray-amjad/hyperwhisper-app/actions/runs?head_sha=<full sha>"`. The GitHub App token cannot read
   commit statuses or check runs, so `gh pr checks` and `statusCheckRollup` are not evidence. Wait in
   the foreground for a `queued` or `in_progress` run. Zero runs returned is a failure. A run that is
   still not finished after a reasonable wait is a failure.
5. The PR carries no open question whose answer would change whether the fix is right. A question
   about follow-up work does not block.
6. The run did not guess at anything material. If you picked one of two readings, this condition
   fails. Say both readings in the reply.

Then:

```bash
gh pr merge <n> --repo ray-amjad/hyperwhisper-app --squash --delete-branch
gh pr view <n> --repo ray-amjad/hyperwhisper-app --json state      # must read MERGED before you claim it
```

A refused merge (branch protection, a conflict) is a failed gate. Do not write the merged marker.

If any condition fails, leave the PR open, write the `auto-dev:pr` marker with the condition that
failed, and tag Ray. That is a normal outcome.

## 6. Queue rule

Count the PRs THIS consumer opened and left for Ray: an open PR whose linked issue carries an
`auto-dev:pr` comment naming it. A human's PR is never in the count. A PR the consumer merged is not
open, so it is not in the count. At 3, reply with one line and stop. A PR nobody reviews is not
progress.

## 7. The production test account

The account is `r+percy-hyperwhisper@rayamjad.com`. Percy signs it up on the first run that needs it, through the web sign-in. The desktop app signs in with the same account.

Sign in: the web sign-in page sends a magic link (Better Auth `magicLink`). There is no password. A code or a link sent to any `r+<tag>@rayamjad.com` address lands in
`r@rayamjad.com`. Read it through the executor gateway's Gmail connection, newest message first.

What Percy may do with this account on production:

- Sign in, and create, edit or delete data INSIDE this account.
- Nothing that costs money: no checkout, no plan change, no seat purchase.
- No `/admin` route, and no other user's data, ever.
- Never delete the account, change its email, or change its password.

This is the ONE exception to the rule that production is read-only for Percy. The rule stands for
every other account.

## 8. After the merge

1. `mark-merged`.
2. Watch the production deploy for the merge sha until it is READY. Compare the merge sha against
   the newest READY deployment first. A merge deploys the web app and the cloud only. For a desktop change there is no production URL to check: the §4 capture on the head is the live check, and the change reaches users at the next release.
3. Check the change on production. One flow is the floor: the page or screen the PR changed. When
   that page is behind a login, sign in with the account in §7. When it is public, check it as a
   visitor. Drive it with Codex on the driver the `verify` skill pins. Put one evidence file per
   flow in the thread.
4. If the live check fails, repair it in the same run:
   - Fix forward once. Open one follow-up PR with the fix, run it through §5, and merge it when
     the gate opens. Tag Ray either way.
   - If the fix-forward PR cannot pass §5 in this run, revert. Open a PR that reverts the merge sha.
     A revert PR needs only condition 4 (CI green). It restores the state that was live before.
     Merge it, watch the deploy, and check the page again.
   - Never leave production broken and quiet. The reply names the merge sha, what broke, and which
     of the two repairs shipped.
