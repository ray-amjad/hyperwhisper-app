import {
  DEFAULT_FROM_EMAIL,
  sendEmail,
  type EmailPayload,
  type EmailSendOutcome,
} from "@/lib/clients/email";
import { emailTag, redactRecipient } from "@/lib/shared/redact";
import { describeDbError } from "@/lib/shared/db-error";
import {
  licenseEmailHtml,
  licenseEmailText,
  type LicenseEmailData,
} from "@/lib/templates/license-email";
import {
  welcomeEmailHtml,
  welcomeEmailText,
  type WelcomeEmailData,
} from "@/lib/templates/welcome-email";
import {
  creditMintEmailHtml,
  creditMintEmailText,
  type CreditMintEmailData,
} from "@/lib/templates/credit-mint-email";
import {
  creditTopUpEmailHtml,
  creditTopUpEmailText,
  type CreditTopUpEmailData,
} from "@/lib/templates/credit-topup-email";
import {
  keyRotatedEmailHtml,
  keyRotatedEmailText,
  type KeyRotatedEmailData,
} from "@/lib/templates/key-rotated-email";
import {
  keyIssuedEmailHtml,
  keyIssuedEmailText,
  type KeyIssuedEmailData,
} from "@/lib/templates/key-issued-email";
import {
  keyDeletedEmailHtml,
  keyDeletedEmailText,
  type KeyDeletedEmailData,
} from "@/lib/templates/key-deleted-email";
import { logSentEmail } from "@/src/lib/db-layer";

interface EmailResult {
  success: boolean;
  /** The provider's success payload: `{ id }`, the Cloudflare message id. */
  data?: { id: string };
  error?: string;
}

/**
 * Error thrown when a send comes back with an error. Carries Cloudflare's
 * error code so we can decide whether the failure is worth retrying.
 */
class EmailSendError extends Error {
  readonly retryable: boolean;

  constructor(message: string, code: string) {
    super(message);
    this.name = "EmailSendError";
    this.retryable = isRetryableEmailError(code);
  }
}

/**
 * Decide whether a Cloudflare Email Service failure is transient (worth
 * retrying) or permanent.
 *
 * Permanent failures (invalid recipient, unverified sender, suppressed
 * recipient, daily quota, a rejected delivery) will never succeed on retry, so
 * we fail fast instead of burning the webhook request thread on backoff sleeps.
 */
function isRetryableEmailError(code: string): boolean {
  // Rate limits and service-side errors are transient.
  if (code === "E_RATE_LIMIT_EXCEEDED" || code === "E_INTERNAL_SERVER_ERROR") {
    return true;
  }

  // Every other documented code starts with `E_` and is permanent. Anything
  // else (`unknown`: the call failed without a code) is treated as transient.
  return !code.startsWith("E_");
}

export type { EmailPayload };

/**
 * The service's three edges: the provider, the audit log and the backoff
 * clock. Production uses the defaults; a test builds its own instance (#769).
 */
export interface EmailDeps {
  send: (payload: EmailPayload) => Promise<EmailSendOutcome>;
  logSentEmail: (data: Parameters<typeof logSentEmail>[0]) => Promise<void>;
  sleep: (ms: number) => Promise<void>;
}

const defaultDeps: EmailDeps = {
  send: sendEmail,
  logSentEmail,
  sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
};

export class EmailService {
  private readonly maxRetries = 3;
  // Base backoff. Kept small because retries run inline inside the Stripe
  // webhook request; worst-case total sleep across attempts is bounded
  // (250ms + 500ms = 750ms) to stay well under Stripe's webhook timeout.
  private readonly retryDelay = 250;

  constructor(private readonly deps: EmailDeps = defaultDeps) {}

  /**
   * Send license key email with retry logic
   */
  async sendLicenseKey(data: LicenseEmailData): Promise<EmailResult> {
    const subject = `Your ${data.productName} License Key`;
    return this.sendWithRetry("license", data.customerEmail, subject, () =>
      this.deps.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject,
        html: licenseEmailHtml(data),
        text: licenseEmailText(data),
      }),
    );
  }

  /**
   * Send the onboarding welcome email used after someone submits the download form.
   */
  async sendWelcomeEmail(data: WelcomeEmailData): Promise<EmailResult> {
    const subject = `Welcome to ${data.productName}`;
    return this.sendWithRetry("welcome", data.customerEmail, subject, () =>
      this.deps.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject,
        html: welcomeEmailHtml(data),
        text: welcomeEmailText(data),
      }),
    );
  }

  /**
   * Send the mint email: a guest bought credits with no key, so we created one.
   * Delivers the new key and its starting balance.
   */
  async sendCreditMint(data: CreditMintEmailData): Promise<EmailResult> {
    const subject = `Your ${data.productName} key and credits`;
    return this.sendWithRetry("credit-mint", data.customerEmail, subject, () =>
      this.deps.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject,
        html: creditMintEmailHtml(data),
        text: creditMintEmailText(data),
      }),
    );
  }

  /**
   * Send the top-up receipt: credits were added to an existing license key.
   */
  async sendCreditTopUp(data: CreditTopUpEmailData): Promise<EmailResult> {
    const subject = `${data.creditAmount.toLocaleString()} credits added`;
    return this.sendWithRetry("credit-topup", data.customerEmail, subject, () =>
      this.deps.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject,
        html: creditTopUpEmailHtml(data),
        text: creditTopUpEmailText(data),
      }),
    );
  }

  /**
   * Send the notice that an Account Key was rotated from the dashboard. It
   * carries only a short prefix of the new key, never the full key.
   */
  async sendKeyRotated(data: KeyRotatedEmailData): Promise<EmailResult> {
    const subject = "Your HyperWhisper Account Key was replaced";
    return this.sendWithRetry("key-rotated", data.customerEmail, subject, () =>
      this.deps.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject,
        html: keyRotatedEmailHtml(data),
        text: keyRotatedEmailText(data),
      }),
    );
  }

  /**
   * Send the notice that a new Account Key was issued from the dashboard. It
   * carries only a short prefix of the new key, never the full key.
   */
  async sendKeyIssued(data: KeyIssuedEmailData): Promise<EmailResult> {
    const subject = "A new HyperWhisper Account Key was added";
    return this.sendWithRetry("key-issued", data.customerEmail, subject, () =>
      this.deps.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject,
        html: keyIssuedEmailHtml(data),
        text: keyIssuedEmailText(data),
      }),
    );
  }

  /**
   * Send the notice that an Account Key was deleted from the dashboard. It
   * carries only a short prefix of the deleted key, never the full key.
   */
  async sendKeyDeleted(data: KeyDeletedEmailData): Promise<EmailResult> {
    const subject = "A HyperWhisper Account Key was deleted";
    return this.sendWithRetry("key-deleted", data.customerEmail, subject, () =>
      this.deps.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject,
        html: keyDeletedEmailHtml(data),
        text: keyDeletedEmailText(data),
      }),
    );
  }

  /**
   * Shared send-with-retry loop.
   *
   * Treats a resolved `{ error }` from `sendEmail` as a failure (it never
   * throws), so transient failures actually retry and permanent failures
   * surface as `{ success: false }` instead of a false positive. Permanent (non-retryable) errors abort immediately so the
   * caller's request thread is not blocked on backoff sleeps.
   */
  private async sendWithRetry(
    kind:
      | "license"
      | "welcome"
      | "credit-mint"
      | "credit-topup"
      | "key-rotated"
      | "key-issued"
      | "key-deleted",
    customerEmail: string,
    subject: string,
    send: () => Promise<EmailSendOutcome>,
  ): Promise<EmailResult> {
    // A `catch` binding is `unknown`: `throw` can carry any value, so the
    // failure is kept as-is and its message is read behind an `instanceof`
    // check instead of asserting it is an Error.
    let lastError: unknown;
    // Logs carry a hash tag of the address, never the address itself (#717).
    const recipientTag = emailTag(customerEmail);

    for (let attempt = 1; attempt <= this.maxRetries; attempt++) {
      try {
        console.log(
          `Sending ${kind} email to ${recipientTag} (attempt ${attempt}/${this.maxRetries})`,
        );

        const result = await send();

        if (result.error) {
          // A provider message can echo the recipient ("alice@corp.com is not
          // a valid recipient"). Redacted HERE, before it becomes the error's
          // message, so the catch-block log below and `EmailResult.error` —
          // which the webhook and download callers log — never carry it (#717).
          throw new EmailSendError(
            redactRecipient(
              result.error.message || `${result.error.code} sending ${kind} email`,
              customerEmail,
            ),
            result.error.code,
          );
        }

        console.log(`${capitalize(kind)} email sent successfully to ${recipientTag}`);

        await this.safeLogSentEmail({
          recipient: customerEmail,
          emailType: kind,
          subject,
          providerMessageId: result.data.id,
          status: "sent",
        });

        return { success: true, data: result.data };
      } catch (error) {
        lastError = error;
        console.error(
          `Failed to send ${kind} email (attempt ${attempt}):`,
          error,
        );

        // Don't retry permanent failures (bad recipient/domain, validation,
        // auth) — they will never succeed and would only block on backoff.
        if (error instanceof EmailSendError && !error.retryable) {
          break;
        }

        // If not the last attempt, wait before retrying (exponential backoff).
        if (attempt < this.maxRetries) {
          const delay = this.retryDelay * Math.pow(2, attempt - 1);

          console.log(`Retrying in ${delay}ms...`);
          await this.deps.sleep(delay);
        }
      }
    }

    const lastErrorMessage =
      lastError instanceof Error ? lastError.message : null;

    await this.safeLogSentEmail({
      recipient: customerEmail,
      emailType: kind,
      subject,
      status: "failed",
      errorMessage: lastErrorMessage,
    });

    return {
      success: false,
      error: lastErrorMessage || "Failed to send email after multiple attempts",
    };
  }

  /**
   * Best-effort audit log of one logical email. A logging failure must never
   * affect email delivery or its result, so this swallows and console.errors —
   * both the success and failure paths route through here so the behavior can't
   * drift between them.
   */
  private async safeLogSentEmail(
    data: Parameters<typeof logSentEmail>[0],
  ): Promise<void> {
    try {
      await this.deps.logSentEmail(data);
    } catch (logError) {
      console.error(
        `Failed to log ${data.status} ${data.emailType} email:`,
        describeDbError(logError),
      );
    }
  }
}

function capitalize(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1);
}

// The process-wide instance every caller uses.
export const emailService = new EmailService();
