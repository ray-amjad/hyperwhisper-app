/**
 * Admin Router
 *
 * Combines all admin sub-routers.
 * All procedures in this namespace require admin authentication.
 *
 * SUB-ROUTERS:
 * - stats: Dashboard statistics (customer counts)
 * - customers: Customer listing with meter balances
 * - devices: Device activation tracking
 *
 * USAGE:
 * api.admin.stats.get.useQuery()
 * api.admin.customers.list.useQuery()
 * api.admin.devices.list.useQuery()
 */
import { createTRPCRouter } from "../../trpc";
import { statsRouter } from "./stats";
import { customersRouter } from "./customers";
import { devicesRouter } from "./devices";
export const adminRouter = createTRPCRouter({
  stats: statsRouter,
  customers: customersRouter,
  devices: devicesRouter,
});
