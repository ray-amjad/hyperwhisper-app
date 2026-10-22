/**
 * `/[locale]/checkout` with a repeated query parameter (#2181).
 *
 * Next passes `?license_key=HW-A&license_key=HW-B` to the page as an array.
 * The page handed the array to `URLSearchParams.set`, which comma-joined it
 * into one key that does not exist (`HW-A,HW-B`). These tests call the real
 * page component with the shapes Next hands it and read the redirect Next's
 * `redirect()` throws, so they exercise the same code path as the request.
 */
import assert from "node:assert/strict";
import test from "node:test";

type SearchParams = Record<string, string | string[] | undefined>;

async function renderCheckout(searchParams: SearchParams) {
  const { default: CheckoutPage } = await import(
    "../app/[locale]/checkout/page"
  );
  try {
    await CheckoutPage({
      params: Promise.resolve({ locale: "en" }),
      searchParams: Promise.resolve(searchParams),
    });
  } catch (error) {
    // `redirect()` throws an error whose digest is
    // `NEXT_REDIRECT;<type>;<url>;<status>;`. Anything else is the bug.
    const digest = (error as { digest?: unknown }).digest;
    if (typeof digest === "string" && digest.startsWith("NEXT_REDIRECT;")) {
      return digest.split(";")[2];
    }
    throw error;
  }
  assert.fail("the checkout page always redirects");
}

test("a repeated license_key, id and code forward only their first values", async () => {
  assert.equal(
    await renderCheckout({
      license_key: ["HW-A", "HW-B"],
      code: ["PROMO-1", "PROMO-2"],
    }),
    "/en/credits?license_key=HW-A&code=PROMO-1",
  );
  assert.equal(
    await renderCheckout({
      id: ["HW-ID-1", "HW-ID-2"],
      license_key: ["HW-LK-1", "HW-LK-2"],
      code: ["PROMO-1", "PROMO-2"],
    }),
    "/en/credits?license_key=HW-ID-1&code=PROMO-1",
  );
});

test("single values behave as before", async () => {
  assert.equal(
    await renderCheckout({ license_key: "HW-A", code: "PROMO" }),
    "/en/credits?license_key=HW-A&code=PROMO",
  );
  // `id` (the legacy macOS link) still wins over `license_key`.
  assert.equal(
    await renderCheckout({ id: "HW-ID", license_key: "HW-LK" }),
    "/en/credits?license_key=HW-ID",
  );
  assert.equal(await renderCheckout({}), "/en/credits");
});
