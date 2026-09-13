import assert from "node:assert/strict";
import test from "node:test";

import {
  creditMintEmailHtml,
  creditMintEmailText,
} from "../lib/templates/credit-mint-email";
import {
  creditTopUpEmailHtml,
  creditTopUpEmailText,
} from "../lib/templates/credit-topup-email";
import {
  COMPANY_ADDRESS,
  COMPANY_EMAIL,
  COMPANY_NAME,
} from "../lib/templates/email-layout";
import {
  licenseEmailHtml,
  licenseEmailText,
} from "../lib/templates/license-email";
import { magicLinkEmailHtml } from "../lib/templates/magic-link-email";
import {
  welcomeEmailHtml,
  welcomeEmailText,
} from "../lib/templates/welcome-email";

// The four transactional emails share one document shell (lib/templates/
// email-layout.ts). These tests lock the parts that must stay on every email,
// so a change to the shared shell cannot quietly drop them from one template.

const base = {
  customerName: "Ada Lovelace",
  customerEmail: "ada@example.com",
  productName: "HyperWhisper",
  supportEmail: "support@example.com",
  licenseKey: "HW-TEST-0000-1111",
};

const allEmails = () => [
  licenseEmailHtml({ ...base, downloadUrl: "https://example.com/download" }),
  creditMintEmailHtml({ ...base, creditAmount: 25000 }),
  creditTopUpEmailHtml({ ...base, creditAmount: 10000, newBalance: 35000 }),
  welcomeEmailHtml({
    ...base,
    downloadUrl: "https://example.com/download",
    loomVideoUrl: "https://example.com/video",
    loomThumbnailUrl: "https://example.com/thumb.png",
  }),
];

// The magic-link email does NOT use the shared document shell — it has its own
// dark table layout — so it is listed separately here. It must still carry the
// same company block, which is the one thing all five emails share.
const everyEmailIncludingMagicLink = () => [
  ...allEmails(),
  magicLinkEmailHtml({ url: "https://example.com/verify?token=abc" }),
];

const allPlainText = () => [
  licenseEmailText({ ...base, downloadUrl: "https://example.com/download" }),
  creditMintEmailText({ ...base, creditAmount: 25000 }),
  creditTopUpEmailText({ ...base, creditAmount: 10000, newBalance: 35000 }),
  welcomeEmailText({
    ...base,
    downloadUrl: "https://example.com/download",
    loomVideoUrl: "https://example.com/video",
    loomThumbnailUrl: "https://example.com/thumb.png",
  }),
];

test("every email renders the shared document shell", () => {
  for (const html of allEmails()) {
    assert.match(html, /^\n<!DOCTYPE html>\n<html lang="en">\n/);
    assert.match(html, /<meta name="viewport"/);
    assert.ok(html.trimEnd().endsWith("</html>"));
  }
});

test("every email carries the company address footer", () => {
  for (const html of everyEmailIncludingMagicLink()) {
    assert.ok(html.includes(COMPANY_NAME));
    assert.ok(html.includes(COMPANY_ADDRESS));
    assert.ok(html.includes(`mailto:${COMPANY_EMAIL}`));
  }
});

test("the company footer is the last thing in every email body", () => {
  for (const html of everyEmailIncludingMagicLink()) {
    const afterFooter = html.slice(html.lastIndexOf(COMPANY_ADDRESS));

    assert.ok(!/<(p|h1|a|img|table)\b/i.test(afterFooter.replace(/<a [^>]*mailto:[^>]*>[^<]*<\/a>/i, "")));
  }
});

test("every plain-text part carries the company address footer", () => {
  for (const text of allPlainText()) {
    assert.ok(text.includes(COMPANY_NAME));
    assert.ok(text.includes(COMPANY_ADDRESS));
    assert.ok(text.includes(COMPANY_EMAIL));
    assert.ok(text.trimEnd().endsWith(COMPANY_EMAIL));
  }
});

test("every email says who it was sent to", () => {
  for (const html of allEmails()) {
    assert.match(html, /This email was sent to ada@example\.com/);
  }
});

test("the customer name is escaped in every email", () => {
  const name = '<script>alert("x")</script>';
  const htmls = [
    licenseEmailHtml({ ...base, customerName: name }),
    creditMintEmailHtml({ ...base, customerName: name, creditAmount: 25000 }),
    creditTopUpEmailHtml({
      ...base,
      customerName: name,
      creditAmount: 10000,
      newBalance: 35000,
    }),
    welcomeEmailHtml({
      ...base,
      customerName: name,
      downloadUrl: "https://example.com/download",
      loomVideoUrl: "https://example.com/video",
      loomThumbnailUrl: "https://example.com/thumb.png",
    }),
  ];

  for (const html of htmls) {
    assert.ok(!html.includes("<script>"));
    assert.match(html, /&lt;script&gt;/);
  }
});

test("the key emails show the full Account Key, the receipt shows a prefix", () => {
  assert.match(
    licenseEmailHtml({ ...base }),
    /letter-spacing: 2px[^>]*>HW-TEST-0000-1111</,
  );
  assert.match(
    creditMintEmailHtml({ ...base, creditAmount: 25000 }),
    /letter-spacing: 2px[^>]*>HW-TEST-0000-1111</,
  );

  const topUp = creditTopUpEmailHtml({
    ...base,
    creditAmount: 10000,
    newBalance: 35000,
  });
  assert.ok(!topUp.includes("HW-TEST-0000-1111"));
  assert.match(topUp, /HW-TEST…/);
});
