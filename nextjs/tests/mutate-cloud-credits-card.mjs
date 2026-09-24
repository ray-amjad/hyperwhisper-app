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
