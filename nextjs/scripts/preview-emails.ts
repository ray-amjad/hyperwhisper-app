/**
 * Render every email HyperWhisper sends into one HTML page.
 *
 * There is no other way to see the emails side by side: four go out from the
 * Stripe webhook and one from a sign-in request, so checking that the branding
 * and the company footer match used to mean buying something five times. This
 * script renders each template with sample data and writes one file.
 *
 *   npm run preview-emails                 # -> .email-preview/index.html
 *   npm run preview-emails -- /tmp/out.html
 *
 * Sample data only. The script sends nothing and reads no database.
 */

import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";

import {
  creditMintEmailHtml,
  creditMintEmailText,
} from "../lib/templates/credit-mint-email";
import {
  creditTopUpEmailHtml,
  creditTopUpEmailText,
} from "../lib/templates/credit-topup-email";
import { licenseEmailHtml, licenseEmailText } from "../lib/templates/license-email";
import {
  magicLinkEmailHtml,
  magicLinkEmailText,
} from "../lib/templates/magic-link-email";
import { welcomeEmailHtml, welcomeEmailText } from "../lib/templates/welcome-email";
import { escapeHtml } from "../lib/templates/escape-html";

const MAGIC_LINK_URL =
  "https://hyperwhisper.com/api/auth/magic-link/verify?token=sample";

const sample = {
  customerName: "Ada Lovelace",
  customerEmail: "ada@example.com",
  productName: "HyperWhisper",
  // The address `lib/services/stripe-webhook.ts` really passes. It is
  // deliberately not the Resend `from` (support@) or the footer (hello@).
  supportEmail: "hi@support.hyperwhisper.com",
  licenseKey: "HW-7QK2-4M9X-PD31",
};

interface Preview {
  /** How the email is named in the code (`emailType` in the sent-emails log). */
  id: string;
  /** Subject line, exactly as the sender builds it. */
  subject: string;
  /** Where the send happens. */
  sentFrom: string;
  html: string;
  text: string | null;
}

const previews: Preview[] = [
  {
    id: "magic-link",
    subject: "Sign in to HyperWhisper",
    sentFrom: "src/lib/auth.ts — Better Auth magic-link plugin",
    html: magicLinkEmailHtml({ url: MAGIC_LINK_URL }),
    text: magicLinkEmailText({ url: MAGIC_LINK_URL }),
  },
  {
    id: "welcome",
    subject: `Welcome to ${sample.productName}`,
    sentFrom: "lib/services/email.ts — sendWelcomeEmail",
    html: welcomeEmailHtml({
      ...sample,
      downloadUrl: "https://hyperwhisper.com/download",
      loomVideoUrl: "https://www.loom.com/share/sample",
      loomThumbnailUrl: "https://hyperwhisper.com/email-assets/welcome-email-thumbnail.png",
    }),
    text: welcomeEmailText({
      ...sample,
      downloadUrl: "https://hyperwhisper.com/download",
      loomVideoUrl: "https://www.loom.com/share/sample",
      loomThumbnailUrl: "https://hyperwhisper.com/email-assets/welcome-email-thumbnail.png",
    }),
  },
  {
    id: "license",
    subject: `Your ${sample.productName} License Key`,
    sentFrom: "lib/services/email.ts — sendLicenseKey",
    html: licenseEmailHtml({ ...sample, downloadUrl: "https://hyperwhisper.com/download" }),
    text: licenseEmailText({ ...sample, downloadUrl: "https://hyperwhisper.com/download" }),
  },
  {
    id: "credit-mint",
    subject: `Your ${sample.productName} key and credits`,
    sentFrom: "lib/services/email.ts — sendCreditMint",
    html: creditMintEmailHtml({ ...sample, creditAmount: 25000 }),
    text: creditMintEmailText({ ...sample, creditAmount: 25000 }),
  },
  {
    id: "credit-topup",
    subject: "10,000 credits added",
    sentFrom: "lib/services/email.ts — sendCreditTopUp",
    html: creditTopUpEmailHtml({ ...sample, creditAmount: 10000, newBalance: 35000 }),
    text: creditTopUpEmailText({ ...sample, creditAmount: 10000, newBalance: 35000 }),
  },
];

/** `srcdoc` holds a whole HTML document, so only the quote needs escaping. */
const srcdoc = (html: string) => html.replace(/&/g, "&amp;").replace(/"/g, "&quot;");

const textPane = (text: string | null) =>
  text === null
    ? `<p class="none">This email has no plain-text part.</p>`
    : `<pre>${escapeHtml(text.trim())}</pre>`;

const card = (p: Preview, index: number) => `
  <section class="email" id="${p.id}">
    <header>
      <span class="num">${index + 1}</span>
      <div>
        <h2>${escapeHtml(p.subject)}</h2>
        <p class="meta"><code>${escapeHtml(p.id)}</code> &middot; ${escapeHtml(p.sentFrom)}</p>
      </div>
    </header>
    <iframe title="${escapeHtml(p.subject)}" onload="fit(this)" srcdoc="${srcdoc(p.html)}"></iframe>
    <details>
      <summary>Plain-text part</summary>
      ${textPane(p.text)}
    </details>
  </section>`;

const page = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>HyperWhisper emails — all ${previews.length}</title>
  <style>
    :root { color-scheme: light; }
    * { box-sizing: border-box; }
    body { margin: 0; padding: 32px 24px 64px; background: #f3f4f6;
           font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; color: #111827; }
    h1 { margin: 0 0 4px; font-size: 22px; }
    .lede { margin: 0 0 32px; color: #6b7280; font-size: 14px; max-width: 70ch; line-height: 1.6; }
    .grid { display: grid; gap: 24px; grid-template-columns: repeat(auto-fit, minmax(420px, 1fr));
            align-items: start; max-width: 2200px; }
    .email { background: #fff; border: 1px solid #e5e7eb; border-radius: 12px; overflow: hidden; }
    .email header { display: flex; gap: 12px; align-items: flex-start; padding: 16px 20px;
                    border-bottom: 1px solid #e5e7eb; background: #fafafa; }
    .num { flex: 0 0 auto; width: 24px; height: 24px; border-radius: 999px; background: #2563eb; color: #fff;
           font-size: 13px; font-weight: 700; display: grid; place-items: center; }
    .email h2 { margin: 0 0 2px; font-size: 15px; font-weight: 600; }
    .meta { margin: 0; font-size: 12px; color: #6b7280; }
    .meta code { background: #eef2ff; color: #3730a3; padding: 1px 5px; border-radius: 4px; }
    iframe { display: block; width: 100%; height: 900px; border: 0; background: #fff; }
    details { border-top: 1px solid #e5e7eb; }
    summary { padding: 10px 20px; font-size: 13px; color: #374151; cursor: pointer; user-select: none; }
    pre { margin: 0; padding: 0 20px 20px; font-size: 12px; line-height: 1.6; color: #374151;
          white-space: pre-wrap; word-break: break-word; }
    .none { margin: 0; padding: 0 20px 20px; font-size: 13px; color: #b45309; }
  </style>
</head>
<body>
  <h1>HyperWhisper emails &mdash; all ${previews.length}</h1>
  <p class="lede">Every email the app sends, rendered from the real templates with sample data.
     Each one closes with the Ray Amjad LTD company block.
     Regenerate with <code>npm run preview-emails</code>.</p>
  <div class="grid">${previews.map(card).join("\n")}</div>
  <script>
    // Grow each frame to its email so no footer is cut off. A srcdoc frame is
    // same-origin, so its document is readable; if a browser disagrees the
    // frame keeps its CSS height and the page still works.
    function fit(frame) {
      try {
        var doc = frame.contentDocument;
        var body = doc.body;
        frame.style.height = Math.max(
          body.scrollHeight, doc.documentElement.scrollHeight, 320
        ) + 16 + 'px';
      } catch (e) { /* keep the fallback height */ }
    }
    addEventListener('load', function () {
      document.querySelectorAll('iframe').forEach(fit);
    });
  </script>
</body>
</html>
`;

const out = resolve(process.argv[2] ?? ".email-preview/index.html");
mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, page);
console.log(`Wrote ${previews.length} emails to ${out}`);
