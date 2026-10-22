/**
 * `/[locale]/credits` with a repeated query parameter (#2061).
 *
 * Next passes `?license_key=HW-A&license_key=HW-B` to the page as an array.
 * The page read it as a string and called `startsWith` on it, which threw and
 * answered 500. These tests call the real page component with the shapes Next
 * hands it and read the redirect Next's `redirect()` throws, so they exercise
 * the same code path as the request.
 */
import assert from "node:assert/strict";
import test from "node:test";

type SearchParams = Record<string, string | string[] | undefined>;

async function renderCredits(searchParams: SearchParams) {
  const { default: CreditsPage } = await import(
    "../app/[locale]/credits/page"
  );
  try {
    await CreditsPage({
      params: Promise.resolve({ locale: "en" }),
      searchParams: Promise.resolve(searchParams),
    });
    return { redirectedTo: null };
  } catch (error) {
    // `redirect()` throws an error whose digest is
    // `NEXT_REDIRECT;<type>;<url>;<status>;`. Anything else is the bug.
    const digest = (error as { digest?: unknown }).digest;
    if (typeof digest === "string" && digest.startsWith("NEXT_REDIRECT;")) {
      return { redirectedTo: digest.split(";")[2] };
    }
    throw error;
  }
}

const signInFor = (key: string) =>
  `/en/user/sign-in?licenseKey=${encodeURIComponent(key)}&returnTo=/en/user/dashboard`;

test("a repeated license_key redirects to sign-in with the first value, not a 500", async () => {
  const result = await renderCredits({ license_key: ["HW-A", "HW-B"] });
  assert.equal(result.redirectedTo, signInFor("HW-A"));
});

test("a repeated id redirects to sign-in with the first value, not a 500", async () => {
  const result = await renderCredits({ id: ["HW-A", "HW-B"] });
  assert.equal(result.redirectedTo, signInFor("HW-A"));
});

test("a repeated parameter whose first value is not a key shows the purchase page", async () => {
  const result = await renderCredits({ id: ["x", "y"] });
  assert.equal(result.redirectedTo, null);
});

test("single values behave as before", async () => {
  assert.equal(
    (await renderCredits({ license_key: "HW-A" })).redirectedTo,
    signInFor("HW-A"),
  );
  // `id` (the legacy macOS link) still wins over `license_key`.
  assert.equal(
    (await renderCredits({ id: "HW-ID", license_key: "HW-LK" })).redirectedTo,
    signInFor("HW-ID"),
  );
  assert.equal((await renderCredits({})).redirectedTo, null);
});
