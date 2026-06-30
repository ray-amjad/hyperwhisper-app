/**
 * Download Router
 *
 * Handles download URL generation and email recording for HyperWhisper downloads.
 * All procedures are public (anyone can download).
 *
 * PROCEDURES:
 * - getLatestUrl: Parses appcast.xml to get latest DMG download URL
 * - recordDownload: Records email, stores in database, sends welcome email
 *
 * INTEGRATIONS:
 * - Database: Email storage (emails table) via db-layer
 * - Resend: Welcome email sending
 * - appcast.xml: Version/download URL source
 */
import { z } from "zod";
import { TRPCError } from "@trpc/server";

import { createTRPCRouter, publicProcedure } from "../trpc";
import { upsertEmail } from "@/src/lib/db-layer";
import { disposableDomains } from "@/lib/disposable_domains";
import { emailService } from "@/lib/services/email";
import { downloadEmailRateLimiter } from "@/lib/rate-limit";
import { getCountryFromIP } from "@/lib/services/geolocation";
import { getClientIPFromHeaders } from "./download-ip";

import type { WelcomeEmailData } from "@/lib/templates/welcome-email";

// ============================================================================
// HELPER FUNCTIONS
// ============================================================================

let cachedDisposableDomains: Set<string> | null = null;
let lastLoadMs = 0;
const RELOAD_INTERVAL_MS = 1000 * 60 * 60; // 1 hour

/**
 * Get the disposable domains list (cached for 1 hour).
 * Used to block temporary email addresses.
 */
async function getDisposableDomains(): Promise<Set<string>> {
  const now = Date.now();

  if (cachedDisposableDomains && now - lastLoadMs < RELOAD_INTERVAL_MS) {
    return cachedDisposableDomains;
  }

  const lines = disposableDomains
    .map((domain) => domain.trim().toLowerCase())
    .filter(Boolean);

  cachedDisposableDomains = new Set(lines);
  lastLoadMs = now;

  return cachedDisposableDomains;
}

/**
 * Check if a domain is disposable.
 * Walks up the domain tree to catch subdomains.
 *
 * Example: sub.mailinator.com matches mailinator.com in list
 */
function isDisposableDomain(emailDomain: string, list: Set<string>): boolean {
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

/**
 * Fetch and parse appcast.xml to get latest download URL.
 * Swaps hostname to CDN (builds-cdn.hyperwhisper.com).
 */
async function getLatestDownloadUrl(origin: string): Promise<string | null> {
  try {
    const appcastUrl = `${origin}/appcast.xml`;
    const res = await fetch(appcastUrl, { cache: "no-store" });

    if (!res.ok) return null;

    const xml = await res.text();
    const match = xml.match(/<item>[\s\S]*?<enclosure[^>]*url="([^"]+)"/i);

    if (!match || !match[1]) return null;

    const latestUrl = new URL(match[1]);
    latestUrl.hostname = "builds-cdn.hyperwhisper.com";

    return latestUrl.toString();
  } catch (error) {
    console.error("Error parsing appcast:", error);
    return null;
  }
}

// ============================================================================
// ROUTER
// ============================================================================

export const downloadRouter = createTRPCRouter({
  /**
   * Get the latest download URL from appcast.xml.
   */
  getLatestUrl: publicProcedure.query(async () => {
    // Use production URL as default since we don't have request context
    const origin = process.env.NEXT_PUBLIC_SITE_URL || "https://hyperwhisper.com";
    const downloadUrl = await getLatestDownloadUrl(origin);

    if (!downloadUrl) {
      throw new TRPCError({
        code: "INTERNAL_SERVER_ERROR",
        message: "Failed to get latest download URL",
      });
    }

    return { downloadUrl };
  }),

  /**
   * Record a download with email tracking.
   */
  recordDownload: publicProcedure
    .input(
      z.object({
        email: z.string().email("Invalid email format"),
      })
    )
    .mutation(async ({ input, ctx }) => {
      try {
        // Rate limiting: 10 requests per IP per hour
        const clientIP = getClientIPFromHeaders(ctx.headers);
        const { success } = await downloadEmailRateLimiter.limit(clientIP);

        if (!success) {
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
        const domainList = await getDisposableDomains();
        if (isDisposableDomain(domain, domainList)) {
          throw new TRPCError({
            code: "BAD_REQUEST",
            message:
              "Disposable email domains are not allowed. Please use a valid email so we can send your download link.",
          });
        }

        // Geolocate the IP for social proof display
        let country: string | null = null;
        try {
          country = await getCountryFromIP(clientIP);
        } catch {
          // Geolocation is best-effort; don't block the download
        }

        // Store email in database (ignore if already exists)
        try {
          await upsertEmail({
            email: input.email,
            source: "hyperwhisper-download",
            ipAddress: clientIP !== "unknown" ? clientIP : null,
            userAgent: ctx.headers.get("user-agent") || null,
            country,
          });
        } catch (insertError) {
          console.error("Error storing email:", insertError);
          // Don't fail the request if email storage fails
        }

        // Log for monitoring
        console.log("Download requested by:", input.email);

        const origin = process.env.NEXT_PUBLIC_SITE_URL || "https://hyperwhisper.com";
        const emailDownloadUrl = `${origin}/api/download`;

        const welcomeEmailPayload: WelcomeEmailData = {
          customerName: input.email.split("@")[0] || "HyperWhisper user",
          customerEmail: input.email,
          productName: "HyperWhisper",
          downloadUrl: `${origin}/download`,
          loomVideoUrl:
            "https://www.loom.com/share/fd73e59755f9473b8bde341845c402e9?sid=e1892b2e-3ce6-4542-b512-31777a400909",
          loomThumbnailUrl:
            "https://www.hyperwhisper.com/email-assets/welcome-email-thumbnail.png",
          supportEmail: process.env.SUPPORT_EMAIL ?? "support@hyperwhisper.com",
        };

        try {
          const welcomeResult =
            await emailService.sendWelcomeEmail(welcomeEmailPayload);

          if (!welcomeResult.success) {
            console.error(
              "Welcome email failed to send:",
              welcomeResult.error ?? "Unknown error"
            );
          }
        } catch (err) {
          console.error("Error sending welcome email:", err);
        }

        const directDownloadUrl = await getLatestDownloadUrl(origin);

        return {
          success: true,
          message: "Email recorded successfully",
          downloadUrl: directDownloadUrl || emailDownloadUrl,
        };
      } catch (error) {
        console.error("Error processing download request:", error);

        if (error instanceof TRPCError) {
          throw error;
        }

        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message: "Internal server error",
        });
      }
    }),
});
