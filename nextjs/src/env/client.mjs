// @ts-check
/**
 * Client env. This module is in the browser bundle on every route (through
 * `src/lib/posthog-client.ts`), so it imports only `schema.client.mjs`: no
 * server schema and no zod (#917).
 */
import { clientEnv, validateClientEnv } from "./schema.client.mjs";

const _clientEnv = validateClientEnv(clientEnv);

if (!_clientEnv.success) {
  console.error(
    "❌ Invalid environment variables:\n",
    ...Object.entries(_clientEnv.errors).map(
      ([name, message]) => `${name}: ${message}\n`,
    ),
  );
  throw new Error("Invalid environment variables");
}

for (let key of Object.keys(_clientEnv.data)) {
  if (!key.startsWith("NEXT_PUBLIC_")) {
    console.warn(
      `❌ Invalid public environment variable name: ${key}. It must begin with 'NEXT_PUBLIC_'`,
    );

    throw new Error("Invalid public environment variable name");
  }
}

export const env = _clientEnv.data;
