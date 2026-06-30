/**
 * Admin Devices Router
 *
 * Device activation tracking for the admin dashboard.
 * All procedures require admin authentication.
 */
import { z } from "zod";
import { TRPCError } from "@trpc/server";

import { createTRPCRouter, adminProcedure } from "../../trpc";
import {
  getDeviceCountsPerLicense,
  getDevicesForLicense,
} from "@/src/lib/db-layer";

export const devicesRouter = createTRPCRouter({
  /**
   * List device counts per license, optionally filtered by last-active window.
   */
  list: adminProcedure
    .input(z.object({ days: z.number().positive().optional() }).optional())
    .query(async ({ input }) => {
      try {
        const days = input?.days ?? 30;
        const rows = await getDeviceCountsPerLicense(days);
        return { devices: rows, days };
      } catch (error) {
        console.error("Device counts fetch error:", error);
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message:
            error instanceof Error
              ? error.message
              : "Failed to fetch device counts",
        });
      }
    }),

  /**
   * List individual devices for a specific license key.
   */
  forLicense: adminProcedure
    .input(
      z.object({
        licenseKeyId: z.string().uuid(),
        days: z.number().positive().optional(),
      })
    )
    .query(async ({ input }) => {
      try {
        const rows = await getDevicesForLicense(
          input.licenseKeyId,
          input.days
        );
        return { devices: rows };
      } catch (error) {
        console.error("Devices for license fetch error:", error);
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message:
            error instanceof Error
              ? error.message
              : "Failed to fetch devices for license",
        });
      }
    }),
});
