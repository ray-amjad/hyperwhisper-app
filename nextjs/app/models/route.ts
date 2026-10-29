import { NextResponse } from "next/server";

import { fetchAvailableModels } from "@/lib/services/model-list";

export const dynamic = "force-dynamic";
export const runtime = "nodejs";

export async function GET() {
  return NextResponse.json(await fetchAvailableModels(), {
    headers: {
      // Lets a shared cache absorb anonymous traffic without invoking the
      // function (which would fan out paid provider calls). On Cloudflare a
      // Worker response is cached only under a zone Cache Rule for this path
      // (DEPLOY-CLOUDFLARE.md); the ~1h per-isolate in-memory cache in
      // fetchAvailableModels() bounds the fan-out either way.
      "Cache-Control": "public, s-maxage=3600, stale-while-revalidate=86400",
    },
  });
}
