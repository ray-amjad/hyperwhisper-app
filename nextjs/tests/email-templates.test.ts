import assert from "node:assert/strict";
import test from "node:test";

import { creditMintEmailHtml } from "../lib/templates/credit-mint-email";
import { creditTopUpEmailHtml } from "../lib/templates/credit-topup-email";
import { licenseEmailHtml } from "../lib/templates/license-email";
import { welcomeEmailHtml } from "../lib/templates/welcome-email";

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

test("every email renders the shared document shell", () => {
  for (const html of allEmails()) {
    assert.match(html, /^\n<!DOCTYPE html>\n<html lang="en">\n/);
    assert.match(html, /<meta name="viewport"/);
    assert.ok(html.trimEnd().endsWith("</html>"));
  }
});

test("every email carries the company address footer", () => {
  for (const html of allEmails()) {
    assert.match(html, /Ray Amjad LTD/);
    assert.match(html, /mailto:hello@hyperwhisper\.com/);
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
