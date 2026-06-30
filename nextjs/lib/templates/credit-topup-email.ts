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

  return `
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>${added} credits added</title>
</head>
<body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: 0 auto; padding: 20px; background-color: #ffffff;">
    <div style="background-color: #f9fafb; border-radius: 8px; padding: 32px; margin-bottom: 24px;">
        <h1 style="color: #2563eb; margin-top: 0; margin-bottom: 16px; font-size: 24px;">Credits added</h1>
        <p style="margin-bottom: 8px; color: #4b5563;">Hi ${customerName},</p>
        <p style="margin-bottom: 16px; color: #4b5563;">Thanks for your top-up! We've added credits to your license key.</p>

        <div style="background-color: #eff6ff; border-radius: 8px; padding: 20px; margin: 24px 0; border-left: 4px solid #2563eb;">
            <p style="margin: 0; color: #1e40af;">Credits added: <strong>${added}</strong></p>
            <p style="margin: 8px 0 0 0; color: #1e40af;">New balance: <strong>${balance} credits</strong></p>
            <p style="margin: 8px 0 0 0; color: #1e40af; font-size: 14px;">License key: <span style="font-family: 'Courier New', monospace;">${keyHint}</span></p>
        </div>

        <div style="background-color: #fffbeb; border-radius: 8px; padding: 16px; margin-top: 24px; border-left: 4px solid #f59e0b;">
            <p style="margin: 0; color: #92400e; font-size: 14px;">These credits are valid for 12 months from this purchase. See your full balance and history on your <a href="https://hyperwhisper.com/user/dashboard" style="color: #92400e;">dashboard</a>.</p>
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

export const creditTopUpEmailText = (data: CreditTopUpEmailData) => {
  const { customerName, licenseKey, productName, supportEmail } = data;
  const added = data.creditAmount.toLocaleString();
  const balance = data.newBalance.toLocaleString();
  const keyHint = `${licenseKey.substring(0, 7)}…`;

  return `
Credits added

Hi ${customerName},

Thanks for your top-up! We've added credits to your license key.

Credits added: ${added}
New balance: ${balance} credits
License key: ${keyHint}

These credits are valid for 12 months from this purchase. See your full balance and history on your dashboard at https://hyperwhisper.com/user/dashboard.

Need help?
Email us at ${supportEmail} or visit our customer portal at https://hyperwhisper.com/user.

This email was sent to ${data.customerEmail} because a purchase was made for ${productName}.
`;
};
