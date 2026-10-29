import { NextResponse } from "next/server";

import { fetchAvailableModels } from "@/lib/services/model-list";

export const dynamic = "force-dynamic";
export const runtime = "nodejs";

export async function GET() {
  return NextResponse.json(await fetchAvailableModels(), {
    headers: {
      // Lets a shared cache absorb anonymous traffic without invoking the
      // function (which would fan out paid provider calls). On Cloudflare the
      // Worker on its custom domain runs in front of the edge cache, so a zone
      // Cache Rule does not cache this response (tried 2026-10-29); the ~1h
      // per-isolate in-memory cache in fetchAvailableModels() bounds the
      // fan-out instead.
      "Cache-Control": "public, s-maxage=3600, stale-while-revalidate=86400",
    },
  });
}
