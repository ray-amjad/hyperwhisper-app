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

import { getClientIPFromHeaders } from "./download-ip";
import { createDisposableDomainCache } from "./download-disposable-domains";
import {
  createAppcastReader,
  createDownloadRecorder,
} from "./download-appcast";

import { upsertEmail } from "@/src/lib/db-layer";
import { disposableDomains } from "@/lib/disposable_domains";
import { emailService } from "@/lib/services/email";
import { downloadEmailRateLimiter } from "@/lib/rate-limit";
import { countFailOpen } from "@/lib/rate-limit-gate";
import { env } from "@/src/env/server.mjs";

// ============================================================================
// WIRING
// ============================================================================

const origin = env.NEXT_PUBLIC_SITE_URL || "https://hyperwhisper.com";

// `fetch` is looked up on each call, not bound here.
const getLatestDownloadUrl = createAppcastReader({
  fetch: (input, init) => fetch(input, init),
});

const recordDownload = createDownloadRecorder({
  clientIP: getClientIPFromHeaders,
  limit: (clientIP) =>
    countFailOpen(
      () => downloadEmailRateLimiter.limit(clientIP),
      "download email",
    ),
  // Cached for 1 hour. Used to block temporary email addresses.
  getDisposableDomains: createDisposableDomainCache(disposableDomains),
  upsertEmail,
  sendWelcomeEmail: (data) => emailService.sendWelcomeEmail(data),
  latestDownloadUrl: getLatestDownloadUrl,
  origin,
  supportEmail: env.SUPPORT_EMAIL ?? "hi@support.hyperwhisper.com",
});

// ============================================================================
// ROUTER
// ============================================================================

export const downloadRouter = createTRPCRouter({
  /**
   * Get the latest download URL from appcast.xml.
   */
  getLatestUrl: publicProcedure.query(async () => {
    // Use production URL as default since we don't have request context
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
      }),
    )
    .mutation(({ input, ctx }) =>
      recordDownload({ email: input.email, headers: ctx.headers }),
    ),
});
