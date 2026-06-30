import { NextResponse } from "next/server";

// DEPRECATED: local trial limits were removed (HyperWhisper is open source —
// local transcription and model downloads are unconditionally free and
// unlimited). The desktop apps no longer fetch or enforce these values; this
// endpoint is kept only so older app versions still get a valid response.
// Safe to delete once those versions are no longer in the field.
export async function GET() {
  return NextResponse.json(
    {
      trial_daily_limit_seconds: 300,
      trial_model_download_limit: 3,
    },
    {
      headers: {
        "Cache-Control": "public, max-age=21600", // 6 hours
      },
    }
  );
}
