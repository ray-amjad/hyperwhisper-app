import { NextResponse } from "next/server";

// DEPRECATED: local trial limits were removed (HyperWhisper is open source —
// local transcription and model downloads are unconditionally free and
// unlimited). New desktop builds no longer fetch or enforce these values, but
// legacy / un-upgraded clients still in the field DO fetch this endpoint and
// apply the returned numbers as live trial caps. So instead of the old
// 300s/day + 3-model gate, we return effectively-unlimited values to keep
// those legacy clients from gating local use.
//
// Why 2_000_000_000 specifically (not i64::MAX or 0):
//   - Legacy Windows ConfigService parses these as 32-bit `int`, so i64::MAX
//     overflows and breaks JSON deserialization. 2_000_000_000 fits in int32
//     (max 2,147,483,647) with headroom; macOS parsed as 64-bit and is fine.
//   - The Rust core enforces via `used >= limit` / `remaining = (limit-used)`,
//     so 0 or a negative value would block everything. A large positive value
//     is effectively unlimited (~63 years of seconds / 2B models) with no
//     underflow.
// Safe to delete once those legacy versions are no longer in the field.
export async function GET() {
  return NextResponse.json(
    {
      trial_daily_limit_seconds: 2_000_000_000,
      trial_model_download_limit: 2_000_000_000,
    },
    {
      headers: {
        "Cache-Control": "public, max-age=21600", // 6 hours
      },
    }
  );
}
