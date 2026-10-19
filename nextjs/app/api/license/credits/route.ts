import { NextRequest, NextResponse } from "next/server";

import { validateCreditDeductionAmount } from "./validation";

import {
  deductCreditBalance,
  findAccountByKey,
  getCreditBalance,
} from "@/src/lib/db-layer";
import { isRecord } from "@/src/lib/type-guards";
import { unparseableRequestFields } from "@/src/lib/unparseable-request-fields";
import { describeDbError } from "@/lib/shared/db-error";
import { licenseCreditsRateLimiter } from "@/lib/rate-limit";
import { rateLimitGate } from "@/lib/rate-limit-gate";
import { getClientIPFromHeaders } from "@/server/api/routers/download-ip";
import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";

/**
 * License Credits API
 *
 * Manages credit balance for licensed users.
 * Used by HyperWhisper Cloud (CF Workers) to check balance and deduct credits.
 *
 * ENDPOINTS:
 * - GET: Get credit balance for a license key
 * - POST: Deduct credits (record usage)
 *
 * SECURITY:
 * - GET: the license key itself acts as authentication. It answers "is this
 *   key real, and what is it worth" to anyone, so it is rate limited per IP
 *   before any DB lookup (`licenseCreditsRateLimiter`, 300/min on its own
 *   budget, #2010).
 * - POST is cloud-only (#2021): the caller must send `x-internal-secret`
 *   equal to `HYPERWHISPER_INTERNAL_SECRET` (timing-safe, same gate as
 *   `/api/internal/*`). It fails closed — secret unset on the server, header
 *   missing or wrong gives 401 before the body is read or the DB is touched.
 *   `/api/account/credits` re-exports this handler, so it gets the same gate.
 */

/**
 * GET /api/license/credits
 *
 * Get credit balance for a license key.
 */
export async function GET(req: NextRequest) {
  const limited = await rateLimitGate(
    licenseCreditsRateLimiter,
    getClientIPFromHeaders(req.headers),
    "GET /api/license/credits",
  );

  if (limited) return limited;

  const { searchParams } = new URL(req.url);
  const licenseKey = searchParams.get("license_key");

  if (!licenseKey) {
    return NextResponse.json(
      { error: "license_key is required" },
      { status: 400 }
    );
  }

  try {
    const license = await findAccountByKey(licenseKey.trim());

    if (!license) {
      return NextResponse.json(
        { error: "License key not found" },
        { status: 400 }
      );
    }

    if (license.status !== "granted") {
      return NextResponse.json(
        { error: `License is ${license.status}` },
        { status: 400 }
      );
    }

    const credits = await getCreditBalance(license.userId);

    return NextResponse.json({
      credits,
      stripe_customer_id: license.stripeCustomerId,
    });
  } catch (error) {
    console.error("Credits balance error:", describeDbError(error));

    return NextResponse.json(
      { error: "Failed to get credit balance" },
      { status: 500 }
    );
  }
}

/**
 * POST /api/license/credits
 *
 * Deduct credits from a license (record usage).
 */
export async function POST(req: NextRequest) {
  // Cloud-only (#2021): checked before the body is parsed or the DB is touched,
  // so this endpoint cannot double as a key checker for anyone else.
  if (
    !timingSafeEqualSecret(
      req.headers.get("x-internal-secret"),
      process.env.HYPERWHISPER_INTERNAL_SECRET,
    )
  ) {
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  }

  let body: unknown;

  try {
    body = await req.json();
  } catch (err) {
    console.error(
      "Credits deduction: request JSON did not parse",
      unparseableRequestFields(req, err),
    );

    return NextResponse.json(
      { error: "Invalid request body" },
      { status: 400 },
    );
  }

  try {
    const { license_key, amount, metadata } = isRecord(body) ? body : {};

    if (!license_key || typeof license_key !== "string") {
      return NextResponse.json(
        { error: "license_key is required" },
        { status: 400 }
      );
    }

    const amountError = validateCreditDeductionAmount(amount);

    if (amountError) {
      return NextResponse.json({ error: amountError }, { status: 400 });
    }

    // The validator proves this branch. Keep the local check so TypeScript also
    // narrows the untrusted JSON value.
    if (typeof amount !== "number") {
      return NextResponse.json(
        { error: "amount must be a finite positive number" },
        { status: 400 }
      );
    }

    const license = await findAccountByKey(license_key.trim());

    if (!license) {
      return NextResponse.json(
        { error: "License key not found" },
        { status: 400 }
      );
    }

    if (license.status !== "granted") {
      return NextResponse.json(
        { error: `License is ${license.status}` },
        { status: 400 }
      );
    }

    let newCredits: number;

    try {
      // Atomic SQL decrement (floored at 0) — concurrent POSTs cannot
      // double-spend via a read-then-write race.
      newCredits = await deductCreditBalance(license.userId, amount);
    } catch (updateError) {
      console.error("Credit deduction failed:", describeDbError(updateError));
      return NextResponse.json(
        { error: "Failed to deduct credits. Please retry." },
        { status: 409 }
      );
    }

    console.log(`Credits deducted: ${amount} from license ${license_key.substring(0, 7)}...`, {
      remaining: newCredits,
      metadata,
    });

    return NextResponse.json({
      credits_remaining: newCredits,
      credits_deducted: amount,
    });
  } catch (error) {
    console.error("Credits deduction error:", describeDbError(error));

    return NextResponse.json(
      { error: "Failed to deduct credits" },
      { status: 500 }
    );
  }
}
