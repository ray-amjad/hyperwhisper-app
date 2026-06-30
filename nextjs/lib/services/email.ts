import { resend, DEFAULT_FROM_EMAIL } from "@/lib/clients/resend";
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

export interface EmailResult {
  success: boolean;
  data?: any;
  error?: string;
}

/**
 * Error thrown when the Resend API resolves with an error object instead of
 * throwing. Carries the Resend error name/statusCode so we can decide whether
 * the failure is worth retrying.
 */
class ResendSendError extends Error {
  readonly retryable: boolean;

  constructor(message: string, name: string, statusCode: number | null) {
    super(message);
    this.name = "ResendSendError";
    this.retryable = isRetryableResendError(name, statusCode);
  }
}

/**
 * Decide whether a Resend failure is transient (worth retrying) or permanent.
 *
 * Permanent failures (invalid recipient/domain, validation errors, auth errors,
 * suppressed recipients) will never succeed on retry, so we fail fast instead of
 * burning the webhook request thread on backoff sleeps.
 */
function isRetryableResendError(
  name: string | undefined,
  statusCode: number | null,
): boolean {
  // Rate limits and server-side errors are transient.
  if (
    name === "rate_limit_exceeded" ||
    name === "internal_server_error" ||
    name === "application_error"
  ) {
    return true;
  }

  // Any 5xx (or unknown/missing status) is treated as transient.
  if (statusCode == null) return true;
  return statusCode >= 500;
}

export class EmailService {
  private static instance: EmailService;
  private readonly maxRetries = 3;
  // Base backoff. Kept small because retries run inline inside the Stripe
  // webhook request; worst-case total sleep across attempts is bounded
  // (250ms + 500ms = 750ms) to stay well under Stripe's webhook timeout.
  private readonly retryDelay = 250;

  private constructor() {}

  public static getInstance(): EmailService {
    if (!EmailService.instance) {
      EmailService.instance = new EmailService();
    }

    return EmailService.instance;
  }

  /**
   * Send license key email with retry logic
   */
  async sendLicenseKey(data: LicenseEmailData): Promise<EmailResult> {
    return this.sendWithRetry("license", data.customerEmail, () =>
      resend.emails.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject: `Your ${data.productName} License Key`,
        html: licenseEmailHtml(data),
        text: licenseEmailText(data),
      }),
    );
  }

  /**
   * Send the onboarding welcome email used after someone submits the download form.
   */
  async sendWelcomeEmail(data: WelcomeEmailData): Promise<EmailResult> {
    return this.sendWithRetry("welcome", data.customerEmail, () =>
      resend.emails.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject: `Welcome to ${data.productName}`,
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
    return this.sendWithRetry("credit-mint", data.customerEmail, () =>
      resend.emails.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject: `Your ${data.productName} key and credits`,
        html: creditMintEmailHtml(data),
        text: creditMintEmailText(data),
      }),
    );
  }

  /**
   * Send the top-up receipt: credits were added to an existing license key.
   */
  async sendCreditTopUp(data: CreditTopUpEmailData): Promise<EmailResult> {
    return this.sendWithRetry("credit-topup", data.customerEmail, () =>
      resend.emails.send({
        from: DEFAULT_FROM_EMAIL,
        to: data.customerEmail,
        subject: `${data.creditAmount.toLocaleString()} credits added`,
        html: creditTopUpEmailHtml(data),
        text: creditTopUpEmailText(data),
      }),
    );
  }

  /**
   * Shared send-with-retry loop.
   *
   * Treats a resolved `{ error }` object from the Resend SDK as a failure (the
   * SDK does NOT throw on API-level errors), so transient failures actually
   * retry and permanent failures surface as `{ success: false }` instead of a
   * false positive. Permanent (non-retryable) errors abort immediately so the
   * caller's request thread is not blocked on backoff sleeps.
   */
  private async sendWithRetry(
    kind: "license" | "welcome" | "credit-mint" | "credit-topup",
    customerEmail: string,
    send: () => Promise<{ data: unknown; error: unknown }>,
  ): Promise<EmailResult> {
    let lastError: Error | undefined;

    for (let attempt = 1; attempt <= this.maxRetries; attempt++) {
      try {
        console.log(
          `Sending ${kind} email to ${customerEmail} (attempt ${attempt}/${this.maxRetries})`,
        );

        const result = await send();

        // The Resend SDK resolves to { data, error } and does NOT throw on
        // API-level failures, so an error object must be inspected explicitly.
        if (result.error) {
          const err = result.error as {
            message?: string;
            name?: string;
            statusCode?: number | null;
          };
          throw new ResendSendError(
            err.message || `Resend returned an error sending ${kind} email`,
            err.name ?? "",
            err.statusCode ?? null,
          );
        }

        console.log(`${capitalize(kind)} email sent successfully to ${customerEmail}`);

        return { success: true, data: result.data };
      } catch (error) {
        lastError = error as Error;
        console.error(
          `Failed to send ${kind} email (attempt ${attempt}):`,
          error,
        );

        // Don't retry permanent failures (bad recipient/domain, validation,
        // auth) — they will never succeed and would only block on backoff.
        if (error instanceof ResendSendError && !error.retryable) {
          break;
        }

        // If not the last attempt, wait before retrying (exponential backoff).
        if (attempt < this.maxRetries) {
          const delay = this.retryDelay * Math.pow(2, attempt - 1);

          console.log(`Retrying in ${delay}ms...`);
          await this.sleep(delay);
        }
      }
    }

    return {
      success: false,
      error:
        lastError?.message || "Failed to send email after multiple attempts",
    };
  }

  /**
   * Sleep for a specified duration
   */
  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}

function capitalize(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1);
}

// Export singleton instance
export const emailService = EmailService.getInstance();
