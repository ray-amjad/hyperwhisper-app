import {
  accountKeyBlock,
  card,
  downloadButton,
  emailDocument,
  infoPanel,
  noticePanel,
  purchaseFooterNote,
  supportParagraph,
  supportText,
} from "./email-layout";
import { escapeHtml } from "./escape-html";

export interface LicenseEmailData {
  customerName: string;
  customerEmail: string;
  licenseKey: string;
  productName: string;
  downloadUrl?: string;
  supportEmail: string;
}

/** The license email links the portal to the receipt as well as the key. */
const PORTAL_NOTE = " to access your Account Key, receipt, and invoice";

export const licenseEmailHtml = (data: LicenseEmailData) => {
  const { licenseKey, productName, downloadUrl, supportEmail } = data;
  // Escape attacker-controllable values (e.g. Stripe customer_details.name)
  // before embedding them in the email HTML to prevent markup injection.
  const customerName = escapeHtml(data.customerName);

  return emailDocument({
    title: `Your ${productName} Account Key`,
    content: `${card(`        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">Welcome to ${productName}!</h1>
        <p style="margin-bottom: 8px; color: #4b5563;">Hi ${customerName},</p>
        <p style="margin-bottom: 16px; color: #4b5563;">Thank you for purchasing ${productName}! Your Account Key is ready and waiting for you below.</p>

${infoPanel(`            <p style="margin: 0; color: #1e40af; font-weight: 600;">Your Account Key</p>
${accountKeyBlock(licenseKey)}
            <p style="margin: 12px 0 0 0; color: #1e40af; font-size: 14px;">Keep this key safe - you'll need it to activate ${productName}</p>`)}

        <div style="background-color: #f3f4f6; border-radius: 8px; padding: 20px; margin: 24px 0;">
            <p style="margin: 0 0 12px 0; color: #374151; font-weight: 600; font-size: 18px;">Quick Start Guide</p>
            <ol style="margin: 0; padding-left: 20px; color: #4b5563;">
                <li style="margin: 8px 0;">Open ${productName} on your Mac</li>
                <li style="margin: 8px 0;">Click on Settings → License</li>
                <li style="margin: 8px 0;">Click "Enter License Key"</li>
                <li style="margin: 8px 0;">Paste your Account Key: <strong>${licenseKey}</strong></li>
                <li style="margin: 8px 0;">Click "Activate" to unlock all Pro features</li>
            </ol>
        </div>

        ${downloadUrl ? downloadButton(downloadUrl, productName) : ""}

${noticePanel(
  `            <p style="margin: 0; color: #92400e;"><strong>Pro Tip:</strong> You can enter your key in the app under <strong>Settings → License</strong> (the HyperWhisper Cloud panel).</p>`,
  "#fef3c7",
)}`)}

${supportParagraph(supportEmail, PORTAL_NOTE)}`,
    footerNote: purchaseFooterNote(data.customerEmail, productName),
  });
};

export const licenseEmailText = (data: LicenseEmailData) => {
  const { customerName, licenseKey, productName, downloadUrl, supportEmail } =
    data;

  return `
Welcome to ${productName}!

Hi ${customerName},

Thank you for purchasing ${productName}! Your Account Key is ready:

ACCOUNT KEY: ${licenseKey}

Quick Start Guide:
1. Open ${productName} on your Mac
2. Click on Settings → License
3. Click "Enter License Key"
4. Paste your Account Key: ${licenseKey}
5. Click "Activate" to unlock all Pro features

Pro Tip: You can enter your key in the app under Settings → License (the HyperWhisper Cloud panel).

${downloadUrl ? `Download ${productName}: ${downloadUrl}` : ""}

${supportText(supportEmail, " to manage your subscription")}

${purchaseFooterNote(data.customerEmail, productName)}
`;
};
