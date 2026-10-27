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

export interface KeyRotatedEmailData {
  customerName: string;
  customerEmail: string;
  /**
   * The NEW key. Only its first 7 characters ("HW-XXXX") reach the email, the
   * same hint the top-up receipt shows: the full key lives on the dashboard,
   * and a mailbox is exactly the place a leaked key would have come from.
   */
  newLicenseKey: string;
  /** When the rotation happened. Rendered in UTC. */
  rotatedAt: Date;
  supportEmail: string;
}

const keyHint = (key: string) => `${key.substring(0, 7)}…`;

const rotatedAtText = (at: Date) =>
  `${at.toISOString().replace("T", " ").slice(0, 16)} UTC`;

/**
 * Notice that an Account Key was rotated from the dashboard. Sent to the
 * account's own address after the rotation commits, so the owner learns of a
 * rotation they did not make. Never carries the full key, old or new.
 */
export const keyRotatedEmailHtml = (data: KeyRotatedEmailData) => {
  const customerName = escapeHtml(data.customerName);
  const hint = escapeHtml(keyHint(data.newLicenseKey));
  const at = rotatedAtText(data.rotatedAt);

  return emailDocument({
    title: "Your Account Key was replaced",
    content: `${card(`        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">Your Account Key was replaced</h1>
        <p style="margin-bottom: 8px; color: #4b5563;">Hi ${customerName},</p>
        <p style="margin-bottom: 16px; color: #4b5563;">Your HyperWhisper Account Key was rotated from your dashboard on ${at}. The old key no longer works. Your credits, plan and purchase history stay with your account.</p>

${infoPanel(`            <p style="margin: 0; color: #1e40af;">New Account Key: <span style="font-family: 'Courier New', monospace;">${hint}</span></p>
            <p style="margin: 8px 0 0 0; color: #1e40af; font-size: 14px;">Copy the full key from your dashboard and enter it in HyperWhisper on each of your devices.</p>`)}

        <p style="margin: 24px 0 0 0;">${primaryButton(DASHBOARD_URL, "Open your dashboard")}</p>

${noticePanel(`            <p style="margin: 0; color: #92400e; font-size: 14px;">Didn't do this? Sign in with a magic link, rotate the key again, and contact us. Every other web session on your account was signed out.</p>`)}`)}

${supportParagraph(data.supportEmail)}`,
    footerNote: `This email was sent to ${escapeHtml(data.customerEmail)} because the Account Key on your HyperWhisper account changed.`,
  });
};

export const keyRotatedEmailText = (data: KeyRotatedEmailData) => `
Your Account Key was replaced

Hi ${data.customerName},

Your HyperWhisper Account Key was rotated from your dashboard on ${rotatedAtText(data.rotatedAt)}. The old key no longer works. Your credits, plan and purchase history stay with your account.

New Account Key: ${keyHint(data.newLicenseKey)}
Copy the full key from your dashboard at ${DASHBOARD_URL} and enter it in HyperWhisper on each of your devices.

Didn't do this? Sign in with a magic link, rotate the key again, and contact us. Every other web session on your account was signed out.

${supportText(data.supportEmail)}

This email was sent to ${data.customerEmail} because the Account Key on your HyperWhisper account changed.

${companyFooterText()}
`;
