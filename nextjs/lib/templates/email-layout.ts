/**
 * Shared building blocks for the transactional email templates.
 *
 * Every template (`license-email`, `welcome-email`, `credit-mint-email`,
 * `credit-topup-email`) used to carry its own copy of the same document shell:
 * the doctype/head/body markup, the grey card, the blue info panel, the amber
 * notice, the "Need help?" paragraph and the company address footer. Four
 * copies meant a brand tweak had to be applied four times, and they had already
 * drifted apart in small ways.
 *
 * The helpers below are the single copy. They emit exactly the markup the
 * templates emitted before, indentation included, so the rendered emails are
 * byte-for-byte what they were.
 */

/** Customer portal, linked from the support paragraph of every purchase email. */
export const PORTAL_URL = "https://hyperwhisper.com/user";
/** Credit balance & history page, linked from the credit emails. */
export const DASHBOARD_URL = "https://hyperwhisper.com/user/dashboard";

const BODY_STYLE =
  "font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: 0 auto; padding: 20px; background-color: #ffffff;";

export interface EmailDocumentParts {
  /** `<title>` of the document. */
  title: string;
  /** Body markup, indented 4 spaces, without the closing address footer. */
  content: string;
  /** The small grey "why you got this" line above the address block. */
  footerNote: string;
}

/**
 * Wrap body markup in the shared HTML document: head, body styling, and the
 * horizontal rule + footer note + company address block that closes every
 * email.
 */
export const emailDocument = ({
  title,
  content,
  footerNote,
}: EmailDocumentParts) => `
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>${title}</title>
</head>
<body style="${BODY_STYLE}">
${content}

    <hr style="border: none; border-top: 1px solid #e5e7eb; margin: 24px 0;">
    <p style="color: #9ca3af; font-size: 12px; margin: 0;">${footerNote}</p>
    <p style="font-size: 11px; color: #9ca3af; text-align: center; margin: 24px 0 0 0;">
        Ray Amjad LTD<br />
        Lytchett House, 13 Freeland Park, Wareham Road, Poole, Dorset, BH16 6FA<br />
        <a href="mailto:hello@hyperwhisper.com" style="color: #9ca3af;">hello@hyperwhisper.com</a>
    </p>
</body>
</html>
`;

/** The grey rounded card that holds the main message. `inner` is indented 8 spaces. */
export const card = (inner: string) =>
  `    <div style="background-color: #f9fafb; border-radius: 8px; padding: 32px; margin-bottom: 24px;">
${inner}
    </div>`;

/** The blue highlight panel inside the card. `inner` is indented 12 spaces. */
export const infoPanel = (inner: string) =>
  `        <div style="background-color: #eff6ff; border-radius: 8px; padding: 20px; margin: 24px 0; border-left: 4px solid #2563eb;">
${inner}
        </div>`;

/**
 * The amber notice panel at the foot of the card. `inner` is indented 12 spaces.
 *
 * `background` exists because the license email uses a deeper amber than the
 * credit emails. Making them the same colour would change how the emails look,
 * so the difference is kept as a parameter.
 */
export const noticePanel = (inner: string, background = "#fffbeb") =>
  `        <div style="background-color: ${background}; border-radius: 8px; padding: 16px; margin-top: 24px; border-left: 4px solid #f59e0b;">
${inner}
        </div>`;

/** The monospace box that shows the full Account Key. Indented 12 spaces. */
export const accountKeyBlock = (licenseKey: string) =>
  `            <div style="margin-top: 12px; background-color: #dbeafe; padding: 12px; border-radius: 4px;">
                <p style="margin: 0; font-family: 'Courier New', monospace; font-size: 20px; font-weight: 700; color: #1e40af; letter-spacing: 2px; word-break: break-all; text-align: center;">${licenseKey}</p>
            </div>`;

/** The blue call-to-action button. */
export const downloadButton = (downloadUrl: string, productName: string) =>
  `<a href="${downloadUrl}" style="display: inline-block; background-color: #2563eb; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 600;">Download ${productName}</a>`;

/**
 * The "Need help?" paragraph that closes the purchase emails. `portalNote` is
 * appended after the customer-portal link, before the full stop.
 */
export const supportParagraph = (supportEmail: string, portalNote = "") =>
  `    <p style="color: #6b7280; font-size: 14px; margin-bottom: 24px;">
        <strong>Need help?</strong> Email us at <a href="mailto:${supportEmail}" style="color: #2563eb;">${supportEmail}</a> or visit our <a href="${PORTAL_URL}" style="color: #2563eb;">customer portal</a>${portalNote}.
    </p>`;

/** Plain-text counterpart of [supportParagraph]. */
export const supportText = (supportEmail: string, portalNote = "") =>
  `Need help?
Email us at ${supportEmail} or visit our customer portal at ${PORTAL_URL}${portalNote}.`;

/** The footer note shared by every email sent off the back of a purchase. */
export const purchaseFooterNote = (
  customerEmail: string,
  productName: string,
) =>
  `This email was sent to ${customerEmail} because a purchase was made for ${productName}.`;
