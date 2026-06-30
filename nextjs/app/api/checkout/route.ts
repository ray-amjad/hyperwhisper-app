import { NextRequest, NextResponse } from "next/server";

export async function GET(request: NextRequest) {
  // The standalone /checkout (license) flow was retired; send callers straight to
  // the credits buy flow, preserving any forwarded params (license_key/id/code).
  const url = new URL("/credits", request.url);
  url.search = new URL(request.url).search;

  return NextResponse.redirect(url);
}
