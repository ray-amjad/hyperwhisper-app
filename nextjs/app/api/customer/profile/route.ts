import { NextRequest, NextResponse } from "next/server";

import { auth } from "@/src/lib/auth";
import {
  getLicensesByEmail,
  getCreditBalancesForLicenses,
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
    const licenses = await getLicensesByEmail(userEmail);

    // Fetch credit balances for all licenses
    const licenseIds = licenses.map((l) => l.id);
    const creditMap = await getCreditBalancesForLicenses(licenseIds);

    // Add credits to each license object
    const licensesWithCredits = licenses.map((license) => ({
      id: license.id,
      key: license.key,
      status: license.status,
      created_at: license.createdAt.toISOString(),
      stripe_customer_id: license.stripeCustomerId,
      polar_customer_id: license.polarCustomerId,
      credits: creditMap.get(license.id) || 0,
    }));

    // Calculate total credits across all licenses
    const totalCredits =
      licensesWithCredits.reduce((sum, license) => sum + license.credits, 0) || 0;

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
