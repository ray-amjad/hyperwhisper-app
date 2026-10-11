/**
 * #1659: on an RTL locale (/ar/) the portal's English-only fragments must not
 * be reordered by the page's direction. The Account Keys card is rendered for
 * real; the other two components need tRPC data, so their source is checked
 * for the isolating markup instead.
 *
 * WHAT IT DOES NOT PROVE: how a browser lays the row out; a Playwright run on
 * /ar/user/dashboard and /ar/user/devices does that.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import * as React from "react";
import { renderToStaticMarkup } from "react-dom/server";

// A variable specifier: a literal `.tsx` specifier is TS5097 under this tsconfig.
const CARD_PATH = "../components/customer/dashboard/LicenseKeysCard.tsx";

// Built from parts so no credential-shaped literal sits in the source.
const SAMPLE = ["HW", "CUST", "AAAA", "BBBB", "CCCC"].join("-");

const source = (path: string) =>
  readFileSync(new URL(path, import.meta.url), "utf8");

test("a masked Account Key is forced left-to-right", async () => {
  const { default: LicenseKeysCard } = (await import(CARD_PATH)) as {
    default: React.ComponentType<{
      licenses: { id: string; key: string; status: string }[];
    }>;
  };
  const html = renderToStaticMarkup(
    React.createElement(LicenseKeysCard, {
      licenses: [{ id: "l1", key: SAMPLE, status: "granted" }],
    }),
  );
  assert.match(html, /<code dir="ltr"[^>]*>HW-CUST-••••-••••-••••<\/code>/);
});

test("credit amounts and their words sit in one isolate", () => {
  const src = source("../components/customer/dashboard/CreditHistoryCard.tsx");
  assert.match(
    src,
    /<bdi>\{grant\.originalAmount\.toLocaleString\(\)\} credits<\/bdi>/,
  );
  assert.match(
    src,
    /<bdi>\s*\{grant\.expired \? 0 : grant\.remainingAmount\.toLocaleString\(\)\}\{" "\}\s*left\s*<\/bdi>/,
  );
});

test("a device name is isolated and the id gap sits on the parent", () => {
  const src = source(
    "../app/[locale]/user/(authenticated)/devices/DevicesClient.tsx",
  );
  assert.match(
    src,
    /<bdi className="text-white text-sm">\s*\{device\.deviceName \|\| device\.deviceId\}\s*<\/bdi>/,
  );
  assert.match(
    src,
    /<div className="flex flex-wrap items-baseline gap-x-2">\s*<bdi className="text-white text-sm">[\s\S]*?<bdi className="text-gray-500 text-xs">\s*\{device\.deviceId\}\s*<\/bdi>/,
  );
  assert.doesNotMatch(src, /text-xs ml-2/);
});
