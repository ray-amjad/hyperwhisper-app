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

export interface KeyDeletedEmailData {
  customerName: string;
  customerEmail: string;
  /**
   * The key that was deleted. Only its first 7 characters ("HW-XXXX") reach
   * the email, as in the key-rotated notice.
   */
  deletedLicenseKey: string;
  /** When the key was deleted. Rendered in UTC. */
  deletedAt: Date;
  supportEmail: string;
}

const keyHint = (key: string) => `${key.substring(0, 7)}…`;

const atText = (at: Date) =>
  `${at.toISOString().replace("T", " ").slice(0, 16)} UTC`;

/**
 * Notice that an Account Key was deleted from the dashboard. Sent to the
 * account's own address after the delete commits, so the owner learns of a
 * delete they did not make. Never carries the full key.
 */
export const keyDeletedEmailHtml = (data: KeyDeletedEmailData) => {
  const customerName = escapeHtml(data.customerName);
  const hint = escapeHtml(keyHint(data.deletedLicenseKey));
  const at = atText(data.deletedAt);

  return emailDocument({
    title: "An Account Key was deleted",
    content: `${card(`        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">An Account Key was deleted</h1>
        <p style="margin-bottom: 8px; color: #4b5563;">Hi ${customerName},</p>
        <p style="margin-bottom: 16px; color: #4b5563;">An Account Key was deleted from your HyperWhisper dashboard on ${at}. It no longer works on any device. Your credits, purchase history and other keys stay with your account.</p>

${infoPanel(`            <p style="margin: 0; color: #1e40af;">Deleted Account Key: <span style="font-family: 'Courier New', monospace;">${hint}</span></p>
            <p style="margin: 8px 0 0 0; color: #1e40af; font-size: 14px;">Devices that used it need one of your other keys, shown on your dashboard.</p>`)}

        <p style="margin: 24px 0 0 0;">${primaryButton(DASHBOARD_URL, "Open your dashboard")}</p>

${noticePanel(`            <p style="margin: 0; color: #92400e; font-size: 14px;">Didn't do this? Sign in with a magic link, rotate your remaining keys, and contact us. Every other web session on your account was signed out.</p>`)}`)}

${supportParagraph(data.supportEmail)}`,
    footerNote: `This email was sent to ${escapeHtml(data.customerEmail)} because a key on your HyperWhisper account was deleted.`,
  });
};

export const keyDeletedEmailText = (data: KeyDeletedEmailData) => `
An Account Key was deleted

Hi ${data.customerName},

An Account Key was deleted from your HyperWhisper dashboard on ${atText(data.deletedAt)}. It no longer works on any device. Your credits, purchase history and other keys stay with your account.

Deleted Account Key: ${keyHint(data.deletedLicenseKey)}
Devices that used it need one of your other keys, shown on your dashboard at ${DASHBOARD_URL}.

Didn't do this? Sign in with a magic link, rotate your remaining keys, and contact us. Every other web session on your account was signed out.

${supportText(data.supportEmail)}

This email was sent to ${data.customerEmail} because a key on your HyperWhisper account was deleted.

${companyFooterText()}
`;
