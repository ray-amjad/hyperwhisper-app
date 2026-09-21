/**
 * Mutation harness for the #881 sign-out seam. Applies ONE mutation at a time,
 * runs the whole suite, reports which tests died, and restores the file.
 *
 * Usage (from `nextjs/`): node tests/mutate-user-header.mjs a
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const VIEW = "components/user/UserHeaderView.tsx";
const WRAPPER = "components/user/UserHeader.tsx";
const LIB = "src/lib/sign-out.ts";

const mutations = {
  a: {
    file: VIEW,
    describe: "delete onClick from the button",
    apply: (s) => s.replace("            onClick={onSignOut}\n", ""),
  },
  b: {
    file: VIEW,
    describe: "delete disabled={signingOut}",
    apply: (s) => s.replace("            disabled={signingOut}\n", ""),
  },
  c: {
    file: VIEW,
    describe: 'delete the whole role="alert" region',
    apply: (s) =>
      s.replace(
        `          {signOutError && (
            <span className="text-red-300 text-sm" role="alert">
              {signOutError}
            </span>
          )}

`,
        "",
      ),
  },
  d: {
    file: VIEW,
    describe: 'the busy label always reads "Sign Out"',
    apply: (s) =>
      s.replace('{signingOut ? "Signing Out..." : "Sign Out"}', "Sign Out"),
  },
  e: {
    file: WRAPPER,
    describe: "restore origin/main:nextjs/components/user/UserHeader.tsx",
    apply: () =>
      execFileSync(
        "git",
        ["show", "origin/main:nextjs/components/user/UserHeader.tsx"],
        { encoding: "utf8" },
      ),
  },
  f: {
    file: LIB,
    describe: "the success path clears the busy flag too",
    apply: (s) => s.replace("if (!navigated) setBusy(false);", "setBusy(false);"),
  },
};

const key = process.argv[2];
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

let output = "";
let exitCode = 0;

try {
  output = execFileSync(
    "npm",
    ["test"],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  );
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
console.log(`restored ${mutation.file}`);
process.exit(failed.length ? 0 : 1);
