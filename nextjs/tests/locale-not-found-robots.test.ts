/**
 * `app/[locale]/not-found.tsx` (#1133). A notFound() under [locale] resolves the
 * [locale] layout's metadata, whose robots is "index, follow", next to Next's
 * injected noindex. This boundary's robots must replace it with a noindex, and
 * the boundary must render the same UI as the root not-found page.
 */
import assert from "node:assert/strict";
import test from "node:test";

import LocaleNotFound, { metadata } from "../app/[locale]/not-found";
import RootNotFound from "../app/not-found";

test("[locale] not-found overrides the layout robots with noindex", () => {
  assert.deepEqual(metadata.robots, { index: false });
});

test("[locale] not-found renders the root not-found component", () => {
  assert.equal(LocaleNotFound, RootNotFound);
});
