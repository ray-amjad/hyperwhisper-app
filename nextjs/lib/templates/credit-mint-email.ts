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

  return `
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Your ${productName} key and credits</title>
</head>
<body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: 0 auto; padding: 20px; background-color: #ffffff;">
    <div style="background-color: #f9fafb; border-radius: 8px; padding: 32px; margin-bottom: 24px;">
        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">Your ${productName} key is ready</h1>
        <p style="margin-bottom: 8px; color: #4b5563;">Hi ${customerName},</p>
        <p style="margin-bottom: 16px; color: #4b5563;">Thanks for your purchase! Your Account Key unlocks your credit wallet — your credits are loaded and ready to use.</p>

        <div style="background-color: #eff6ff; border-radius: 8px; padding: 20px; margin: 24px 0; border-left: 4px solid #2563eb;">
            <p style="margin: 0; color: #1e40af; font-weight: 600;">Your Account Key</p>
            <div style="margin-top: 12px; background-color: #dbeafe; padding: 12px; border-radius: 4px;">
                <p style="margin: 0; font-family: 'Courier New', monospace; font-size: 20px; font-weight: 700; color: #1e40af; letter-spacing: 2px; word-break: break-all; text-align: center;">${licenseKey}</p>
            </div>
            <p style="margin: 12px 0 0 0; color: #1e40af; font-size: 14px;">Starting balance: <strong>${credits} credits</strong></p>
        </div>

        <div style="background-color: #f3f4f6; border-radius: 8px; padding: 20px; margin: 24px 0;">
            <p style="margin: 0 0 12px 0; color: #374151; font-weight: 600; font-size: 18px;">Quick Start</p>
            <ol style="margin: 0; padding-left: 20px; color: #4b5563;">
                <li style="margin: 8px 0;">Open ${productName}</li>
                <li style="margin: 8px 0;">Go to Settings → License</li>
                <li style="margin: 8px 0;">Paste your Account Key: <strong>${licenseKey}</strong></li>
                <li style="margin: 8px 0;">Start using HyperWhisper Cloud</li>
            </ol>
        </div>

        <div style="background-color: #fffbeb; border-radius: 8px; padding: 16px; margin-top: 24px; border-left: 4px solid #f59e0b;">
            <p style="margin: 0; color: #92400e; font-size: 14px;">Credits are valid for 12 months from purchase. You can top up anytime from your <a href="https://hyperwhisper.com/user/dashboard" style="color: #92400e;">dashboard</a>.</p>
        </div>
    </div>

    <p style="color: #6b7280; font-size: 14px; margin-bottom: 24px;">
        <strong>Need help?</strong> Email us at <a href="mailto:${supportEmail}" style="color: #2563eb;">${supportEmail}</a> or visit our <a href="https://hyperwhisper.com/user" style="color: #2563eb;">customer portal</a>.
    </p>

    <hr style="border: none; border-top: 1px solid #e5e7eb; margin: 24px 0;">
    <p style="color: #9ca3af; font-size: 12px; margin: 0;">This email was sent to ${data.customerEmail} because a purchase was made for ${productName}.</p>
    <p style="font-size: 11px; color: #9ca3af; text-align: center; margin: 24px 0 0 0;">
        Ray Amjad LTD<br />
        Lytchett House, 13 Freeland Park, Wareham Road, Poole, Dorset, BH16 6FA<br />
        <a href="mailto:hello@hyperwhisper.com" style="color: #9ca3af;">hello@hyperwhisper.com</a>
    </p>
</body>
</html>
`;
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

Credits are valid for 12 months from purchase. You can top up anytime from your dashboard at https://hyperwhisper.com/user/dashboard.

Need help?
Email us at ${supportEmail} or visit our customer portal at https://hyperwhisper.com/user.

This email was sent to ${data.customerEmail} because a purchase was made for ${productName}.
`;
};
