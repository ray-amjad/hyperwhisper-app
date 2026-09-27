/**
 * Mutation proof for tests/download-route.test.ts.
 *
 * Each entry below does ONE exact string replace in the download route,
 * runs the one test file, then puts the source back. A mutant that still
 * PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-download-route.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/download-route.test.ts";

const ROUTE = "app/api/download/route.ts";

const MUTANTS = [
  {
    file: ROUTE,
    name: "time out after 30 s, not 3 s",
    from: "const APPCAST_FETCH_TIMEOUT_MS = 3000;",
    to: "const APPCAST_FETCH_TIMEOUT_MS = 30000;",
  },
  {
    file: ROUTE,
    name: "drop the fetch timeout",
    from: "      signal: AbortSignal.timeout(APPCAST_FETCH_TIMEOUT_MS),\n",
    to: "",
  },
  {
    file: ROUTE,
    name: "let the appcast be cached",
    from: "      cache: \"no-store\",",
    to: "      cache: \"default\",",
  },
  {
    file: ROUTE,
    name: "stop forcing dynamic rendering",
    from: "export const dynamic = \"force-dynamic\";",
    to: "export const dynamic = \"auto\";",
  },
  {
    file: ROUTE,
    name: "read the Mac feed for Windows",
    from: "return platform === \"windows\" ? \"appcast-windows.xml\" : \"appcast.xml\";",
    to: "return platform === \"windows\" ? \"appcast.xml\" : \"appcast.xml\";",
  },
  {
    file: ROUTE,
    name: "accept any arch value",
    from: "return input === \"x64\" || input === \"arm64\" ? input : null;",
    to: "return input === \"x64\" || input === \"arm64\" ? input : (input as WindowsArch | null);",
  },
  {
    file: ROUTE,
    name: "drop arm64 from the allow-list",
    from: "return input === \"x64\" || input === \"arm64\" ? input : null;",
    to: "return input === \"x64\" ? input : null;",
  },
  {
    file: ROUTE,
    name: "compare the UA without lowercasing",
    from: "const ua = userAgent.toLowerCase();",
    to: "const ua = userAgent;",
  },
  {
    file: ROUTE,
    name: "stop matching a bare 'arm'",
    from: "if (ua.includes(\"arm64\") || ua.includes(\"arm\")) {",
    to: "if (ua.includes(\"arm64\")) {",
  },
  {
    file: ROUTE,
    name: "default unknown UAs to arm64",
    from: "  return \"x64\";\n}",
    to: "  return \"arm64\";\n}",
  },
  {
    file: ROUTE,
    name: "let the UA override an explicit arch",
    from: "const arch = explicitArch || detectWindowsArch(userAgent);",
    to: "const arch = detectWindowsArch(userAgent) || explicitArch;",
  },
  {
    file: ROUTE,
    name: "ignore the sparkle:os filter",
    from: "<item>[\\\\s\\\\S]*?<sparkle:os>${osValue}</sparkle:os>[\\\\s\\\\S]*?<enclosure",
    to: "<item>[\\\\s\\\\S]*?<enclosure",
  },
  {
    file: ROUTE,
    name: "take the LAST Mac item",
    from: "match = xml.match(/<item>[\\s\\S]*?<enclosure[^>]*url=\"([^\"]+)\"/i);",
    to: "match = [...xml.matchAll(/<item>[\\s\\S]*?<enclosure[^>]*url=\"([^\"]+)\"/gi)].at(-1) ?? null;",
  },
  {
    file: ROUTE,
    name: "serve a non-2xx appcast",
    from: "if (!res.ok) return null;",
    to: "if (false) return null;",
  },
  {
    file: ROUTE,
    name: "skip URL validation",
    from: "const latestUrl = new URL(match[1]);",
    to: "const latestUrl = { toString: () => match![1] };",
  },
  {
    file: ROUTE,
    name: "stop logging a parse fault",
    from: "console.error(\"Error parsing appcast:\", error);",
    to: "",
  },
  {
    file: ROUTE,
    name: "treat 'Windows' as a platform value too",
    from: "return input === \"windows\" ? \"windows\" : \"mac\";",
    to: "return input?.toLowerCase() === \"windows\" ? \"windows\" : \"mac\";",
  },
  {
    file: ROUTE,
    name: "fetch the appcast from a fixed host",
    from: "const appcastUrl = `${origin}/${getAppcastFilename(platform)}`;",
    to: "const appcastUrl = `https://www.hyperwhisper.com/${getAppcastFilename(platform)}`;",
  },
  {
    file: ROUTE,
    name: "answer 404 on no URL",
    from: "        { status: 500 },\n      );\n    }\n\n    // Redirect",
    to: "        { status: 404 },\n      );\n    }\n\n    // Redirect",
  },
  {
    file: ROUTE,
    name: "redirect with 308",
    from: "return NextResponse.redirect(downloadUrl);",
    to: "return NextResponse.redirect(downloadUrl, 308);",
  },
];

const results = [];

for (const mutant of MUTANTS) {
  const original = readFileSync(mutant.file, "utf8");
  const occurrences = original.split(mutant.from).length - 1;
  if (occurrences !== 1) {
    results.push({ ...mutant, verdict: `SKIPPED (${occurrences} matches)` });
    console.log(`SKIPPED   ${mutant.name}`);
    continue;
  }

  let mutated = original.replace(mutant.from, mutant.to);
  if (mutant.prelude) {
    mutated = mutated.replace(mutant.prelude.from, mutant.prelude.to);
  }
  writeFileSync(mutant.file, mutated);

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
    writeFileSync(mutant.file, original);
  }

  const verdict = killed
    ? "KILLED"
    : mutant.equivalent
      ? "SURVIVED (equivalent)"
      : "SURVIVED";
  results.push({ ...mutant, verdict });
  console.log(`${verdict.padEnd(21)} ${mutant.name}`);
}

const survivors = results.filter(
  (r) => r.verdict !== "KILLED" && r.verdict !== "SURVIVED (equivalent)",
);
console.log("");
console.log(`| File | Mutation | Verdict |`);
console.log(`| --- | --- | --- |`);
for (const r of results) console.log(`| \`${r.file}\` | ${r.name} | ${r.verdict} |`);
console.log("");
const killedCount = results.filter((r) => r.verdict === "KILLED").length;
console.log(
  `${killedCount}/${results.length} killed, ` +
    `${results.length - killedCount - survivors.length} equivalent, ` +
    `${survivors.length} hollow`,
);
process.exit(survivors.length === 0 ? 0 : 1);
