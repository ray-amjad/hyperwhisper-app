import {
  card,
  DASHBOARD_URL,
  emailDocument,
  infoPanel,
  noticePanel,
  purchaseFooterNote,
  supportParagraph,
  supportText,
} from "./email-layout";
import { escapeHtml } from "./escape-html";

export interface CreditTopUpEmailData {
  customerName: string;
  customerEmail: string;
  licenseKey: string;
  /** Credits added by this purchase. */
  creditAmount: number;
  /** Total spendable balance after this purchase. */
  newBalance: number;
  productName: string;
  supportEmail: string;
}

/**
 * Receipt email for a top-up: credits were added to an existing license key.
 * Confirms the amount added and the new balance.
 */
export const creditTopUpEmailHtml = (data: CreditTopUpEmailData) => {
  const { licenseKey, productName, supportEmail } = data;
  const customerName = escapeHtml(data.customerName);
  const added = data.creditAmount.toLocaleString();
  const balance = data.newBalance.toLocaleString();
  // Show only a short prefix of the key in the receipt; the full key already
  // lives on the customer's dashboard and original key email.
  const keyHint = `${licenseKey.substring(0, 7)}…`;

  return emailDocument({
    title: `${added} credits added`,
    content: `${card(`        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">Credits added</h1>
        <p style="margin-bottom: 8px; color: #4b5563;">Hi ${customerName},</p>
        <p style="margin-bottom: 16px; color: #4b5563;">Thanks for your top-up! We've added credits to your Account Key.</p>

${infoPanel(`            <p style="margin: 0; color: #1e40af;">Credits added: <strong>${added}</strong></p>
            <p style="margin: 8px 0 0 0; color: #1e40af;">New balance: <strong>${balance} credits</strong></p>
            <p style="margin: 8px 0 0 0; color: #1e40af; font-size: 14px;">Account Key: <span style="font-family: 'Courier New', monospace;">${keyHint}</span></p>`)}

${noticePanel(`            <p style="margin: 0; color: #92400e; font-size: 14px;">These credits are valid for 12 months from this purchase. See your full balance and history on your <a href="${DASHBOARD_URL}" style="color: #92400e;">dashboard</a>.</p>`)}`)}

${supportParagraph(supportEmail)}`,
    footerNote: purchaseFooterNote(data.customerEmail, productName),
  });
};

export const creditTopUpEmailText = (data: CreditTopUpEmailData) => {
  const { customerName, licenseKey, productName, supportEmail } = data;
  const added = data.creditAmount.toLocaleString();
  const balance = data.newBalance.toLocaleString();
  const keyHint = `${licenseKey.substring(0, 7)}…`;

  return `
Credits added

Hi ${customerName},

Thanks for your top-up! We've added credits to your Account Key.

Credits added: ${added}
New balance: ${balance} credits
Account Key: ${keyHint}

These credits are valid for 12 months from this purchase. See your full balance and history on your dashboard at ${DASHBOARD_URL}.

${supportText(supportEmail)}

${purchaseFooterNote(data.customerEmail, productName)}
`;
};
