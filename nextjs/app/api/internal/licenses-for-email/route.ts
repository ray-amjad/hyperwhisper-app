import { NextRequest, NextResponse } from "next/server";
import {
  getAccountKeysByEmail,
  getCreditBalancesForUsers,
} from "@/src/lib/db-layer";
import { parseInternalEmailRequest } from "../email-request";

export async function POST(request: NextRequest) {
  const parsed = await parseInternalEmailRequest(request);
  if ("response" in parsed) return parsed.response;
  const { email } = parsed;

  try {
    // Read-only: this endpoint never mints. An email with no granted key simply
    // returns an empty list. (Minting happens only via grant-license, driven by
    // the ACS claim flow — so nothing here can accidentally create a key.)
    const all = await getAccountKeysByEmail(email);

    // Display only active keys; revoked keys are hidden. Credits are pooled per
    // account, so resolve balances by the keys' distinct owning users; each key
    // then reports its account balance.
    const granted = all.filter((l) => l.status === "granted");
    const balances = await getCreditBalancesForUsers(
      Array.from(new Set(granted.map((l) => l.userId)))
    );

    const licenses = granted.map((license) => ({
      key: license.key,
      status: license.status,
      createdAt: license.createdAt.toISOString(),
      credits: balances.get(license.userId) ?? 0,
    }));

    return NextResponse.json({ licenses });
  } catch (error) {
    console.error("Error in licenses-for-email:", error);
    return NextResponse.json({ error: "Internal server error" }, { status: 500 });
  }
}
