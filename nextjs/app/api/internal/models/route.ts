import { NextResponse } from "next/server";

import { fetchAvailableModels } from "@/lib/services/model-list";

export const dynamic = "force-dynamic";
export const runtime = "nodejs";

export async function GET() {
  return NextResponse.json(await fetchAvailableModels(), {
    headers: {
      // Let Vercel's edge cache absorb anonymous traffic without invoking the
      // function (which would fan out paid provider calls). Pairs with the
      // ~1h in-memory cache in fetchAvailableModels() for cache-busted requests.
      "Cache-Control": "public, s-maxage=3600, stale-while-revalidate=86400",
    },
  });
}
