import { NextRequest, NextResponse } from "next/server";

export async function GET(request: NextRequest) {
  const url = new URL("/checkout", request.url);

  return NextResponse.redirect(url);
}
