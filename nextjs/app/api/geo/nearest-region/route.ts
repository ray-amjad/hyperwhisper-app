import { getCloudflareContext } from "@opennextjs/cloudflare";
import { NextRequest, NextResponse } from "next/server";

import { nearestRegion } from "@/lib/latency/fly-regions";

export const dynamic = "force-dynamic";

const MAX_CANDIDATES = 60;

/**
 * Parses one coordinate. Returns null when it is absent or blank —
 * `Number(null)` and `Number("")` are both 0, which is a real, finite
 * coordinate in the Atlantic, so converting first would send every visitor
 * without geo data into a distance contest from Null Island.
 */
function parseCoordinate(raw: unknown): number | null {
  if (typeof raw === "number") {
    return Number.isFinite(raw) ? raw : null;
  }
  if (typeof raw !== "string" || raw.trim() === "") {
    return null;
  }

  const value = Number(raw);
  return Number.isFinite(value) ? value : null;
}

/** Cloudflare's `request.cf`, or undefined off Workers (node tests, next dev). */
function requestCf(): Record<string, unknown> | undefined {
  try {
    return getCloudflareContext().cf as Record<string, unknown> | undefined;
  } catch {
    return undefined;
  }
}

/**
 * Reads one visitor coordinate: Cloudflare's `request.cf` first, then the
 * `cf-iplatitude` / `cf-iplongitude` headers (the zone's "Add visitor location
 * headers" managed transform), which is also how the tests feed it.
 */
function coordinateHeader(
  request: NextRequest,
  name: "cf-iplatitude" | "cf-iplongitude",
): number | null {
  const cfKey = name === "cf-iplatitude" ? "latitude" : "longitude";
  const fromCf = parseCoordinate(requestCf()?.[cfKey]);

  if (fromCf !== null) {
    return fromCf;
  }
  return parseCoordinate(request.headers.get(name));
}

/**
 * Tells the /latency page which region column to highlight for this visitor.
 *
 * The page itself is static (revalidated hourly), so it cannot know who is
 * reading it. The client calls this one small dynamic endpoint after paint and
 * highlights the answer. Nothing about the visitor is stored or logged — the
 * coordinates are read from Cloudflare's request.cf data, used once, and dropped.
 *
 * Candidates are the region codes the matrix actually holds data for, passed by
 * the client, so the highlight can never point at an empty column.
 */
export function GET(request: NextRequest) {
  const latitude = coordinateHeader(request, "cf-iplatitude");
  const longitude = coordinateHeader(request, "cf-iplongitude");

  const candidates = (request.nextUrl.searchParams.get("regions") ?? "")
    .split(",")
    .map((code) => code.trim().toLowerCase())
    .filter((code) => /^[a-z]{3,12}$/.test(code))
    .slice(0, MAX_CANDIDATES);

  // Off Cloudflare (local dev) there is no location data. Say so plainly
  // rather than highlighting a wrong column.
  if (latitude === null || longitude === null || candidates.length === 0) {
    return NextResponse.json(
      { region: null, city: null },
      { headers: { "Cache-Control": "private, max-age=300" } },
    );
  }

  const match = nearestRegion({ lat: latitude, lon: longitude }, candidates);

  return NextResponse.json(
    {
      region: match?.region ?? null,
      city: match?.city ?? null,
    },
    // Private: this answer is specific to one visitor and must not be shared by
    // a CDN. Short: a visitor who moves gets a fresh answer soon enough.
    { headers: { "Cache-Control": "private, max-age=300" } },
  );
}