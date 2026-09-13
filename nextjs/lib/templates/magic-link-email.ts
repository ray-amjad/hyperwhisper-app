import {
  card,
  companyFooterText,
  emailDocument,
  primaryButton,
} from "./email-layout";

/**
 * How long a magic link stays valid, in seconds.
 *
 * This is the ONE number. `src/lib/auth.ts` passes it to the `magicLink`
 * plugin and the copy below is derived from it, so the promise in the email
 * cannot drift from what the server enforces.
 *
 * It used to drift: the plugin was configured with no `expiresIn`, which means
 * its 300-second default (`node_modules/better-auth/dist/plugins/magic-link/
 * index.mjs`: `opts.expiresIn || 300`), while the email said "10 minutes". A
 * user who waited 6 minutes got a dead link and a correct-looking email.
 * Raising the limit is a one-line change here; the copy follows.
 */
export const MAGIC_LINK_EXPIRY_SECONDS = 300;

const LINK_LIFETIME = `${MAGIC_LINK_EXPIRY_SECONDS / 60} minutes`;

const HEADLINE = "Sign in to HyperWhisper";

const IGNORE_NOTE =
  "This email was sent because someone asked to sign in to HyperWhisper with this address. If that was not you, you can safely ignore it — nobody can sign in without the link above.";

/**
 * The sign-in email sent by the Better Auth magic-link plugin.
 *
 * It lived inline in `src/lib/auth.ts` and had a design of its own: a nested
 * table on a dark background with a gradient pill button. It now uses the same
 * [emailDocument] shell as the purchase emails, so all five emails share one
 * card, one heading colour, one button and one company block.
 */
export const magicLinkEmailHtml = ({ url }: { url: string }): string =>
  emailDocument({
    title: HEADLINE,
    content:
      card(`        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">${HEADLINE}</h1>
        <p style="margin-bottom: 24px; color: #4b5563;">Click the button below to sign in to your account. The link works once and expires in ${LINK_LIFETIME}.</p>

        ${primaryButton(url, HEADLINE)}`),
    footerNote: IGNORE_NOTE,
  });

/**
 * Plain-text counterpart. The other four emails all send one; without it this
 * email is HTML-only, which reads worse to a spam filter and shows nothing at
 * all in a text-only client.
 */
export const magicLinkEmailText = ({ url }: { url: string }): string => `
${HEADLINE}

Open this link to sign in to your account. It works once and expires in ${LINK_LIFETIME}.

${url}

${IGNORE_NOTE}

${companyFooterText()}
`;
