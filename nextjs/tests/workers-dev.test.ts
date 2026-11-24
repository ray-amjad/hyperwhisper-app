/**
 * Production Workers must not also be reachable on *.workers.dev.
 *
 * Wrangler publishes that hostname when `workers_dev` is true, and when the
 * key is omitted (the default is true). The website Worker is served on
 * www.hyperwhisper.com and hyperwhisper.com, attached in the Cloudflare
 * dashboard; `nextjs-deploy.yml` deploys this config with no `--env`. Nothing
 * in CI, scripts, or the current runbook calls a *.workers.dev URL.
 *
 * `preview_urls` is a different switch (already false; no per-PR previews)
 * and is not asserted here.
 *
 * A config counts as production when its top-level `name` has a `prod` /
 * `production` segment (hyperwhisper-web-prod), when it is the known website
 * config, or when an environment is named `production` or `prod`. Any other
 * Worker config is left alone: a future one that really uses workers.dev
 * should not fail this test.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, test } from "node:test";

const REPO_ROOT = fileURLToPath(new URL("../..", import.meta.url));

/** The Worker nextjs-deploy.yml ships. Kept even if its name ever drops "prod". */
const KNOWN_PRODUCTION_CONFIGS = ["nextjs/wrangler.jsonc"];

const CONFIG_NAMES = new Set(["wrangler.toml", "wrangler.json", "wrangler.jsonc"]);

/** Installed or generated trees. The walk does not enter them. */
const SKIP_DIRS = new Set([
  "node_modules",
  ".git",
  ".next",
  ".open-next",
  ".wrangler",
  ".vercel",
  "dist",
  "build",
  "coverage",
  "out",
  "target",
]);

const PRODUCTION_ENVS = new Set(["production", "prod"]);

interface WranglerSection {
  workers_dev?: boolean;
}

interface WranglerConfig {
  name?: string;
  workers_dev?: boolean;
  env?: Record<string, WranglerSection>;
}

function walk(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    if (entry.isDirectory()) {
      return SKIP_DIRS.has(entry.name) ? [] : walk(join(dir, entry.name));
    }
    return entry.isFile() && CONFIG_NAMES.has(entry.name) ? [join(dir, entry.name)] : [];
  });
}

/** wrangler.jsonc, with its full-line `//` comments removed. Same as cron-jobs.test.ts. */
function parseJsonc(raw: string, path: string): WranglerConfig {
  const json = raw
    .split("\n")
    .filter((line) => !/^\s*\/\//.test(line))
    .join("\n");
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch (error) {
    throw new Error(`Could not parse ${path} as JSONC: ${error instanceof Error ? error.message : error}`);
  }
  return asConfig(parsed, path);
}

/**
 * The keys this test cares about, from a wrangler.toml. Tables other than
 * the top level and `[env.<name>]` (including `[env.<name>.vars]`) do not
 * carry `workers_dev`.
 */
function parseToml(raw: string, path: string): WranglerConfig {
  const config: WranglerConfig = {};
  // Keys before any table, and keys in `[env.<name>]`, are the ones Wrangler
  // reads. A nested table such as `[vars]` or `[env.production.vars]` is not.
  let section: WranglerSection | null = config;
  let envName: string | null = null;

  raw.split("\n").forEach((line, index) => {
    const code = line.replace(/#.*$/, "").trim();
    if (code === "") return;

    const table = /^\[([^\]]+)\]$/.exec(code);
    if (table) {
      const name = table[1].trim();
      const env = /^env\.([A-Za-z0-9_-]+)$/.exec(name);
      if (env) {
        envName = env[1];
        config.env ??= {};
        const child: WranglerSection = config.env[envName] ?? {};
        config.env[envName] = child;
        section = child;
      } else {
        envName = null;
        section = null;
      }
      return;
    }

    const current = section;
    if (current === null) return;

    const field = /^([A-Za-z0-9_-]+)\s*=\s*(.+)$/.exec(code);
    if (!field) return;
    const key = field[1];
    const value = field[2].trim();

    if (key === "name" && envName === null) {
      const quoted = /^"(.*)"$/.exec(value);
      if (!quoted) {
        throw new Error(`${path}:${index + 1} has a non-string name`);
      }
      config.name = quoted[1];
      return;
    }

    if (key !== "workers_dev") return;
    if (value !== "true" && value !== "false") {
      throw new Error(`${path}:${index + 1} workers_dev must be true or false`);
    }
    current.workers_dev = value === "true";
  });

  return config;
}

function asConfig(value: unknown, path: string): WranglerConfig {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${path} is not a Wrangler config object`);
  }
  const record = value as Record<string, unknown>;
  const config: WranglerConfig = {};
  if (record.name !== undefined) {
    if (typeof record.name !== "string") throw new Error(`${path} name is not a string`);
    config.name = record.name;
  }
  config.workers_dev = booleanField(record.workers_dev, `${path} workers_dev`);
  if (record.env !== undefined) {
    if (record.env === null || typeof record.env !== "object" || Array.isArray(record.env)) {
      throw new Error(`${path} env is not an object`);
    }
    config.env = {};
    for (const [name, section] of Object.entries(record.env)) {
      if (section === null || typeof section !== "object" || Array.isArray(section)) {
        throw new Error(`${path} env.${name} is not an object`);
      }
      config.env[name] = {
        workers_dev: booleanField(
          (section as Record<string, unknown>).workers_dev,
          `${path} env.${name}.workers_dev`,
        ),
      };
    }
  }
  return config;
}

function booleanField(value: unknown, label: string): boolean | undefined {
  if (value === undefined) return undefined;
  if (typeof value !== "boolean") throw new Error(`${label} must be a boolean`);
  return value;
}

function parseConfig(path: string): WranglerConfig {
  const raw = readFileSync(path, "utf8");
  if (path.endsWith(".toml")) return parseToml(raw, path);
  return parseJsonc(raw, path);
}

/** `prod` or `production` as its own `-` / `_` segment. "product" does not match. */
function isProductionWorkerName(name: string | undefined): boolean {
  return name !== undefined && /(?:^|[-_])prod(?:uction)?(?:$|[-_])/i.test(name);
}

interface ProductionTarget {
  label: string;
  workers_dev: boolean | undefined;
}

/** Top-level when this file is a production Worker, plus every production environment. */
function productionTargets(path: string, config: WranglerConfig): ProductionTarget[] {
  const targets: ProductionTarget[] = [];
  const rel = relative(REPO_ROOT, path).split("\\").join("/");
  if (KNOWN_PRODUCTION_CONFIGS.includes(rel) || isProductionWorkerName(config.name)) {
    targets.push({ label: rel, workers_dev: config.workers_dev });
  }
  for (const [name, section] of Object.entries(config.env ?? {})) {
    if (!PRODUCTION_ENVS.has(name)) continue;
    targets.push({
      label: `${rel} env.${name}`,
      // Wrangler inherits an omitted environment key from the top level.
      workers_dev: section.workers_dev !== undefined ? section.workers_dev : config.workers_dev,
    });
  }
  return targets;
}

describe("wrangler.toml production sections", () => {
  test("inherits a top-level false and ignores workers_dev in nested tables", () => {
    const config = parseToml(
      [
        'name = "hyperwhisper-web-prod"',
        "workers_dev = false",
        "",
        "[vars]",
        "workers_dev = true",
        "",
        "[env.staging]",
        "workers_dev = true",
        "",
        "[env.production]",
        "# omitted here, so the top-level value applies",
        "",
        "[env.production.vars]",
        "workers_dev = true",
      ].join("\n"),
      "fixture.toml",
    );
    const targets = productionTargets(join(REPO_ROOT, "fixture.toml"), config);
    assert.deepEqual(
      targets.map((target) => [target.label, target.workers_dev]),
      [
        ["fixture.toml", false],
        ["fixture.toml env.production", false],
      ],
    );
  });
});

describe("production Workers do not publish *.workers.dev", () => {
  const configs = walk(REPO_ROOT);

  test("finds the website Worker config", () => {
    const rels = configs.map((path) => relative(REPO_ROOT, path).split("\\").join("/"));
    for (const expected of KNOWN_PRODUCTION_CONFIGS) {
      assert.ok(rels.includes(expected), `expected ${expected} among ${rels.join(", ") || "(none)"}`);
    }
  });

  test("workers_dev is false on every production config and production environment", () => {
    const targets = configs.flatMap((path) => productionTargets(path, parseConfig(path)));
    assert.ok(targets.length > 0, "no production Worker config was found");
    for (const target of targets) {
      assert.equal(
        target.workers_dev,
        false,
        `${target.label} workers_dev is ${String(target.workers_dev)}; ` +
          "omit-or-true publishes the Worker on *.workers.dev beside its custom domain",
      );
    }
  });
});
