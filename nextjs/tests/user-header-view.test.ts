/**
 * The header's MARKUP and its click wiring: `components/user/UserHeaderView.tsx`
 * (#870, #881 review round 2).
 *
 * Round 1 put every one of these assertions against `UserHeader` itself, and
 * round 2 proved by mutation that all of them were vacuous. One root cause:
 * `renderToStaticMarkup` is the only rendering surface in this repo, it renders
 * ONCE, and the only state that render can reach is the initial one — not busy,
 * no error. So every assertion had to be a NEGATIVE one against markup that was
 * absent anyway, and each passed just as happily when the markup it described
 * was deleted. React does not serialise event handlers either, so `onClick`
 * was invisible on top of that.
 *
 * `UserHeaderView` exists to make all of it reachable. It holds NO hooks, so:
 *
 * - it can be CALLED as a plain function, and the element tree it returns still
 *   carries the button's real `onClick`, which this file invokes;
 * - `signingOut` and `signOutError` are PROPS, so the busy and failed renders
 *   are reachable and every markup assertion below is POSITIVE.
 *
 * The wrapper that owns those two flags is `tests/user-header-sign-out.test.ts`.
 * The decision the handler makes is `tests/sign-out-seam.test.ts`.
 */
import assert from "node:assert/strict";
import test, { mock } from "node:test";
import {
  Children,
  createElement,
  isValidElement,
  type ReactElement,
  type ReactNode,
} from "react";
import { renderToStaticMarkup } from "react-dom/server";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the one method used here rather than bumping
 * the types in a test-only change — the same shape
 * `tests/root-layout-direction.test.ts` uses.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

/**
 * `next-intl`'s `createNavigation` builds a `Link` that reads the request
 * locale from React context, which a bare `renderToStaticMarkup` does not
 * provide. The stub is a plain anchor: the logo link is not what this file is
 * about, but it must render for the header to render at all.
 */
moduleMock.module("../src/i18n/navigation", {
  namedExports: {
    Link: ({ children, ...props }: { children?: ReactNode; href: string }) =>
      createElement("a", props, children),
  },
});

// A VARIABLE specifier, and deferred: a static import would bind before the
// `mock.module` call above, and a literal `.tsx` specifier is a TS5097 error
// under this tsconfig. Same rule the other mock.module tests here document.
const VIEW_PATH = "../components/user/UserHeaderView.tsx";

interface ViewProps {
  user: { email?: string | null };
  isAdmin: boolean;
  signingOut: boolean;
  signOutError: string | null;
  onSignOut: () => void;
}

type UserHeaderView = (props: ViewProps) => ReactElement;

/** The real view, with the defaults of a signed-in page's first paint. */
async function loadView(): Promise<UserHeaderView> {
  const { default: UserHeaderView } = (await import(VIEW_PATH)) as {
    default: UserHeaderView;
  };

  return UserHeaderView;
}

function propsFor(overrides: Partial<ViewProps> = {}): ViewProps {
  return {
    user: { email: "someone@example.com" },
    isAdmin: false,
    signingOut: false,
    signOutError: null,
    onSignOut: () => {},
    ...overrides,
  };
}

/** Renders the real view to the bytes a browser would receive. */
async function renderView(overrides: Partial<ViewProps> = {}): Promise<string> {
  const UserHeaderView = await loadView();

  return renderToStaticMarkup(createElement(UserHeaderView, propsFor(overrides)));
}

/**
 * The first `<button>` in a returned element tree, or `null`.
 *
 * This walks the tree the component RETURNS rather than its markup, because the
 * handler is exactly what markup cannot show. `Children.toArray` drops the
 * `false` branches of the conditional regions and flattens the arrays; the
 * recursion covers the nesting.
 */
function findButton(
  node: ReactNode,
): ReactElement<{ onClick?: () => void; disabled?: boolean }> | null {
  for (const child of Children.toArray(node)) {
    if (!isValidElement(child)) continue;

    if (child.type === "button") {
      return child as ReactElement<{ onClick?: () => void; disabled?: boolean }>;
    }

    const nested = findButton((child.props as { children?: ReactNode }).children);

    if (nested) return nested;
  }

  return null;
}

test("the Sign Out button calls back when it is clicked", async () => {
  const UserHeaderView = await loadView();
  const clicks: number[] = [];

  // Called as a plain function, not rendered: this view has no hooks, so the
  // tree it returns is the real one and it still carries the real handler.
  const button = findButton(
    UserHeaderView(propsFor({ onSignOut: () => clicks.push(1) })),
  );

  assert.ok(button, "the view rendered no <button>");

  // The whole of the round 1 gap. Deleting `onClick` left the suite at 5 pass /
  // 0 fail, because React never serialises a handler into static markup.
  assert.equal(typeof button.props.onClick, "function", "the button has no onClick");

  button.props.onClick?.();

  assert.deepEqual(clicks, [1]);
});

test("the button is the only thing wired to sign out", async () => {
  const UserHeaderView = await loadView();
  const clicks: number[] = [];

  const tree = UserHeaderView(propsFor({ onSignOut: () => clicks.push(1) }));

  // Nothing is invoked by rendering alone — a handler called in the render body
  // would sign the user out on every paint.
  renderToStaticMarkup(tree);

  assert.deepEqual(clicks, []);
});

test("the idle header renders an enabled Sign Out button", async () => {
  const markup = await renderView();

  assert.match(markup, /<button[^>]*>Sign Out<\/button>/);
  assert.doesNotMatch(markup, /Signing Out/);
  // React serialises a true `disabled` as `disabled=""` and omits a false one.
  // Anchored on the `=` because the button's Tailwind classes contain the bare
  // word `disabled:` twice.
  assert.doesNotMatch(markup, /disabled="/);
  assert.match(markup, /someone@example\.com/);
});

test("a sign-out in flight disarms the button and says so", async () => {
  const markup = await renderView({ signingOut: true });

  // POSITIVE, and reachable only because `signingOut` is a prop. The round 1
  // assertions were the negatives of these against a render that could never
  // be busy, so deleting `disabled={signingOut}` or the busy label killed
  // nothing.
  assert.match(markup, /<button[^>]*\sdisabled=""/);
  assert.match(markup, /<button[^>]*>Signing Out\.\.\.<\/button>/);
  assert.doesNotMatch(markup, />Sign Out</);
});

test("a refused sign-out is announced in a live region", async () => {
  const markup = await renderView({ signOutError: "Internal Server Error" });

  assert.match(markup, /role="alert"/);
  assert.match(markup, /<span[^>]*role="alert"[^>]*>Internal Server Error<\/span>/);
  // The failure re-arms the button — the user is still on this page.
  assert.match(markup, /<button[^>]*>Sign Out<\/button>/);
  assert.doesNotMatch(markup, /<button[^>]*\sdisabled=""/);
});

test("the header announces nothing before a sign-out has failed", async () => {
  // A live region that is present and empty on every page load is an assistive
  // technology annoyance and would also mean the error span renders with no
  // error. Safe as a negative now only because the test above proves the region
  // exists when there IS an error.
  const markup = await renderView({ signOutError: null });

  assert.doesNotMatch(markup, /role="alert"/);
});

test("the admin header hides the logo and shows the badge", async () => {
  const markup = await renderView({ isAdmin: true });

  assert.match(markup, />Admin</);
  assert.doesNotMatch(markup, />HyperWhisper</);
  assert.match(markup, /<button[^>]*>Sign Out<\/button>/);
});
