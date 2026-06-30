import { NextResponse } from "next/server";

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
