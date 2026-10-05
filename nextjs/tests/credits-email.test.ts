import assert from "node:assert/strict";
import test from "node:test";

import { showEmailError } from "../src/lib/credits-email";

// #967: `me@gmail` left the checkout button dead with no reason given.
test("a rejected address shows the error once the field is left", () => {
  assert.equal(showEmailError("me@gmail", true, false), true);
});

test("a rejected address shows nothing while the field is untouched", () => {
  assert.equal(showEmailError("me@gmail", false, false), false);
});

test("an empty or blank field never shows the error, even after blur", () => {
  assert.equal(showEmailError("", true, false), false);
  assert.equal(showEmailError("   ", true, false), false);
});

test("an accepted address shows nothing", () => {
  assert.equal(showEmailError("me@gmail.com", true, true), false);
});
