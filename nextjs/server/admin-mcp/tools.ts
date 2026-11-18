/**
 * Admin MCP tools: look a customer up, and add HyperWhisper Cloud credits to
 * the account that owns a license key.
 *
 * Served by `app/api/mcp/admin/route.ts` (the protocol lives in
 * `./protocol.ts`). The caller is an agent gateway holding
 * HYPERWHISPER_ADMIN_MCP_TOKEN, so every tool here is admin-only by
 * construction: the route refuses every request without that token.
 *
 * Grants match the admin tRPC `addCredits` (server/api/routers/admin/
 * customers.ts): one `grantCreditLot` to the license's owning user, sourceType
 * `admin_manual`. The difference is the source id. The tRPC path draws a
 * random one; here it is derived from the caller's `idempotencyKey`, and
 * (source type, source id) is unique in `credit_grants`, so a retried call can
 * never grant twice.
 *
 * Two grant tools share one implementation and differ only in their band: up
 * to 30,000 credits ($30 at 1,000 credits per dollar) is `add_credits`; above
 * that is `add_credits_large`, which is annotated destructive so the gateway
 * holds it for a human's approval.
 */
import { z } from "zod";

import {
  findAccountByKey,
  findCreditGrantBySource,
  getAccountKeysByEmail,
  getAccountKeysWithCreditsForUserIds,
  getCreditBalance,
  getCreditGrantsForUsers,
  getUserByEmail,
  getUsersByIds,
  grantCreditLot,
  type AccountKeyRow,
} from "@/src/lib/db-layer";
import { sha256Hex } from "@/lib/shared/sha256";
import { describeDbError, dbErrorCode, isDbError } from "@/lib/shared/db-error";

/** Largest grant `add_credits` makes without a human's approval ($30). */
export const MAX_UNAPPROVED_CREDIT_GRANT = 30_000;
/** Same ceiling as the admin tRPC `addCredits`. */
export const MAX_ADMIN_CREDIT_GRANT = 1_000_000;

/** Grants listed per `find_customer` reply (newest first). */
const FIND_CUSTOMER_GRANT_LIMIT = 200;

/** The prefix that keeps MCP source ids apart from the tRPC path's random UUIDs. */
const MCP_SOURCE_ID_PREFIX = "mcp:";

/**
 * The deterministic `credit_grants.source_id` for one idempotency key. The
 * column is text, so a prefixed SHA-256 is enough: the same key always maps to
 * the same id, and the raw key (which the caller may have built from customer
 * details) never lands in the ledger.
 */
export function mcpGrantSourceId(idempotencyKey: string): string {
  return `${MCP_SOURCE_ID_PREFIX}${sha256Hex(idempotencyKey)}`;
}

// ---------------------------------------------------------------------------
// Tool results
// ---------------------------------------------------------------------------

export interface ToolResult {
  content: Array<{ type: "text"; text: string }>;
  structuredContent?: Record<string, unknown>;
  isError?: boolean;
}

function ok(data: Record<string, unknown>): ToolResult {
  return {
    content: [{ type: "text", text: JSON.stringify(data, null, 2) }],
    structuredContent: data,
  };
}

/** A tool-level failure the agent can read and act on (MCP `isError`). */
function toolError(message: string): ToolResult {
  return { content: [{ type: "text", text: message }], isError: true };
}

function validationError(tool: string, error: z.ZodError): ToolResult {
  const issues = error.issues
    .map((issue) =>
      issue.path.length > 0
        ? `${issue.path.join(".")}: ${issue.message}`
        : issue.message,
    )
    .join("; ");

  return toolError(`Invalid arguments for ${tool}: ${issues}`);
}

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------

type LogFields = Record<string, string | number | boolean | null | undefined>;

/**
 * One structured line per grant attempt, in the shape the admin router's
 * `withAdminLog` writes: `ok`, `rejected` (a refusal the agent sees as an
 * `isError` result) or `failed` (unexpected). Never the bearer token, never a
 * license key string, never an email address.
 */
function logGrant(
  outcome: "ok" | "rejected" | "failed",
  fields: LogFields,
): void {
  const line = JSON.stringify({
    event: "admin.mcp.add_credits",
    outcome,
    ...fields,
  });

  if (outcome === "ok") console.log(line);
  else if (outcome === "rejected") console.warn(line);
  else console.error(line);
}

// ---------------------------------------------------------------------------
// find_customer
// ---------------------------------------------------------------------------

const findCustomerInput = z
  .strictObject({
    email: z.string().trim().toLowerCase().email().optional(),
    licenseKey: z.string().trim().min(1).max(200).optional(),
  })
  .refine((v) => (v.email === undefined) !== (v.licenseKey === undefined), {
    message: "Pass exactly one of `email` or `licenseKey`",
  });

function isoDate(date: Date | null): string | null {
  return date ? date.toISOString() : null;
}

async function findCustomer(args: unknown): Promise<ToolResult> {
  const parsed = findCustomerInput.safeParse(args ?? {});

  if (!parsed.success) return validationError("find_customer", parsed.error);
  const { email, licenseKey } = parsed.data;

  // Resolve the matching accounts (users). An email matches both the user's
  // login email and any license row still carrying that email.
  const userIds = new Set<string>();

  if (licenseKey !== undefined) {
    const license = await findAccountByKey(licenseKey);

    if (license) userIds.add(license.userId);
  } else if (email !== undefined) {
    const [licenses, user] = await Promise.all([
      getAccountKeysByEmail(email),
      getUserByEmail(email),
    ]);

    for (const l of licenses) userIds.add(l.userId);
    if (user) userIds.add(user.id);
  }

  const ids = Array.from(userIds);

  if (ids.length === 0) {
    return ok({ customers: [], message: "No matching customer" });
  }

  const [licenses, users, grants] = await Promise.all([
    getAccountKeysWithCreditsForUserIds(ids),
    getUsersByIds(ids),
    getCreditGrantsForUsers(ids, FIND_CUSTOMER_GRANT_LIMIT),
  ]);

  const customers = await Promise.all(
    ids.map(async (userId) => {
      const own = licenses.filter((l) => l.userId === userId);
      // Credits are pooled per account; a user with no license row still has
      // a balance worth reporting, so read it directly in that case.
      const balance = own[0]?.credits ?? (await getCreditBalance(userId));

      return {
        userId,
        email: users.get(userId)?.email ?? own[0]?.email ?? null,
        creditBalance: balance,
        licenses: own.map((l: AccountKeyRow & { credits: number }) => ({
          id: l.id,
          key: l.key,
          status: l.status,
          email: l.email,
          createdAt: isoDate(l.createdAt),
          matched:
            licenseKey !== undefined
              ? l.key === licenseKey
              : l.email.toLowerCase() === email,
        })),
        creditGrants: grants
          .filter((g) => g.userId === userId)
          .map((g) => ({
            sourceType: g.sourceType,
            originalAmount: g.originalAmount,
            remainingAmount: g.remainingAmount,
            refundedAmount: g.refundedAmount,
            status: g.status,
            createdAt: isoDate(g.createdAt),
            expiresAt: isoDate(g.expiresAt),
          })),
      };
    }),
  );

  return ok({ customers });
}

// ---------------------------------------------------------------------------
// add_credits / add_credits_large
// ---------------------------------------------------------------------------

type GrantBand = "standard" | "large";

const grantInput = z.strictObject({
  licenseKey: z.string().trim().min(1).max(200),
  credits: z.number().int(),
  reason: z.string().trim().min(1).max(500),
  idempotencyKey: z.string().trim().min(8).max(200),
});

function bandError(band: GrantBand, credits: number): string | null {
  if (band === "standard") {
    if (credits < 1) return "credits must be at least 1";
    if (credits > MAX_UNAPPROVED_CREDIT_GRANT) {
      return `credits must be at most ${MAX_UNAPPROVED_CREDIT_GRANT} for add_credits; use add_credits_large (needs human approval) for ${credits} credits`;
    }

    return null;
  }
  if (credits <= MAX_UNAPPROVED_CREDIT_GRANT) {
    return `add_credits_large is only for more than ${MAX_UNAPPROVED_CREDIT_GRANT} credits; use add_credits for ${credits} credits`;
  }
  if (credits > MAX_ADMIN_CREDIT_GRANT) {
    return `credits must be at most ${MAX_ADMIN_CREDIT_GRANT}`;
  }

  return null;
}

async function addCredits(band: GrantBand, args: unknown): Promise<ToolResult> {
  const tool = band === "standard" ? "add_credits" : "add_credits_large";
  const parsed = grantInput.safeParse(args ?? {});

  if (!parsed.success) return validationError(tool, parsed.error);
  const { licenseKey, credits, reason, idempotencyKey } = parsed.data;

  const sourceId = mcpGrantSourceId(idempotencyKey);
  const log: LogFields = { tool, amount: credits, sourceId, reason };

  const outOfBand = bandError(band, credits);

  if (outOfBand) {
    logGrant("rejected", { ...log, rejection: "out_of_band" });

    return toolError(outOfBand);
  }

  try {
    const license = await findAccountByKey(licenseKey);

    if (!license) {
      logGrant("rejected", { ...log, rejection: "license_not_found" });

      return toolError("License key not found");
    }
    log.licenseKeyId = license.id;
    log.userId = license.userId;
    if (license.status !== "granted") {
      logGrant("rejected", {
        ...log,
        rejection: "license_not_active",
        licenseStatus: license.status,
      });

      return toolError(
        `License key is ${license.status}; credits can only be added to a granted (active) license`,
      );
    }

    const result = await grantCreditLot({
      userId: license.userId,
      amount: credits,
      sourceType: "admin_manual",
      sourceId,
    });

    if (result.status === "duplicate") {
      // Nothing was written. Confirm the earlier grant is the SAME request
      // (account and amount) before calling it a harmless retry: a key reused
      // for a different grant must be refused, not silently swallowed.
      const existing = await findCreditGrantBySource("admin_manual", sourceId);

      if (
        !existing ||
        existing.userId !== license.userId ||
        existing.originalAmount !== credits
      ) {
        logGrant("rejected", { ...log, rejection: "idempotency_key_reused" });

        return toolError(
          "idempotencyKey was already used for a different grant (another account or amount); nothing was added. Use a new idempotencyKey for a new grant.",
        );
      }
      const balance = await getCreditBalance(license.userId);

      log.newBalance = balance;
      log.status = "duplicate";
      logGrant("ok", log);

      return ok({
        status: "duplicate",
        licenseKeyId: license.id,
        creditsAdded: 0,
        creditsOfOriginalGrant: credits,
        grantedAt: isoDate(existing.createdAt),
        newBalance: balance,
      });
    }

    log.newBalance = result.balance;
    log.status = "processed";
    logGrant("ok", log);

    return ok({
      status: "processed",
      licenseKeyId: license.id,
      creditsAdded: credits,
      newBalance: result.balance,
    });
  } catch (error) {
    if (isDbError(error)) {
      log.dbCode = dbErrorCode(error);
    }
    logGrant("failed", log);
    console.error("admin MCP grant error:", describeDbError(error));

    // Safe to retry: the same idempotencyKey can never grant twice.
    return toolError(
      "Internal error while adding credits; retry with the same idempotencyKey",
    );
  }
}

// ---------------------------------------------------------------------------
// Tool catalogue
// ---------------------------------------------------------------------------

const grantInputSchema = (min: number, max: number) => ({
  type: "object",
  properties: {
    licenseKey: {
      type: "string",
      description:
        "The customer's license (Account) key. Credits go to the account that owns it.",
    },
    credits: {
      type: "integer",
      minimum: min,
      maximum: max,
      description: "Credits to add. 1,000 credits = $1.",
    },
    reason: {
      type: "string",
      minLength: 1,
      maxLength: 500,
      description: "Why the credits are being added; written to the audit log.",
    },
    idempotencyKey: {
      type: "string",
      minLength: 8,
      maxLength: 200,
      description:
        'A unique key for THIS grant. Retrying with the same key never grants twice (it returns status "duplicate"). Use a new key for every new grant.',
    },
  },
  required: ["licenseKey", "credits", "reason", "idempotencyKey"],
  additionalProperties: false,
});

export interface ToolDefinition {
  name: string;
  title: string;
  description: string;
  inputSchema: Record<string, unknown>;
  annotations: {
    title: string;
    readOnlyHint: boolean;
    destructiveHint: boolean;
    idempotentHint: boolean;
    openWorldHint: boolean;
  };
}

export const ADMIN_MCP_TOOLS: ToolDefinition[] = [
  {
    name: "find_customer",
    title: "Find customer",
    description:
      "Look up a HyperWhisper customer by email or license key (exactly one). Returns each matching account: its licenses (id, key, status, email, created date), the account's pooled Cloud credit balance, and its credit grants (source type, original, remaining, refunded, created). Read-only.",
    inputSchema: {
      type: "object",
      properties: {
        email: { type: "string", description: "Customer email address." },
        licenseKey: { type: "string", description: "A license (Account) key." },
      },
      additionalProperties: false,
    },
    annotations: {
      title: "Find customer",
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
  {
    name: "add_credits",
    title: "Add credits",
    description: `Add 1 to ${MAX_UNAPPROVED_CREDIT_GRANT.toLocaleString("en-US")} HyperWhisper Cloud credits (up to $30; 1,000 credits = $1) to the account that owns a granted license key. Idempotent on idempotencyKey. For more than ${MAX_UNAPPROVED_CREDIT_GRANT.toLocaleString("en-US")} credits use add_credits_large. Returns status (processed or duplicate), credits added and the new balance.`,
    inputSchema: grantInputSchema(1, MAX_UNAPPROVED_CREDIT_GRANT),
    annotations: {
      title: "Add credits",
      readOnlyHint: false,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
  {
    name: "add_credits_large",
    title: "Add credits (large, needs approval)",
    description: `Add more than ${MAX_UNAPPROVED_CREDIT_GRANT.toLocaleString("en-US")} (over $30) and at most ${MAX_ADMIN_CREDIT_GRANT.toLocaleString("en-US")} HyperWhisper Cloud credits to the account that owns a granted license key. NEEDS HUMAN APPROVAL: the gateway holds this call until a person approves it. Otherwise identical to add_credits.`,
    inputSchema: grantInputSchema(
      MAX_UNAPPROVED_CREDIT_GRANT + 1,
      MAX_ADMIN_CREDIT_GRANT,
    ),
    annotations: {
      title: "Add credits (large, needs approval)",
      readOnlyHint: false,
      destructiveHint: true,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
];

/** Runs one tool. Returns null for an unknown tool name (a protocol error). */
export async function callAdminTool(
  name: string,
  args: unknown,
): Promise<ToolResult | null> {
  switch (name) {
    case "find_customer":
      return findCustomer(args);
    case "add_credits":
      return addCredits("standard", args);
    case "add_credits_large":
      return addCredits("large", args);
    default:
      return null;
  }
}
