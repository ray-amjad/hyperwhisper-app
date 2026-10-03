/**
 * The sidebar pill rule (#915): `activeNavHref` in
 * `components/user/UserSidebar.tsx`. A clicked link reads active while its
 * navigation is pending, and only one item ever carries the pill.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { activeNavHref } from "../components/user/UserSidebar";

const dashboard = "/en/user/dashboard";
const devices = "/en/user/devices";
const customers = "/en/user/customers";

test("with no click pending, the current path is active", () => {
  assert.equal(activeNavHref(dashboard, null), dashboard);
});

test("a clicked link is active while the old path is still showing", () => {
  assert.equal(
    activeNavHref(dashboard, { href: devices, from: dashboard }),
    devices,
  );
});

test("once the path commits, the pending click no longer counts", () => {
  assert.equal(
    activeNavHref(devices, { href: devices, from: dashboard }),
    devices,
  );
  // Any other committed path wins over the stale click as well.
  assert.equal(
    activeNavHref(customers, { href: devices, from: dashboard }),
    customers,
  );
});

test("exactly one item carries the pill during a pending click", () => {
  const active = activeNavHref(dashboard, { href: devices, from: dashboard });
  const lit = [dashboard, customers, devices].filter((h) => h === active);
  assert.deepEqual(lit, [devices]);
});
