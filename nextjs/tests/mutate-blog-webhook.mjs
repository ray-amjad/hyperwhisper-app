/**
 * Mutation proof for tests/blog-webhook.test.ts (issue #718).
 *
 * Each entry below does ONE exact string replace in the blog webhook route,
 * runs the one test file, then puts the source back. A mutant that still
 * PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-blog-webhook.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/blog-webhook.test.ts";
const ROUTE = "app/api/webhooks/add-blog-post/route.ts";

const READ_LOG = `    console.error("[add-blog-post] could not read request body", {
      contentType: req.headers.get("content-type"),
      err,
    });
`;
const PARSE_LOG = `    console.error("[add-blog-post] request body is not valid JSON", {
      contentType: req.headers.get("content-type"),
      bodyBytes: Buffer.byteLength(rawBody, "utf8"),
      errorName: err instanceof Error ? err.name : typeof err,
    });
`;

const MUTANTS = [
  { name: "drop the body-read log line", from: READ_LOG, to: "" },
  { name: "drop the JSON-parse log line", from: PARSE_LOG, to: "" },
  {
    name: "log the read error as its message only",
    from: `      contentType: req.headers.get("content-type"),\n      err,\n    });\n    return NextResponse.json({ error: "Invalid body" }`,
    to: `      contentType: req.headers.get("content-type"),\n      err: String(err),\n    });\n    return NextResponse.json({ error: "Invalid body" }`,
  },
  {
    name: "drop the content type from the read log",
    from: `could not read request body", {\n      contentType: req.headers.get("content-type"),`,
    to: `could not read request body", {`,
  },
  {
    name: "log the authorization header on the read path",
    from: `could not read request body", {\n      contentType: req.headers.get("content-type"),`,
    to: `could not read request body", {\n      contentType: req.headers.get("content-type"),\n      auth: req.headers.get("authorization"),`,
  },
  {
    name: "count UTF-16 units instead of UTF-8 bytes",
    from: `bodyBytes: Buffer.byteLength(rawBody, "utf8"),`,
    to: `bodyBytes: rawBody.length,`,
  },
  {
    name: "log the body itself on the parse path",
    from: `bodyBytes: Buffer.byteLength(rawBody, "utf8"),`,
    to: `bodyBytes: Buffer.byteLength(rawBody, "utf8"),\n      rawBody,`,
  },
  {
    name: "log the parse error whole on the parse path",
    from: `errorName: err instanceof Error ? err.name : typeof err,`,
    to: `err,`,
  },
  {
    name: "log the parse error message on the parse path",
    from: `errorName: err instanceof Error ? err.name : typeof err,`,
    to: `errorName: err instanceof Error ? err.message : typeof err,`,
  },
  {
    name: "log the parse error beside its name",
    from: `errorName: err instanceof Error ? err.name : typeof err,`,
    to: `errorName: err instanceof Error ? err.name : typeof err,\n      err,`,
  },
  {
    name: "answer 422 on invalid JSON",
    from: `{ error: "Invalid JSON" }, { status: 400 }`,
    to: `{ error: "Invalid JSON" }, { status: 422 }`,
  },
  {
    name: "change the Invalid body reply",
    from: `{ error: "Invalid body" }, { status: 400 }`,
    to: `{ error: "Unreadable body" }, { status: 400 }`,
  },
];

const results = [];
for (const mutant of MUTANTS) {
  const original = readFileSync(ROUTE, "utf8");
  const occurrences = original.split(mutant.from).length - 1;
  if (occurrences !== 1) {
    results.push({ ...mutant, verdict: `SKIPPED (${occurrences} matches)` });
    console.log(`SKIPPED   ${mutant.name}`);
    continue;
  }
  writeFileSync(ROUTE, original.replace(mutant.from, mutant.to));
  let killed = false;
  try {
    execFileSync(
      "node",
      ["--import", "tsx", "--experimental-test-module-mocks", "--test", TEST],
      { stdio: "pipe" },
    );
  } catch {
    killed = true;
  } finally {
    writeFileSync(ROUTE, original);
  }
  const verdict = killed ? "KILLED" : "SURVIVED";
  results.push({ ...mutant, verdict });
  console.log(`${verdict.padEnd(9)} ${mutant.name}`);
}

const bad = results.filter((r) => r.verdict !== "KILLED");
console.log(`\n${results.length - bad.length}/${results.length} killed`);
process.exitCode = bad.length === 0 ? 0 : 1;
