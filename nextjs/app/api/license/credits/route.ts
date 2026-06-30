import { NextRequest, NextResponse } from "next/server";

import { validateCreditDeductionAmount } from "./validation";

import {
  deductCreditBalance,
  findAccountByKey,
  getCreditBalance,
} from "@/src/lib/db-layer";

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
 * - The license key itself acts as authentication
 * - Only valid license keys can query/deduct credits
 */

/**
 * GET /api/license/credits
 *
 * Get credit balance for a license key.
 */
export async function GET(req: NextRequest) {
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
    console.error("Credits balance error:", error);

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
  try {
    const body = await req.json();
    const { license_key, amount, metadata } = body;

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
      console.error("Credit deduction failed:", updateError);
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
    console.error("Credits deduction error:", error);

    return NextResponse.json(
      { error: "Failed to deduct credits" },
      { status: 500 }
    );
  }
}
