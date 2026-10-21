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
  DEVICES_PAGE_SIZE,
  getDeviceCountsPerLicense,
  getDevicesForLicense,
} from "@/src/lib/db-layer";
import { describeDbError, safeErrorMessage } from "@/lib/shared/db-error";

/**
 * The last-active window in whole days, 1 to 3650 (ten years). Without a
 * bound a huge value made `since` an Invalid Date in the db-layer and the
 * query answered 500 "Invalid time value"; now it is a 400 input error (#2083).
 */
const daysInput = z.number().int().min(1).max(3650);

export const devicesRouter = createTRPCRouter({
  /**
   * List device counts per license, optionally filtered by last-active window.
   */
  list: adminProcedure
    .input(z.object({ days: daysInput.optional() }).optional())
    .query(async ({ input }) => {
      try {
        const days = input?.days ?? 30;
        const rows = await getDeviceCountsPerLicense(days);
        return { devices: rows, days };
      } catch (error) {
        console.error("Device counts fetch error:", describeDbError(error));
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message: safeErrorMessage(error, "Failed to fetch device counts"),
          // A DB error never enters the message; `cause` keeps it for the server (#1049).
          cause: error,
        });
      }
    }),

  /**
   * One page of a license key's devices (#1661), newest validation first.
   * `cursor` is the row offset of the page (the shape `useInfiniteQuery`
   * drives); `nextCursor` is the next page's offset, or null after the last.
   * `search` filters by device name or id; `total` counts every match.
   */
  forLicense: adminProcedure
    .input(
      z.object({
        licenseKeyId: z.string().uuid(),
        days: daysInput.optional(),
        search: z.string().max(200).optional(),
        limit: z.number().int().min(1).max(100).optional(),
        cursor: z.number().int().min(0).nullish(),
      })
    )
    .query(async ({ input }) => {
      try {
        const limit = input.limit ?? DEVICES_PAGE_SIZE;
        const offset = input.cursor ?? 0;
        const { devices, total } = await getDevicesForLicense(
          input.licenseKeyId,
          input.days,
          { limit, offset, search: input.search }
        );
        const next = offset + devices.length;
        return {
          devices,
          total,
          nextCursor: devices.length === limit && next < total ? next : null,
        };
      } catch (error) {
        console.error("Devices for license fetch error:", describeDbError(error));
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message: safeErrorMessage(error, "Failed to fetch devices for license"),
          cause: error,
        });
      }
    }),
});
