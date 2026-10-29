/**
 * scripts/wait-for-migrations.mjs gates the Workers deploy on nextjs-migrate.
 * Its path list must match the workflow's trigger, or a migration commit
 * deploys without waiting (or waits for a run that never starts).
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, test } from "node:test";

import {
  MIGRATE_WORKFLOW,
  MIGRATION_PATHS,
  migrateVerdict,
  touchesMigrations,
} from "../scripts/wait-for-migrations.mjs";

const workflow = readFileSync(
  new URL(`../../.github/workflows/${MIGRATE_WORKFLOW}`, import.meta.url),
  "utf8",
);

describe("wait-for-migrations", () => {
  test("MIGRATION_PATHS are the workflow's push paths", () => {
    const paths = Array.from(workflow.matchAll(/^\s+- '([^']+)'$/gm))
      .map((m) => m[1])
      .filter((p) => !p.startsWith(".github/"))
      .map((p) => p.replace(/\*\*$/, ""));
    assert.deepEqual([...paths].sort(), [...MIGRATION_PATHS].sort());
  });

  test("the migrate job runs only on main", () => {
    assert.match(workflow, /if: github\.ref == 'refs\/heads\/main'/);
  });

  test("touchesMigrations", () => {
    assert.equal(touchesMigrations(["nextjs/drizzle/0007_x.sql"]), true);
    assert.equal(touchesMigrations(["nextjs/src/db/schema/auth.ts"]), true);
    assert.equal(touchesMigrations(["nextjs/app/page.tsx", "nextjs/public/appcast.xml"]), false);
    assert.equal(touchesMigrations([]), false);
  });

  test("migrateVerdict", () => {
    const run = (status: string, conclusion: string | null, head_branch = "main") => ({
      status,
      conclusion,
      head_branch,
    });
    assert.equal(migrateVerdict([]), "none");
    assert.equal(migrateVerdict([run("completed", "success", "feature")]), "none");
    assert.equal(migrateVerdict([run("in_progress", null)]), "pending");
    assert.equal(migrateVerdict([run("queued", null)]), "pending");
    assert.equal(migrateVerdict([run("completed", "failure")]), "failed");
    assert.equal(migrateVerdict([run("completed", "skipped")]), "failed");
    // A manual re-run that succeeded unblocks the deploy.
    assert.equal(
      migrateVerdict([run("completed", "success"), run("completed", "failure")]),
      "success",
    );
  });
});
