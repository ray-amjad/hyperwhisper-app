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
  ACCOUNT_KEYS_PAGE_SIZE,
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
   * One page of device counts per license (#1948), optionally filtered by
   * last-active window, biggest count first, then by key id. `cursor` is the
   * row offset of the page (the shape `useInfiniteQuery` drives); `nextCursor`
   * is the next page's offset, or null after the last. `search` filters by
   * email or Account Key; `total` counts every matching key.
   */
  list: adminProcedure
    .input(
      z
        .object({
          days: daysInput.optional(),
          search: z.string().max(200).optional(),
          limit: z.number().int().min(1).max(100).optional(),
          cursor: z.number().int().min(0).nullish(),
        })
        .optional(),
    )
    .query(async ({ input }) => {
      try {
        const days = input?.days ?? 30;
        const limit = input?.limit ?? ACCOUNT_KEYS_PAGE_SIZE;
        const offset = input?.cursor ?? 0;
        const { rows, total } = await getDeviceCountsPerLicense(days, {
          limit,
          offset,
          search: input?.search,
        });
        const next = offset + rows.length;

        return {
          devices: rows,
          total,
          nextCursor: rows.length === limit && next < total ? next : null,
          days,
        };
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
