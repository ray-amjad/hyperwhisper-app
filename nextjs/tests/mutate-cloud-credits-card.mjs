/**
 * Mutation harness for the #737 buy-credits chain. Applies ONE mutation at a
 * time, runs the whole suite, reports which tests died, and restores the file.
 *
 * A green suite is not evidence. #881 review round 2 proved that here: the
 * whole suite passed with the header button's `onClick` deleted, because
 * `renderToStaticMarkup` never serialises a handler. Every row below is an
 * observed run, not a prediction.
 *
 * Usage (from `nextjs/`):
 *   node tests/mutate-cloud-credits-card.mjs a
 *   node tests/mutate-cloud-credits-card.mjs all    # every row, in order
 *
 * Exit 0 means the mutation was KILLED (at least one test failed). Exit 1 means
 * it SURVIVED and the suite has a hole. Exit 2 means the anchor text moved and
 * the mutation never applied — fix the anchor, never report that as a kill.
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const VIEW = "components/customer/dashboard/CloudCreditsCardView.tsx";
const WRAPPER = "components/customer/dashboard/CloudCreditsCard.tsx";
const LIB = "src/lib/buy-credits.ts";
const WATCH = "src/lib/abandoned-redirect.ts";

const mutations = {
  a: {
    file: VIEW,
    describe: 'delete the role="alert" region from the View',
    expect: "cloud-credits-card-view + cloud-credits-card-error-state",
    apply: (s) =>
      s.replace(
        `          {error && (
            <div className="mt-2">
              <span className="text-red-300 text-sm" role="alert">
                {error}
              </span>
            </div>
          )}
`,
        "",
      ),
  },
  b: {
    file: VIEW,
    describe: "delete onClick from the tier buttons",
    expect: "cloud-credits-card-view (the click test)",
    apply: (s) =>
      s.replace(
        "                  onClick={() => onBuy(tier.amount, tier.amount)}\n",
        "",
      ),
  },
  c: {
    file: VIEW,
    describe: "delete disabled={isDisabled} from the tier buttons",
    expect: "cloud-credits-card-view (the busy test)",
    apply: (s) => s.replace("                  disabled={isDisabled}\n", ""),
  },
  d: {
    file: LIB,
    describe: "drop `response.ok &&` from the redirect guard",
    expect: "buy-credits-seam (the refused-response-with-a-checkoutUrl test)",
    apply: (s) =>
      s.replace(
        "if (response.ok && isRecord(data) && typeof data.checkoutUrl === \"string\")",
        "if (isRecord(data) && typeof data.checkoutUrl === \"string\")",
      ),
  },
  e: {
    file: LIB,
    describe: "delete the onError call on the failing branch",
    expect: "buy-credits-seam (the 400 and 500 tests)",
    apply: (s) =>
      s.replace(
        `  onError(
    isRecord(data) && typeof data.error === "string" && data.error !== ""
      ? data.error
      : checkoutErrorMessage,
  );

`,
        "",
      ),
  },
  f: {
    file: LIB,
    describe: "let the failing branch navigate too",
    expect: "buy-credits-seam (the 400 test)",
    // A refusal bounces the customer to the public credits page instead of
    // telling them what went wrong — silence of the same shape #737 is about,
    // and a redirect no markup assertion could ever see.
    apply: (s) =>
      s.replace(
        "  onError(\n    isRecord(data)",
        '  navigate("/credits");\n\n  onError(\n    isRecord(data)',
      ),
  },
  g: {
    file: WRAPPER,
    describe: "restore origin/main:nextjs/.../CloudCreditsCard.tsx",
    expect: "cloud-credits-card-wiring + cloud-credits-card-error-state",
    apply: () =>
      execFileSync(
        "git",
        [
          "show",
          "origin/main:nextjs/components/customer/dashboard/CloudCreditsCard.tsx",
        ],
        { encoding: "utf8" },
      ),
  },
  h: {
    file: LIB,
    describe: "delete the reportError call on the failing branch",
    expect: "buy-credits-seam (the reporting tests)",
    apply: (s) =>
      s.replace(
        `  reportError(
    new Error(\`\${BUY_CREDITS_OPERATION} failed: \${response.status}\`),
    {
      operation: BUY_CREDITS_OPERATION,
      status: response.status,
    },
  );

`,
        "",
      ),
  },
  i: {
    file: WRAPPER,
    describe: "the View is handed error={null} instead of error={error}",
    expect: "cloud-credits-card-error-state ONLY",
    // The row the whole error-state file exists for. The seam still calls
    // `setError`, the View still paints an `error` prop, and the wiring test
    // still sees a `setError` function — three green files, and the customer
    // is back to a spinner that stops and nothing else.
    apply: (s) => s.replace("      error={error}\n", "      error={null}\n"),
  },
  j: {
    file: WRAPPER,
    describe: "drop setError from the object handed to createBuyCreditsHandler",
    expect: "cloud-credits-card-error-state + cloud-credits-card-wiring",
    apply: (s) =>
      s.replace("    setBusy: setLoadingTier,\n    setError,\n", "    setBusy: setLoadingTier,\n"),
  },

  // ─── #947 review round 1 ────────────────────────────────────────────────
  // k–n are findings 1 and 3, which are one rule: the busy flag has exactly
  // one owner on every exit, and no exit has none. o–p are finding 2.

  k: {
    file: LIB,
    describe: "let a collaborator's throw escape the handler again",
    expect: "buy-credits-seam (the navigate-throws and ad-blocker tests)",
    // Finding 1 exactly as it stood: the handler rejects under a `void` call
    // site (an unhandled rejection at the customer) and `loadingTier` is never
    // released, so the whole buy block is dead until a reload.
    apply: (s) =>
      s.replace(
        "      reportHandlerFault(thrown, reportError);\n    }\n\n    // A plain statement",
        "      throw thrown;\n    }\n\n    // A plain statement",
      ),
  },
  l: {
    file: LIB,
    describe: "drop the busy flag on the floor once a redirect is scheduled",
    expect: "buy-credits-seam (the scheduled-redirect handover test)",
    // Finding 3 exactly as it stood. Every other test in the file still
    // passes: a scheduled redirect looked identical to one that committed.
    apply: (s) =>
      s.replace(
        "      if (navigated) onRedirectScheduled(() => setBusy(null));\n      else setBusy(null);",
        "      if (!navigated) setBusy(null);",
      ),
  },
  m: {
    file: WATCH,
    describe: "re-arm the card on ANY pageshow, not only a bfcache restore",
    expect: "abandoned-redirect (the ordinary-page-load test)",
    apply: (s) =>
      s.replace("    if (event.persisted) finish();", "    finish();"),
  },
  n: {
    file: WATCH,
    describe: "leave the pageshow listener registered after releasing",
    expect: "abandoned-redirect (the bfcache, once and two-checkouts tests)",
    // What makes the release at-most-once. A `released` boolean was written
    // here first; this harness proved it unreachable, so the teardown IS the
    // guard and this row is what holds it.
    apply: (s) =>
      s.replace('    host.removeEventListener("pageshow", onPageShow);\n', ""),
  },
  // Row `r` — "leave the timer running after releasing" — was DELETED in #947
  // review round 2. Its target code is gone: `watchForAbandonedRedirect` no
  // longer arms a timer at all, because a timer fires on a slow-but-real
  // redirect as readily as on an abandoned one. Row `v` below is the row that
  // now guards that deletion.
  o: {
    file: WRAPPER,
    describe: "interpolate the minutes line unconditionally again",
    expect: "cloud-credits-card-wiring (the no-minutes test)",
    // Finding 2 on the wrapper's side: the label always carries a string, so
    // the decision moves back into whoever paints it.
    apply: (s) =>
      s.replace(
        `        minutesRemaining:
          totalMinutesRemaining > 0
            ? t("minutesRemaining", { minutes: totalMinutesRemaining })
            : null,`,
        `        minutesRemaining: t("minutesRemaining", {
          minutes: totalMinutesRemaining,
        }),`,
      ),
  },
  p: {
    file: VIEW,
    describe: "paint the minutes line whatever the label says",
    expect: "cloud-credits-card-view (the null-label test)",
    // The other half of finding 2, and the reason its test asserts on the
    // container's class: with a null label this renders an EMPTY `<p>`, whose
    // bytes contain no "minutes remaining" for a text-only assertion to miss.
    apply: (s) =>
      s.replace(
        `        {labels.minutesRemaining && (
          <p className="text-sm text-gray-400 mt-0.5">
            {labels.minutesRemaining}
          </p>
        )}`,
        `        <p className="text-sm text-gray-400 mt-0.5">
          {labels.minutesRemaining}
        </p>`,
      ),
  },
  q: {
    file: WRAPPER,
    describe: "drop onRedirectScheduled from the object the card builds",
    expect: "cloud-credits-card-wiring (the collaborators and window tests)",
    apply: (s) =>
      s.replace(
        `    onRedirectScheduled: (release) => {
      watchForAbandonedRedirect(window, release);
    },
`,
        "",
      ),
  },

  // ─── #947 review round 2 ────────────────────────────────────────────────
  // s–t are finding 3: the raw `data.error` is shown on a 4xx and never on a
  // 5xx. One row per side, because a single row could be killed by the half
  // of the split that was already there before this round.

  s: {
    file: LIB,
    describe: "paint the route's 5xx words at the customer again",
    expect: "buy-credits-seam (the 500-real-body and 503 tests)",
    // Finding 3 exactly as it stood: `route.ts:218-227` answers every
    // unhandled throw with `{ error: "Failed to create checkout session",
    // details: <the throw's message> }`, and that went verbatim into a
    // 40-locale alert region. The empty-body 500 test still passes with this
    // applied, which is why finding 5 had to replace that fixture first.
    apply: (s) =>
      s.replace(
        "    response.status < 500 && serverMessage !== \"\"",
        "    serverMessage !== \"\"",
      ),
  },
  t: {
    file: LIB,
    describe: "hide the route's 4xx words behind the translated copy",
    expect: "buy-credits-seam (both 400 tests, and the refused-with-a-URL test)",
    // The other side of the split. #737's `## Proposed fix` and `## Done when`
    // both pin "Amount too large" on screen verbatim, so a fix that swept ALL
    // statuses into the translated copy would fail the issue's own acceptance
    // test. These rows are what stop the split being flattened either way.
    apply: (s) =>
      s.replace(
        `  onError(
    response.status < 500 && serverMessage !== ""
      ? serverMessage
      : checkoutErrorMessage,
  );`,
        "  onError(checkoutErrorMessage);",
      ),
  },
  v: {
    file: WATCH,
    describe: "put the 20-second abandoned-redirect timer back",
    expect: "abandoned-redirect (the arms-no-clock and no-timer-in-source tests)",
    // Finding 1 exactly as round 1 shipped it. `location.href` leaves this
    // document live and interactive until the new response's first byte, so a
    // `checkout.stripe.com` TTFB past the timeout re-armed all four controls
    // while the real redirect was still in flight — and a second click POSTed
    // again and created a second checkout session for the same top-up.
    //
    // This row is a RESTORATION, not a corruption: it is the deleted code,
    // pasted back. Everything else in the file still passes with it applied,
    // which is the measurement that says the two new tests are the only thing
    // standing between this branch and the money-path fault.
    apply: (s) =>
      s
        .replace(
          `  removeEventListener: (
    type: "pageshow",
    listener: (event: RedirectPageShowEvent) => void,
  ) => void;
}`,
          `  removeEventListener: (
    type: "pageshow",
    listener: (event: RedirectPageShowEvent) => void,
  ) => void;
  setTimeout: (handler: () => void, timeout: number) => number;
  clearTimeout: (id: number) => void;
}`,
        )
        .replace(
          `  host: RedirectWatchHost,
  release: () => void,
): void {
  function finish(): void {`,
          `  host: RedirectWatchHost,
  release: () => void,
  timeoutMs: number = 20_000,
): void {
  let timer: number | undefined;

  function finish(): void {`,
        )
        .replace(
          `    host.removeEventListener("pageshow", onPageShow);

    release();`,
          `    host.removeEventListener("pageshow", onPageShow);

    if (timer !== undefined) host.clearTimeout(timer);

    release();`,
        )
        .replace(
          '  host.addEventListener("pageshow", onPageShow);\n}',
          '  host.addEventListener("pageshow", onPageShow);\n  timer = host.setTimeout(finish, timeoutMs);\n}',
        ),
  },
};

function runOne(key) {
  const mutation = mutations[key];

  if (!mutation) {
    console.error(`unknown mutation: ${key}`);
    process.exit(2);
  }

  const original = readFileSync(mutation.file, "utf8");
  const mutated = mutation.apply(original);

  if (mutated === original) {
    console.error(`MUTATION ${key} DID NOT APPLY — the anchor text moved`);
    process.exit(2);
  }

  writeFileSync(mutation.file, mutated);
  console.log(`### mutation ${key}: ${mutation.describe}  (${mutation.file})`);
  console.log(`    expected to kill: ${mutation.expect}`);

  let output = "";
  let exitCode = 0;

  try {
    output = execFileSync("npm", ["test"], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
  } catch (thrown) {
    exitCode = thrown.status ?? 1;
    output = `${thrown.stdout ?? ""}${thrown.stderr ?? ""}`;
  } finally {
    writeFileSync(mutation.file, original);
  }

  const failed = [
    ...new Set(
      [...output.matchAll(/^\s*not ok \d+ - (.+)$/gm)].map((m) => m[1].trim()),
    ),
  ];
  const counts = output.match(/^# (?:tests|pass|fail) \d+$/gm) ?? [];

  console.log(`npm test exit: ${exitCode}`);
  console.log(counts.join("\n"));
  console.log(
    failed.length
      ? `KILLED ${failed.length} test(s):\n${failed.map((n) => `  - ${n}`).join("\n")}`
      : "SURVIVED — no test failed. The mutation is not covered.",
  );
  console.log(`restored ${mutation.file}\n`);

  return failed;
}

const key = process.argv[2];

if (key === "all") {
  const survivors = [];

  for (const each of Object.keys(mutations)) {
    if (runOne(each).length === 0) survivors.push(each);
  }

  console.log(
    survivors.length
      ? `SURVIVORS: ${survivors.join(", ")}`
      : "every mutation was killed",
  );
  process.exit(survivors.length ? 1 : 0);
}

process.exit(runOne(key).length ? 0 : 1);
