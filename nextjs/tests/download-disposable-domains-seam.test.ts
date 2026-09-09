import assert from "node:assert/strict";
import test from "node:test";

const MODULE_PATH = "../server/api/routers/download-disposable-domains.ts";
const load = () => import(MODULE_PATH);

test("the disposable-domain cache reloads at the exact 1-hour boundary", async () => {
  const { createDisposableDomainCache } = await load();
  let clock = 0;
  const getDomains = createDisposableDomainCache(
    [" Example.COM ", "", "example.com"],
    { now: () => clock },
  );

  const first = await getDomains();
  assert.deepEqual([...first], ["example.com"]);

  clock = 60 * 60 * 1000 - 1;
  assert.equal(await getDomains(), first);

  clock = 60 * 60 * 1000;
  assert.notEqual(await getDomains(), first);
});
