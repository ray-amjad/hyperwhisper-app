/**
 * Shared scaffolding for the tests that replace collaborators with
 * `mock.module`: the typed tracker, a URL helper for the specifiers, and the
 * "swallow the console and keep the lines" capture.
 *
 * This module only defines things. Importing it registers no mock, so a test
 * still registers its `moduleMock.module(...)` calls in its own body, before
 * its dynamic `import()` of the module under test.
 */
/* eslint-disable no-console -- the capture swaps console methods by design */
import { mock } from "node:test";
import { inspect } from "node:util";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrow the tracker to the one method used here rather
 * than bumping the types in a test-only change.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: {
      namedExports?: Record<string, unknown>;
      defaultExport?: unknown;
    },
  ): void;
}

export const moduleMock = mock as unknown as ModuleMocker;

/**
 * Resolves `relative` against this file, which sits in `nextjs/tests/` beside
 * every caller, so a caller passes the same path it would resolve itself.
 */
export function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

/**
 * Renders console arguments as Node's console does: a string as is, anything
 * else through `util.inspect`, so a stack, a bound param or a `cause` hiding in
 * a logged error stays visible to a leak check. Re-exported by
 * `db-error-fixture.ts`; it lives here so a test that only wants the capture
 * does not load `pg` and `drizzle-orm` through that fixture.
 */
export function formatLogArgs(args: unknown[]): string {
  return args
    .map((a) => (typeof a === "string" ? a : inspect(a, { depth: 6 })))
    .join(" ");
}

export type ConsoleLevel = "log" | "warn" | "error";

export interface ConsoleCapture {
  /** One rendered line per call; prefixed `"<level> "` when more than 1 level is captured. */
  readonly lines: string[];
  /** Puts back the console methods this capture replaced. Safe to call twice. */
  restore(): void;
}

/**
 * Replaces `console[level]` for each level with a recorder that swallows the
 * call and pushes a {@link formatLogArgs} line onto `lines` (a new array unless
 * the caller passes one it already exports).
 */
export function captureConsole(
  levels: ConsoleLevel[],
  lines: string[] = [],
): ConsoleCapture {
  const prefixed = levels.length > 1;
  const originals = levels.map((level) => [level, console[level]] as const);

  for (const level of levels) {
    console[level] = (...args: unknown[]): void => {
      lines.push(
        prefixed ? `${level} ${formatLogArgs(args)}` : formatLogArgs(args),
      );
    };
  }
  let restored = false;

  return {
    lines,
    restore(): void {
      if (restored) return;
      restored = true;
      for (const [level, original] of originals) console[level] = original;
    },
  };
}

/**
 * A harness's `silence…` / `restore…` pair over one shared `lines` array.
 * `silence` is idempotent and `restore` puts back the console as it was before
 * the first `silence`, as the module-level `realConsole` copies did.
 */
export function consoleSilencer(
  levels: ConsoleLevel[],
  lines: string[],
): { silence(): void; restore(): void } {
  let active: ConsoleCapture | null = null;

  return {
    silence(): void {
      active ??= captureConsole(levels, lines);
    },
    restore(): void {
      active?.restore();
      active = null;
    },
  };
}
