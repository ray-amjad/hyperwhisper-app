import assert from "node:assert/strict";
import test from "node:test";

const originalFetch = globalThis.fetch;
const originalProxycheckKey = process.env.PROXYCHECK_API_KEY;

test.afterEach(() => {
  globalThis.fetch = originalFetch;

  if (originalProxycheckKey === undefined) {
    delete process.env.PROXYCHECK_API_KEY;
  } else {
    process.env.PROXYCHECK_API_KEY = originalProxycheckKey;
  }
});

test("rejects malformed IPs before calling proxycheck", async () => {
  process.env.PROXYCHECK_API_KEY = "test-key";

  const { getCountryFromIP } = await import(
    `../lib/services/geolocation.ts?invalid=${Date.now()}`
  );

  let fetchCalled = false;
  globalThis.fetch = async () => {
    fetchCalled = true;
    throw new Error("fetch should not be called");
  };

  assert.equal(await getCountryFromIP("1.1.1.1/../../foo?x="), null);
  assert.equal(fetchCalled, false);
});

test("encodes valid IP path segment when calling proxycheck", async () => {
  process.env.PROXYCHECK_API_KEY = "test-key";

  const { getCountryFromIP } = await import(
    `../lib/services/geolocation.ts?valid=${Date.now()}`
  );

  let requestedUrl = "";
  globalThis.fetch = async (input) => {
    requestedUrl = String(input);
    return new Response(
      JSON.stringify({
        "2001:db8::1": { location: { country_name: "Exampleland" } },
      }),
      { status: 200 }
    );
  };

  assert.equal(await getCountryFromIP("2001:db8::1"), "Exampleland");
  assert.equal(
    requestedUrl,
    "https://proxycheck.io/v3/2001%3Adb8%3A%3A1?key=test-key"
  );
});
