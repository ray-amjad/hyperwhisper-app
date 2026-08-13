import {
  accountKeyBlock,
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

export interface CreditMintEmailData {
  customerName: string;
  customerEmail: string;
  licenseKey: string;
  /** Credits granted by this purchase (also the starting balance). */
  creditAmount: number;
  productName: string;
  supportEmail: string;
}

/**
 * Email for the buy-credits-mints-a-key flow: a guest bought credits with no
 * existing key, so we minted one. Delivers the new key AND the starting credit
 * balance (the two are the same on a fresh key).
 */
export const creditMintEmailHtml = (data: CreditMintEmailData) => {
  const { licenseKey, productName, supportEmail } = data;
  // Escape attacker-controllable values (e.g. Stripe customer_details.name)
  // before embedding them in the email HTML to prevent markup injection.
  const customerName = escapeHtml(data.customerName);
  const credits = data.creditAmount.toLocaleString();

  return emailDocument({
    title: `Your ${productName} key and credits`,
    content: `${card(`        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">Your ${productName} key is ready</h1>
        <p style="margin-bottom: 8px; color: #4b5563;">Hi ${customerName},</p>
        <p style="margin-bottom: 16px; color: #4b5563;">Thanks for your purchase! Your Account Key unlocks your credit wallet — your credits are loaded and ready to use.</p>

${infoPanel(`            <p style="margin: 0; color: #1e40af; font-weight: 600;">Your Account Key</p>
${accountKeyBlock(licenseKey)}
            <p style="margin: 12px 0 0 0; color: #1e40af; font-size: 14px;">Starting balance: <strong>${credits} credits</strong></p>`)}

        <div style="background-color: #f3f4f6; border-radius: 8px; padding: 20px; margin: 24px 0;">
            <p style="margin: 0 0 12px 0; color: #374151; font-weight: 600; font-size: 18px;">Quick Start</p>
            <ol style="margin: 0; padding-left: 20px; color: #4b5563;">
                <li style="margin: 8px 0;">Open ${productName}</li>
                <li style="margin: 8px 0;">Go to Settings → License</li>
                <li style="margin: 8px 0;">Paste your Account Key: <strong>${licenseKey}</strong></li>
                <li style="margin: 8px 0;">Start using HyperWhisper Cloud</li>
            </ol>
        </div>

${noticePanel(`            <p style="margin: 0; color: #92400e; font-size: 14px;">Credits are valid for 12 months from purchase. You can top up anytime from your <a href="${DASHBOARD_URL}" style="color: #92400e;">dashboard</a>.</p>`)}`)}

${supportParagraph(supportEmail)}`,
    footerNote: purchaseFooterNote(data.customerEmail, productName),
  });
};

export const creditMintEmailText = (data: CreditMintEmailData) => {
  const { customerName, licenseKey, productName, supportEmail } = data;
  const credits = data.creditAmount.toLocaleString();

  return `
Your ${productName} key is ready

Hi ${customerName},

Thanks for your purchase! Your Account Key unlocks your credit wallet — your credits are loaded and ready to use.

ACCOUNT KEY: ${licenseKey}
Starting balance: ${credits} credits

Quick Start:
1. Open ${productName}
2. Go to Settings → License
3. Paste your Account Key: ${licenseKey}
4. Start using HyperWhisper Cloud

Credits are valid for 12 months from purchase. You can top up anytime from your dashboard at ${DASHBOARD_URL}.

${supportText(supportEmail)}

${purchaseFooterNote(data.customerEmail, productName)}
`;
};
