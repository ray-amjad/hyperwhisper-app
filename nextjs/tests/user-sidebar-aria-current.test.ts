/**
 * #1314 (sibling of #1311): the user portal sidebar showed the open page by
 * colour only. The active nav `<Link>` now carries `aria-current="page"`, so
 * a screen reader can tell which entry is the current page.
 *
 * HOW. Renders the REAL `UserSidebar` inside the real `UserProvider` through
 * `renderToStaticMarkup`, with `usePathname` mocked to the page under test.
 * A static render has the phone menu closed, so the markup holds one `<nav>`:
 * the desktop sidebar's.
 *
 * WHAT IT DOES NOT PROVE: what a screen reader announces, or that the
 * attribute moves on a client-side navigation.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { moduleMock } from "./module-mock";

let currentPath = "/en/user/dashboard";

moduleMock.module("next/navigation", {
  namedExports: {
    usePathname: () => currentPath,
    useRouter: () => ({ push() {}, replace() {}, prefetch() {} }),
  },
});

// Variable specifiers, and deferred: a static import would bind before the
// mock above, and a literal `.tsx` specifier is a TS5097 error here.
const SIDEBAR_PATH = "../components/user/UserSidebar.tsx";
const CONTEXT_PATH = "../components/user/UserContext.tsx";

async function render(path: string, isAdmin: boolean): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: UserSidebar } = (await import(SIDEBAR_PATH)) as {
    default: (props: { locale: string }) => null;
  };
  const { UserProvider } = (await import(CONTEXT_PATH)) as {
    UserProvider: (props: {
      email: string;
      isAdmin: boolean;
      children: unknown;
    }) => null;
  };

  currentPath = path;
  return renderToStaticMarkup(
    createElement(UserProvider, {
      email: "someone@example.com",
      isAdmin,
      children: createElement(UserSidebar, { locale: "en" }),
    }),
  );
}

/** The nav links' text, each with its `aria-current` value (or null). */
function navLinks(
  html: string,
): Array<{ name: string; current: string | null }> {
  const navs = Array.from(html.matchAll(/<nav\b[^>]*>([\s\S]*?)<\/nav>/g));
  assert.equal(navs.length, 1, "expected one <nav> in a static render");

  return Array.from(
    navs[0][1].matchAll(/<a\b([^>]*)>[\s\S]*?<span[^>]*>([^<]*)<\/span><\/a>/g),
  ).map((m) => ({
    name: m[2],
    current: /\baria-current="([^"]+)"/.exec(m[1])?.[1] ?? null,
  }));
}

test("a member on the dashboard: its one link is the current page", async () => {
  assert.deepEqual(navLinks(await render("/en/user/dashboard", false)), [
    { name: "Dashboard", current: "page" },
  ]);
});

test("an admin on the dashboard: only the Dashboard link is the current page", async () => {
  const links = navLinks(await render("/en/user/dashboard", true));

  assert.ok(links.length > 1, `nav links: ${JSON.stringify(links)}`);
  assert.deepEqual(
    links.filter((l) => l.current !== null),
    [{ name: "Dashboard", current: "page" }],
  );
});

test("an admin on Customers: only the Customers link is the current page", async () => {
  const links = navLinks(await render("/en/user/customers", true));

  assert.ok(
    links.some((l) => l.name === "Customers"),
    `nav links: ${JSON.stringify(links)}`,
  );
  assert.deepEqual(
    links.filter((l) => l.current !== null),
    [{ name: "Customers", current: "page" }],
  );
});

test("on a page with no nav entry, no link claims to be current", async () => {
  const links = navLinks(await render("/en/user/not-a-nav-page", true));

  assert.ok(links.length > 1, `nav links: ${JSON.stringify(links)}`);
  assert.deepEqual(
    links.filter((l) => l.current !== null),
    [],
  );
});
