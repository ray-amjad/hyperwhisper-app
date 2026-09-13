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
import {
  MAGIC_LINK_EXPIRY_SECONDS,
  magicLinkEmailHtml,
  magicLinkEmailText,
} from "../lib/templates/magic-link-email";
import {
  welcomeEmailHtml,
  welcomeEmailText,
} from "../lib/templates/welcome-email";

// All five emails share one document shell (lib/templates/email-layout.ts).
// These tests lock the parts that must stay on every email, so a change to the
// shared shell cannot quietly drop them from one template.

const magicLinkUrl = "https://example.com/verify?token=abc";

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
  magicLinkEmailHtml({ url: magicLinkUrl }),
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
  magicLinkEmailText({ url: magicLinkUrl }),
];

test("every email renders the shared document shell", () => {
  for (const html of allEmails()) {
    assert.match(html, /^\n<!DOCTYPE html>\n<html lang="en">\n/);
    assert.match(html, /<meta name="viewport"/);
    assert.ok(html.trimEnd().endsWith("</html>"));
  }
});

test("every email carries the company address footer", () => {
  for (const html of allEmails()) {
    assert.ok(html.includes(COMPANY_NAME));
    assert.ok(html.includes(COMPANY_ADDRESS));
    assert.ok(html.includes(`mailto:${COMPANY_EMAIL}`));
  }
});

test("the company footer is the last thing in every email body", () => {
  for (const html of allEmails()) {
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

// The sign-in email is excluded: it is addressed to whoever asked to sign in,
// and it has no customer record behind it, so it names no recipient.
test("every email sent off a purchase says who it was sent to", () => {
  const addressed = allEmails().filter(
    (html) => !html.includes("Sign in to HyperWhisper"),
  );

  assert.equal(addressed.length, 4);
  for (const html of addressed) {
    assert.match(html, /This email was sent to ada@example\.com/);
  }
});

test("the sign-in email states the expiry the server enforces", () => {
  const minutes = MAGIC_LINK_EXPIRY_SECONDS / 60;

  for (const body of [
    magicLinkEmailHtml({ url: magicLinkUrl }),
    magicLinkEmailText({ url: magicLinkUrl }),
  ]) {
    assert.ok(body.includes(`expires in ${minutes} minutes`));
    assert.ok(body.includes(magicLinkUrl));
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
