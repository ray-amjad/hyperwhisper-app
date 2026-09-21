/**
 * Test harness for `lib/services/license-key.ts`.
 *
 * The generator draws every character through `crypto.randomInt`, and its
 * bounds checks and its retry loop only run when that draw misbehaves. So the
 * harness replaces the `crypto` module at the boundary with a scriptable
 * stand-in: it delegates to the real module for everything, and lets a test
 * decide what each `randomInt` call answers, or that it throws.
 *
 * A test that wants real randomness leaves `randomInt.script` empty, and the
 * draws go to the real `crypto.randomInt`.
 *
 * The test file must NEVER import `lib/services/license-key.ts` itself, or it
 * binds to the real `crypto` and the fault branches stay unreachable.
 */
import realCrypto from "node:crypto";
import { mock } from "node:test";

export interface RandomIntCall {
  max: number;
}

/** Every `randomInt` the generator made, in call order. */
export const calls = {
  randomInt: [] as RandomIntCall[],
};

export const behaviour = {
  /**
   * Answers for the next draws, consumed in order. A `number` is returned as
   * the index; an `Error` is thrown. When the script runs out, the draw falls
   * through to the real `crypto.randomInt`.
   */
  script: [] as Array<number | Error>,
  /** Returned by every draw once the script is empty. `null` = use the real one. */
  fixedIndex: null as number | null,
  /** Thrown by every draw once the script is empty. */
  alwaysThrow: null as Error | null,
};

export function resetHarness(): void {
  calls.randomInt.length = 0;
  behaviour.script = [];
  behaviour.fixedIndex = null;
  behaviour.alwaysThrow = null;
}

/** The generator warns between retries. Capture the lines rather than print them. */
export const warnLines: string[] = [];

const realConsole = { warn: console.warn };

export function silenceRetryWarnings(): void {
  console.warn = (...args: unknown[]): void => {
    warnLines.push(args.map((a) => String(a)).join(" "));
  };
}

export function restoreRetryWarnings(): void {
  console.warn = realConsole.warn;
}

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

const moduleMock = mock as unknown as ModuleMocker;

function scriptedRandomInt(max: number): number {
  calls.randomInt.push({ max });

  const next = behaviour.script.shift();
  if (next instanceof Error) {
    throw next;
  }
  if (typeof next === "number") {
    return next;
  }
  if (behaviour.alwaysThrow) {
    throw behaviour.alwaysThrow;
  }
  if (behaviour.fixedIndex !== null) {
    return behaviour.fixedIndex;
  }

  return realCrypto.randomInt(max);
}

const fakeCrypto = { ...realCrypto, randomInt: scriptedRandomInt };

moduleMock.module("crypto", {
  defaultExport: fakeCrypto,
  namedExports: { randomInt: scriptedRandomInt },
});

/** Loads the service AFTER the mock above is installed, so it binds to it. */
export const loadService = () => import("@/lib/services/license-key");

/**
 * The alphabet the service documents: base32 without the characters people
 * misread. Declared here so a test can assert the service agrees, instead of
 * importing the constant the service does not export.
 */
export const EXPECTED_ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
