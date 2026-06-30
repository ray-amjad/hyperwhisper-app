import { betterAuth } from "better-auth";
import { drizzleAdapter } from "better-auth/adapters/drizzle";
import { magicLink } from "better-auth/plugins";
import { nextCookies } from "better-auth/next-js";
import { db } from "@/src/db";
import { resend, DEFAULT_FROM_EMAIL } from "@/lib/clients/resend";
import { licenseKeyPlugin } from "./auth-license-key-plugin";

export const auth = betterAuth({
  baseURL: process.env.BETTER_AUTH_URL,
  database: drizzleAdapter(db, { provider: "pg" }),
  user: {
    additionalFields: {
      role: {
        type: "string",
        required: false,
        defaultValue: "user",
        input: false,
      },
    },
  },
  plugins: [
    magicLink({
      sendMagicLink: async ({ email, url }) => {
        await resend.emails.send({
          from: DEFAULT_FROM_EMAIL,
          to: email,
          subject: "Sign in to HyperWhisper",
          html: magicLinkEmailHtml({ url }),
        });
      },
    }),
    licenseKeyPlugin(),
    nextCookies(),
  ],
});

function magicLinkEmailHtml({ url }: { url: string }): string {
  return `
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
</head>
<body style="margin:0;padding:0;background-color:#111827;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
  <table width="100%" cellpadding="0" cellspacing="0" style="background-color:#111827;padding:40px 20px;">
    <tr>
      <td align="center">
        <table width="480" cellpadding="0" cellspacing="0" style="background-color:#ffffff;border-radius:16px;box-shadow:0 20px 45px rgba(15,23,42,0.18);overflow:hidden;">
          <tr>
            <td style="padding:40px 40px 24px;text-align:center;">
              <h1 style="margin:0 0 8px;font-size:26px;font-weight:700;color:#2563eb;">HyperWhisper</h1>
              <p style="margin:0;font-size:14px;color:#6b7280;">AI-Powered Speech to Text</p>
            </td>
          </tr>
          <tr>
            <td style="padding:0 40px 24px;text-align:center;">
              <p style="margin:0 0 24px;font-size:16px;color:#1f2937;line-height:1.6;">
                Click the button below to sign in to your account.
              </p>
              <a href="${url}" style="display:inline-block;padding:14px 28px;background:linear-gradient(135deg,#6366f1,#2563eb);color:#ffffff;font-size:16px;font-weight:600;text-decoration:none;border-radius:9999px;box-shadow:0 10px 20px rgba(99,102,241,0.35);">
                Sign in to HyperWhisper
              </a>
            </td>
          </tr>
          <tr>
            <td style="padding:0 40px 40px;text-align:center;">
              <p style="margin:0;padding-top:24px;border-top:1px solid #e5e7eb;font-size:12px;color:#6b7280;line-height:1.5;">
                If you didn't request this email, you can safely ignore it.<br>
                This link will expire in 10 minutes.
              </p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>`;
}
