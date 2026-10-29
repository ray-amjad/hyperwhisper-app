#!/usr/bin/env node
// @ts-check
/**
 * Runs in the Workers Builds deploy command, before `opennextjs-cloudflare
 * deploy` (DEPLOY-CLOUDFLARE.md). It holds the deploy back until the
 * migrations the commit ships have been applied.
 *
 * On Vercel, `db:migrate` ran inside the build, so a failed migration kept the
 * old code serving. Now `.github/workflows/nextjs-migrate.yml` applies them
 * from GitHub Actions while Workers Builds builds the same push, in parallel.
 * Without this gate a failed (or not yet finished) migration would leave the
 * new Worker serving against the old schema.
 *
 * - The commit touches none of MIGRATION_PATHS: exit 0 at once.
 * - Otherwise poll the GitHub API (public repo, no token needed) for the
 *   nextjs-migrate run on this commit on `main`, and exit 0 only once one has
 *   concluded `success`. A failed run, no run, or a timeout exits 1 and the
 *   deploy does not happen; re-run the migration, then retry the build.
 *
 * Optional `GITHUB_TOKEN` (build variable) only raises the API rate limit.
 */
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

/** Must match the `push.paths` of nextjs-migrate.yml (minus the workflow file). */
export const MIGRATION_PATHS = Object.freeze(["nextjs/drizzle/", "nextjs/src/db/"]);
export const MIGRATE_WORKFLOW = "nextjs-migrate.yml";
const REPO = process.env.GITHUB_REPOSITORY || "ray-amjad/hyperwhisper-app";
const POLL_MS = 20_000;
/** How long a run may take to appear after the push. */
const APPEAR_TIMEOUT_MS = 4 * 60_000;
/** Workers Builds stops a build at 20 minutes; leave room for the upload. */
const TOTAL_TIMEOUT_MS = 10 * 60_000;

/**
 * @param {readonly string[]} files repo-relative paths
 * @returns {boolean}
 */
export function touchesMigrations(files) {
  return files.some((file) => MIGRATION_PATHS.some((prefix) => file.startsWith(prefix)));
}

/**
 * The verdict for the runs of nextjs-migrate on one commit.
 * @param {ReadonlyArray<{ head_branch: string | null; status: string; conclusion: string | null; html_url?: string }>} runs
 * @returns {"success" | "pending" | "failed" | "none"}
 */
export function migrateVerdict(runs) {
  const onMain = runs.filter((run) => run.head_branch === "main");
  if (onMain.length === 0) return "none";
  if (onMain.some((run) => run.status === "completed" && run.conclusion === "success")) {
    return "success";
  }
  if (onMain.some((run) => run.status !== "completed")) return "pending";
  return "failed";
}

/**
 * @param {string} path
 * @param {{ notFoundAs?: unknown }} [options] value to return on a 404
 */
async function github(path, options = {}) {
  /** @type {Record<string, string>} */
  const headers = {
    accept: "application/vnd.github+json",
    "user-agent": "hyperwhisper-web-deploy",
    "x-github-api-version": "2022-11-28",
  };
  if (process.env.GITHUB_TOKEN) headers.authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  const response = await fetch(`https://api.github.com/repos/${REPO}${path}`, { headers });
  if (response.status === 404 && "notFoundAs" in options) return options.notFoundAs;
  if (!response.ok) {
    throw new Error(`GitHub API ${path} -> ${response.status}: ${(await response.text()).slice(0, 300)}`);
  }
  return response.json();
}

/**
 * Repo-relative paths the commit changed against its first parent: local git
 * when the clone has the parent, the GitHub API otherwise.
 * @param {string} sha
 * @returns {Promise<string[]>}
 */
async function changedFiles(sha) {
  try {
    const out = execFileSync("git", ["diff", "--name-only", `${sha}^1`, sha], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    });
    return out.split("\n").filter(Boolean);
  } catch {
    const commit = await github(`/commits/${sha}`);
    /** @type {string[]} */
    const files = (commit.files ?? []).map((/** @type {{ filename: string }} */ f) => f.filename);
    // The API lists at most 300 files; assume a bigger commit has a migration.
    return files.length >= 300 ? [...files, MIGRATION_PATHS[0]] : files;
  }
}

/** @param {number} ms */
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function main() {
  const sha =
    process.env.WORKERS_CI_COMMIT_SHA ||
    execFileSync("git", ["rev-parse", "HEAD"], { encoding: "utf8" }).trim();

  if (!touchesMigrations(await changedFiles(sha))) {
    console.log(`wait-for-migrations: ${sha.slice(0, 7)} ships no migration; deploying.`);
    return;
  }

  console.log(`wait-for-migrations: ${sha.slice(0, 7)} touches ${MIGRATION_PATHS.join(" or ")}; waiting for ${MIGRATE_WORKFLOW}.`);
  const started = Date.now();
  for (;;) {
    const { workflow_runs: runs = [] } = await github(
      `/actions/workflows/${MIGRATE_WORKFLOW}/runs?head_sha=${sha}&per_page=20`,
      // The push that adds the workflow file may not have registered it yet.
      { notFoundAs: {} },
    );
    const verdict = migrateVerdict(runs);
    const elapsed = Date.now() - started;

    if (verdict === "success") {
      console.log("wait-for-migrations: migration applied; deploying.");
      return;
    }
    if (verdict === "failed") {
      throw new Error(
        `${MIGRATE_WORKFLOW} failed for ${sha} (${runs[0]?.html_url ?? "no url"}). ` +
          "Not deploying against the old schema: fix and re-run the migration, then retry this build.",
      );
    }
    if (verdict === "none" && elapsed > APPEAR_TIMEOUT_MS) {
      throw new Error(
        `no ${MIGRATE_WORKFLOW} run on main for ${sha} after ${APPEAR_TIMEOUT_MS / 60_000} min. ` +
          "Run it (Actions -> nextjs-migrate -> Run workflow, branch main), then retry this build.",
      );
    }
    if (elapsed > TOTAL_TIMEOUT_MS) {
      throw new Error(`${MIGRATE_WORKFLOW} still running for ${sha} after ${TOTAL_TIMEOUT_MS / 60_000} min; retry this build once it succeeds.`);
    }
    console.log(`wait-for-migrations: ${verdict === "none" ? "no run yet" : "running"}; checking again in ${POLL_MS / 1000}s.`);
    await sleep(POLL_MS);
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(`wait-for-migrations: ${error instanceof Error ? error.message : error}`);
    process.exit(1);
  });
}
