import {
  card,
  companyFooterText,
  DASHBOARD_URL,
  emailDocument,
  infoPanel,
  noticePanel,
  primaryButton,
  supportParagraph,
  supportText,
} from "./email-layout";
import { escapeHtml } from "./escape-html";

export interface KeyIssuedEmailData {
  customerName: string;
  customerEmail: string;
  /**
   * The NEW key. Only its first 7 characters ("HW-XXXX") reach the email, as
   * in the key-rotated notice: the full key lives on the dashboard.
   */
  newLicenseKey: string;
  /** When the key was issued. Rendered in UTC. */
  issuedAt: Date;
  supportEmail: string;
}

const keyHint = (key: string) => `${key.substring(0, 7)}…`;

const atText = (at: Date) =>
  `${at.toISOString().replace("T", " ").slice(0, 16)} UTC`;

/**
 * Notice that a new Account Key was issued from the dashboard. Sent to the
 * account's own address after the key is created, so the owner learns of a
 * key they did not make. Never carries the full key.
 */
export const keyIssuedEmailHtml = (data: KeyIssuedEmailData) => {
  const customerName = escapeHtml(data.customerName);
  const hint = escapeHtml(keyHint(data.newLicenseKey));
  const at = atText(data.issuedAt);

  return emailDocument({
    title: "A new Account Key was added",
    content: `${card(`        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">A new Account Key was added</h1>
        <p style="margin-bottom: 8px; color: #4b5563;">Hi ${customerName},</p>
        <p style="margin-bottom: 16px; color: #4b5563;">A new Account Key was added to your HyperWhisper account from your dashboard on ${at}. It shares your account's credits. Your other keys keep working.</p>

${infoPanel(`            <p style="margin: 0; color: #1e40af;">New Account Key: <span style="font-family: 'Courier New', monospace;">${hint}</span></p>
            <p style="margin: 8px 0 0 0; color: #1e40af; font-size: 14px;">Copy the full key from your dashboard.</p>`)}

        <p style="margin: 24px 0 0 0;">${primaryButton(DASHBOARD_URL, "Open your dashboard")}</p>

${noticePanel(`            <p style="margin: 0; color: #92400e; font-size: 14px;">Didn't do this? Sign in with a magic link, delete the new key, and contact us.</p>`)}`)}

${supportParagraph(data.supportEmail)}`,
    footerNote: `This email was sent to ${escapeHtml(data.customerEmail)} because a key was added to your HyperWhisper account.`,
  });
};

export const keyIssuedEmailText = (data: KeyIssuedEmailData) => `
A new Account Key was added

Hi ${data.customerName},

A new Account Key was added to your HyperWhisper account from your dashboard on ${atText(data.issuedAt)}. It shares your account's credits. Your other keys keep working.

New Account Key: ${keyHint(data.newLicenseKey)}
Copy the full key from your dashboard at ${DASHBOARD_URL}.

Didn't do this? Sign in with a magic link, delete the new key, and contact us.

${supportText(data.supportEmail)}

This email was sent to ${data.customerEmail} because a key was added to your HyperWhisper account.

${companyFooterText()}
`;
