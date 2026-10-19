import { NextRequest, NextResponse } from "next/server";

import { checkoutRateLimiter } from "@/lib/rate-limit";
import { rateLimitGate } from "@/lib/rate-limit-gate";
import { getClientIPFromHeaders } from "@/server/api/routers/download-ip";

export async function GET(request: NextRequest) {
  // Same per-IP budget as /api/checkout/credits, which this redirects into (#2010).
  const limited = await rateLimitGate(
    checkoutRateLimiter,
    getClientIPFromHeaders(request.headers),
    "GET /api/checkout",
  );
  if (limited) return limited;

  // The standalone /checkout (license) flow was retired; send callers straight to
  // the credits buy flow, preserving any forwarded params (license_key/id/code).
  const url = new URL("/credits", request.url);
  url.search = new URL(request.url).search;

  return NextResponse.redirect(url);
}
