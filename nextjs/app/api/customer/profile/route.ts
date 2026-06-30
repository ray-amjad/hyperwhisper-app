import { NextRequest, NextResponse } from "next/server";

import { auth } from "@/src/lib/auth";
import {
  getAccountKeysByEmail,
  getCreditBalancesForUsers,
} from "@/src/lib/db-layer";

/**
 * Customer Profile API
 *
 * Returns the authenticated customer's profile and license information.
 *
 * GET /api/customer/profile
 * Returns:
 * - user: { id, email }
 * - licenses: Array of license objects with credits
 * - totalCredits: Sum of all credits across licenses
 *
 * Requires authentication via Better Auth session cookie.
 */
export async function GET(req: NextRequest) {
  const session = await auth.api.getSession({ headers: req.headers });
  if (!session?.user) {
    return NextResponse.json(
      { error: "Unauthorized" },
      { status: 401 }
    );
  }
  const user = session.user;

  try {
    const userEmail = user.email?.toLowerCase() ?? "";

    // Fetch user's license keys from database
    const licenses = await getAccountKeysByEmail(userEmail);

    // Credits are pooled per account. Resolve balances by the licenses' distinct
    // owning users; each license then reports its account balance.
    const userIds = Array.from(new Set(licenses.map((l) => l.userId)));
    const creditMap = await getCreditBalancesForUsers(userIds);

    // Add credits to each license object
    const licensesWithCredits = licenses.map((license) => ({
      id: license.id,
      key: license.key,
      status: license.status,
      created_at: license.createdAt.toISOString(),
      stripe_customer_id: license.stripeCustomerId,
      polar_customer_id: license.polarCustomerId,
      credits: creditMap.get(license.userId) || 0,
    }));

    // Total credits = the sum of the DISTINCT account balances (not a per-license
    // sum, which would double-count a multi-key account's pooled balance).
    const totalCredits = userIds.reduce(
      (sum, uid) => sum + (creditMap.get(uid) || 0),
      0
    );

    return NextResponse.json({
      user: {
        id: user.id,
        email: user.email,
      },
      licenses: licensesWithCredits,
      totalCredits,
    });
  } catch (error) {
    console.error("Profile API error:", error);
    return NextResponse.json(
      { error: "Internal server error" },
      { status: 500 }
    );
  }
}
