import { cloudflareBinding } from "@/lib/clients/cloudflare-binding";

// Default from email address
export const DEFAULT_FROM_EMAIL = "HyperWhisper <support@hyperwhisper.com>";

/** The Worker's Cloudflare Email Service binding (`send_email` in wrangler.jsonc). */
export const EMAIL_BINDING = "EMAIL";

/** One message, as every sender hands it to the provider. */
export interface EmailPayload {
  from: string;
  to: string;
  subject: string;
  html: string;
  text: string;
}

/**
 * What one send came to. Never throws: a refused or failed send is an `error`
 * carrying Cloudflare's code (`E_SENDER_NOT_VERIFIED`, `E_RATE_LIMIT_EXCEEDED`,
 * ...) and message, so every caller handles both outcomes the same way.
 */
export type EmailSendOutcome =
  | { data: { id: string }; error: null }
  | { data: null; error: { code: string; message: string } };

/** The slice of the `send_email` binding this site uses. */
interface SendEmailBinding {
  send(message: {
    from: string | { email: string; name?: string };
    to: string;
    subject: string;
    html: string;
    text: string;
  }): Promise<{ messageId: string }>;
}

/** `"Name <addr>"` to the binding's `{ name, email }`; a bare address passes through. */
function senderOf(from: string): string | { email: string; name: string } {
  const match = /^\s*(.*?)\s*<([^<>]+)>\s*$/.exec(from);

  return match ? { name: match[1], email: match[2] } : from;
}

/**
 * Send one email through Cloudflare Email Service. Without the binding (node
 * tests, scripts) the send fails with `E_BINDING_MISSING` rather than
 * pretending to succeed.
 */
export async function sendEmail(payload: EmailPayload): Promise<EmailSendOutcome> {
  const binding = cloudflareBinding<SendEmailBinding>(EMAIL_BINDING);

  if (!binding) {
    return {
      data: null,
      error: {
        code: "E_BINDING_MISSING",
        message: `No ${EMAIL_BINDING} binding: not running on the Worker`,
      },
    };
  }

  try {
    const { messageId } = await binding.send({
      ...payload,
      from: senderOf(payload.from),
    });

    return { data: { id: messageId }, error: null };
  } catch (error) {
    // The binding throws an Error with a `code`. Anything without one (a
    // network failure inside the runtime) is reported as `unknown`.
    const code =
      typeof error === "object" &&
      error !== null &&
      "code" in error &&
      typeof error.code === "string"
        ? error.code
        : "unknown";
    const message = error instanceof Error ? error.message : String(error);

    return { data: null, error: { code, message } };
  }
}
