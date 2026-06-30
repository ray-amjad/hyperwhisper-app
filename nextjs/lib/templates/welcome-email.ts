import { escapeHtml } from "./escape-html";

/**
 * Data contract for welcome emails that are sent after a prospective user
 * submits their address on the download form. The Loom fields are kept
 * generic so we can reuse the template with different onboarding videos
 * without touching the markup.
 */
export interface WelcomeEmailData {
  customerName: string;
  customerEmail: string;
  productName: string;
  downloadUrl: string;
  loomVideoUrl: string;
  loomThumbnailUrl: string;
  supportEmail?: string;
}

/**
 * Render the rich HTML version of the welcome email. The layout intentionally
 * mirrors the existing license email to maintain brand consistency while the
 * Loom thumbnail functions as a large play button that deep links to the
 * walkthrough recording.
 */
export const welcomeEmailHtml = (data: WelcomeEmailData) => {
  const {
    productName,
    downloadUrl,
    loomVideoUrl,
    loomThumbnailUrl,
    supportEmail,
  } = data;
  // Escape user-derived values before embedding them in the email HTML to
  // prevent markup injection from an attacker-controllable customer name.
  const customerName = escapeHtml(data.customerName);

  return `
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Welcome to ${productName}</title>
</head>
<body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: 0 auto; padding: 20px; background-color: #ffffff;">
    <div style="background-color: #f9fafb; border-radius: 8px; padding: 32px; margin-bottom: 24px;">
        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">Great to have you, ${customerName}!</h1>
        <p style="margin-bottom: 16px; color: #4b5563;">Thanks for requesting a download link, here is everything you need to get started with <strong>${productName}</strong>.</p>

        <div style="background-color: #eff6ff; border-radius: 8px; padding: 20px; margin: 24px 0; border-left: 4px solid #2563eb;">
            <p style="margin: 0 0 4px 0; color: #1e40af; font-weight: 600;">Get set up in minutes</p>
            <ul style="margin: 12px 0 0 0; padding-left: 20px; color: #1e40af;">
                <li style="margin: 8px 0;">Download the application using the button below.</li>
                <li style="margin: 8px 0;">Watch a short walkthrough that shows how to set it up and use it.</li>
                <li style="margin: 8px 0;">Reply to this email if you have any questions. Real humans read every message.</li>
            </ul>
        </div>

        <a href="${downloadUrl}" style="display: inline-block; background-color: #2563eb; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 600;">Download ${productName}</a>
    </div>

    <a href="${loomVideoUrl}" style="display: block; margin-bottom: 24px; border-radius: 8px; overflow: hidden;" target="_blank" rel="noopener noreferrer">
        <img src="${loomThumbnailUrl}" alt="Watch the HyperWhisper quickstart video" style="display: block; width: 100%; height: auto; border-radius: 8px;" />
    </a>

    <p style="color: #6b7280; font-size: 14px; margin-bottom: 24px;">Need a hand? ${supportEmail ? `Email us at <a href="mailto:${supportEmail}" style="color: #2563eb;">${supportEmail}</a>` : "Just hit reply"} and we will reply as soon as possible.</p>

    <hr style="border: none; border-top: 1px solid #e5e7eb; margin: 24px 0;">
    <p style="color: #9ca3af; font-size: 12px; margin: 0;">This email was sent to ${data.customerEmail} because you requested a ${productName} download link. There is no need to unsubscribe. If you didn't request this, simply ignore it.</p>
    <p style="font-size: 11px; color: #9ca3af; text-align: center; margin: 24px 0 0 0;">
        Ray Amjad LTD<br />
        Lytchett House, 13 Freeland Park, Wareham Road, Poole, Dorset, BH16 6FA<br />
        <a href="mailto:hello@hyperwhisper.com" style="color: #9ca3af;">hello@hyperwhisper.com</a>
    </p>
</body>
</html>
`;
};

/**
 * Plain-text fallback that keeps the same call-to-action in a transport-safe
 * format for clients that ignore or strip HTML content.
 */
export const welcomeEmailText = (data: WelcomeEmailData) => {
  const { customerName, productName, downloadUrl, loomVideoUrl, supportEmail } =
    data;

  return `
Welcome aboard, ${customerName}!

We just saved your email, so here is everything you need to get started with ${productName}.

Get set up in minutes:
- Download the application using the bottom below.
- Watch our quick Loom walkthrough of the transcription workflow.
- Reply if you have questions and we will help you out.

Download ${productName}: ${downloadUrl}

Watch the quickstart video: ${loomVideoUrl}

Need a hand?
${supportEmail ? `Email us at ${supportEmail}` : "Reply to this message"}

This email was sent to ${data.customerEmail} because you requested a ${productName} download link. There is no need to unsubscribe. If you didn't request this, simply delete the email.
`;
};
