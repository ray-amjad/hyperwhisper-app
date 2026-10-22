/**
 * The download form's back end, with its collaborators passed in (#774).
 *
 * `download.ts` keeps the tRPC router and builds the source from the real
 * rate limiter, database layer, email service and `fetch`; a test hands in
 * stubs. Moved across verbatim: the limit numbers, the messages and the
 * `TRPCError` codes are the router's own.
 */
import { TRPCError } from "@trpc/server";

import { APPCAST_FETCH_TIMEOUT_MS } from "@/lib/shared/appcast";
import { emailTag } from "@/lib/shared/redact";
import { describeDbError } from "@/lib/shared/db-error";
import type { RateLimitOutcome } from "@/lib/rate-limit-gate";

import type { WelcomeEmailData } from "@/lib/templates/welcome-email";

/**
 * Check if a domain is disposable.
 * Walks up the domain tree to catch subdomains.
 *
 * Example: sub.mailinator.com matches mailinator.com in list
 */
export function isDisposableDomain(emailDomain: string, list: Set<string>): boolean {
  if (list.size === 0) return false;
  let current = emailDomain.toLowerCase();

  // Walk up the domain tree: sub.a.b -> a.b -> b
  while (true) {
    if (list.has(current)) return true;
    const dotIndex = current.indexOf(".");

    if (dotIndex === -1) break;
    const next = current.slice(dotIndex + 1);

    if (!next.includes(".")) break; // avoid matching bare TLDs
    current = next;
  }

  return false;
}

export interface AppcastReaderDeps {
  fetch: typeof fetch;
}

/** Builds the appcast read around the `fetch` it is given. */
export function createAppcastReader(
  deps: AppcastReaderDeps,
): (origin: string) => Promise<string | null> {
  /**
   * Fetch and parse appcast.xml to get latest download URL.
   */
  return async function getLatestDownloadUrl(origin: string): Promise<string | null> {
    try {
      const appcastUrl = `${origin}/appcast.xml`;
      const res = await deps.fetch(appcastUrl, {
        cache: "no-store",
        signal: AbortSignal.timeout(APPCAST_FETCH_TIMEOUT_MS),
      });

      if (!res.ok) return null;

      const xml = await res.text();
      const match = xml.match(/<item>[\s\S]*?<enclosure[^>]*url="([^"]+)"/i);

      if (!match || !match[1]) return null;

      const latestUrl = new URL(match[1]);

      return latestUrl.toString();
    } catch (error) {
      console.error("Error parsing appcast:", error);
      return null;
    }
  };
}

export interface DownloadRecordSource {
  /** The caller's IP, read from the request headers. */
  clientIP: (headers: Headers) => string;
  /** One rate-limit check for this IP; the router wraps the Upstash limiter. */
  limit: (clientIP: string) => Promise<RateLimitOutcome>;
  getDisposableDomains: () => Promise<Set<string>>;
  upsertEmail: (data: { email: string; source: string }) => Promise<void>;
  sendWelcomeEmail: (
    data: WelcomeEmailData,
  ) => Promise<{ success: boolean; error?: string }>;
  latestDownloadUrl: (origin: string) => Promise<string | null>;
  /** The site origin, read once at the router edge. */
  origin: string;
  supportEmail: string;
  /** The Loom walkthrough linked from the welcome email. */
  welcomeVideoUrl: string;
}

export interface DownloadRecordResult {
  success: true;
  message: string;
  downloadUrl: string;
}

/** Builds the `recordDownload` mutation's body around its collaborators. */
export function createDownloadRecorder(
  source: DownloadRecordSource,
): (input: { email: string; headers: Headers }) => Promise<DownloadRecordResult> {
  return async function recordDownload(input) {
    try {
      // Rate limiting: 10 requests per IP per hour. Fails open, so an Upstash
      // outage never keeps a visitor from the download link (#2136).
      const clientIP = source.clientIP(input.headers);
      const rateLimit = await source.limit(clientIP);

      if (rateLimit.limited) {
        throw new TRPCError({
          code: "TOO_MANY_REQUESTS",
          message: "Too many requests. Please try again later.",
        });
      }

      // Block disposable email domains
      const domain = String(input.email).split("@")[1]?.toLowerCase();
      if (!domain) {
        throw new TRPCError({
          code: "BAD_REQUEST",
          message: "Invalid email domain",
        });
      }
      const domainList = await source.getDisposableDomains();
      if (isDisposableDomain(domain, domainList)) {
        throw new TRPCError({
          code: "BAD_REQUEST",
          message:
            "Disposable email domains are not allowed. Please use a valid email so we can send your download link.",
        });
      }

      // Store email in database (ignore if already exists)
      try {
        await source.upsertEmail({
          email: input.email,
          source: "hyperwhisper-download",
        });
      } catch (insertError) {
        console.error("Error storing email:", describeDbError(insertError));
        // Don't fail the request if email storage fails
      }

      // Log for monitoring
      const requesterTag = emailTag(input.email);
      console.log("Download requested by:", requesterTag);

      const origin = source.origin;
      const emailDownloadUrl = `${origin}/api/download`;

      const welcomeEmailPayload: WelcomeEmailData = {
        customerName: input.email.split("@")[0] || "HyperWhisper user",
        customerEmail: input.email,
        productName: "HyperWhisper",
        downloadUrl: `${origin}/download`,
        loomVideoUrl: source.welcomeVideoUrl,
        loomThumbnailUrl:
          "https://www.hyperwhisper.com/email-assets/welcome-email-thumbnail.png",
        supportEmail: source.supportEmail,
      };

      try {
        const welcomeResult =
          await source.sendWelcomeEmail(welcomeEmailPayload);

        if (!welcomeResult.success) {
          console.error(
            "Welcome email failed to send:",
            welcomeResult.error ?? "Unknown error"
          );
        }
      } catch (err) {
        console.error("Error sending welcome email:", err);
      }

      const directDownloadUrl = await source.latestDownloadUrl(origin);

      return {
        success: true,
        message: "Email recorded successfully",
        downloadUrl: directDownloadUrl || emailDownloadUrl,
      };
    } catch (error) {
      console.error("Error processing download request:", describeDbError(error));

      if (error instanceof TRPCError) {
        throw error;
      }

      throw new TRPCError({
        code: "INTERNAL_SERVER_ERROR",
        message: "Internal server error",
      });
    }
  };
}
