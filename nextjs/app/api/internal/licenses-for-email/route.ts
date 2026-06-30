import { NextRequest, NextResponse } from "next/server";
import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import {
  getAccountKeysByEmail,
  getCreditBalancesForUsers,
  provisionAccountKeyForEmail,
} from "@/src/lib/db-layer";

export async function POST(request: NextRequest) {
  // Validate internal secret
  const secret = request.headers.get("x-internal-secret");
  if (!timingSafeEqualSecret(secret, process.env.HYPERWHISPER_INTERNAL_SECRET)) {
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  }

  let email: string;
  try {
    const body = await request.json();
    email = body?.email;
    if (!email || typeof email !== "string") {
      return NextResponse.json({ error: "email is required" }, { status: 400 });
    }
    email = email.toLowerCase().trim();
  } catch {
    return NextResponse.json({ error: "Invalid JSON body" }, { status: 400 });
  }

  try {
    // Mint only when the email has *zero* keys of any status. A revoked-only
    // email is intentionally not re-minted (closes the refund-then-regrant gap):
    // it returns an empty list rather than a fresh free key.
    let all = await getAccountKeysByEmail(email);
    if (all.length === 0) {
      // The freshly minted row is granted with the full 5,000-credit bundle, so
      // use it directly instead of issuing a second read for the same row.
      all = [await provisionAccountKeyForEmail(email)];
    }

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
