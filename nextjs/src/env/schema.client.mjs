// @ts-check
/**
 * The client env: 4 optional `NEXT_PUBLIC_` variables, which Next.js inlines
 * at build time. This module reaches the browser bundle on every route, so it
 * must import nothing from `schema.server.mjs` and must not import zod (#917).
 * The check below is hand-written and rejects exactly what the zod schema it
 * replaces rejected:
 *
 *   NEXT_PUBLIC_ENVIRONMENT:  z.enum(["development", "test", "production"]).optional()
 *   NEXT_PUBLIC_SITE_URL:     z.string().url().optional()
 *   NEXT_PUBLIC_POSTHOG_KEY:  z.string().optional()
 *   NEXT_PUBLIC_POSTHOG_HOST: z.string().url().optional()
 *
 * `tests/env-client-schema.test.ts` holds it to that zod schema, value by value.
 */

/**
 * @typedef {{
 *   NEXT_PUBLIC_ENVIRONMENT?: "development" | "test" | "production";
 *   NEXT_PUBLIC_SITE_URL?: string;
 *   NEXT_PUBLIC_POSTHOG_KEY?: string;
 *   NEXT_PUBLIC_POSTHOG_HOST?: string;
 * }} ClientEnv
 */

/**
 * @typedef {{ success: true, data: ClientEnv }
 *   | { success: false, errors: Record<string, string> }} ClientEnvResult
 */

const ENVIRONMENTS = ["development", "test", "production"];

const asciiTabOrNewline = /[\t\n\r]/g;

/**
 * zod 4's `z.string().url()`: the trimmed value must parse as a URL, and the
 * value it hands back is trimmed, with every ASCII tab, LF and CR deleted.
 * @param {string} value
 * @returns {string | null} the normalised value, or null when it is not a URL
 */
function parseUrl(value) {
  const trimmed = value.trim();
  let ok;
  try {
    if (typeof URL.canParse === "function") {
      ok = URL.canParse(trimmed);
    } else {
      new URL(trimmed);
      ok = true;
    }
  } catch {
    ok = false;
  }
  return ok ? trimmed.replace(asciiTabOrNewline, "") : null;
}

/**
 * Validate the 4 client variables. Keys other than these 4 are dropped, as a
 * zod object drops them.
 * @param {Record<string, unknown>} input
 * @returns {ClientEnvResult}
 */
export function validateClientEnv(input) {
  /** @type {Record<string, string>} */
  const errors = {};
  /** @type {Record<string, string | undefined>} */
  const data = {};

  /**
   * @param {string} key
   * @param {(value: string) => string | null} check returns the value to
   *   keep, or null to reject it
   * @param {string} message
   */
  const field = (key, check, message) => {
    const value = input[key];
    if (value === undefined) {
      if (key in input) data[key] = undefined;
      return;
    }
    if (typeof value !== "string") {
      errors[key] = "Invalid input: expected string";
      return;
    }
    const kept = check(value);
    if (kept === null) errors[key] = message;
    else data[key] = kept;
  };

  field(
    "NEXT_PUBLIC_ENVIRONMENT",
    (v) => (ENVIRONMENTS.includes(v) ? v : null),
    'Invalid option: expected one of "development"|"test"|"production"',
  );
  field("NEXT_PUBLIC_SITE_URL", parseUrl, "Invalid URL");
  field("NEXT_PUBLIC_POSTHOG_KEY", (v) => v, "Invalid input");
  field("NEXT_PUBLIC_POSTHOG_HOST", parseUrl, "Invalid URL");

  if (Object.keys(errors).length > 0) return { success: false, errors };
  return { success: true, data: /** @type {ClientEnv} */ (data) };
}

/**
 * You can't destruct `process.env` as a regular object, so you have to do
 * it manually here. This is because Next.js evaluates this at build time,
 * and only used environment variables are included in the build.
 * @type {{ [k in keyof ClientEnv]: string | undefined }}
 */
export const clientEnv = {
  NEXT_PUBLIC_ENVIRONMENT: process.env.NEXT_PUBLIC_ENVIRONMENT,
  NEXT_PUBLIC_SITE_URL: process.env.NEXT_PUBLIC_SITE_URL,
  NEXT_PUBLIC_POSTHOG_KEY: process.env.NEXT_PUBLIC_POSTHOG_KEY,
  NEXT_PUBLIC_POSTHOG_HOST: process.env.NEXT_PUBLIC_POSTHOG_HOST,
};
