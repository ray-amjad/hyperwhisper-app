import { NextResponse } from "next/server";

type Platform = "mac" | "windows";
type WindowsArch = "x64" | "arm64";

// Fail fast instead of holding a serverless function open for the full
// platform max duration if the appcast origin is slow or hangs.
const APPCAST_FETCH_TIMEOUT_MS = 3000;

// Force this route to be evaluated on every request so we always
// read the latest appcast and produce a fresh redirect.
export const dynamic = "force-dynamic";

function getAppcastFilename(platform: Platform) {
  return platform === "windows" ? "appcast-windows.xml" : "appcast.xml";
}

/**
 * Validate an untrusted `arch` query value against the allow-list of
 * supported Windows architectures. Returns `null` for any other value so
 * it can never reach the dynamically-constructed appcast RegExp (ReDoS).
 */
function parseWindowsArch(input: string | null): WindowsArch | null {
  return input === "x64" || input === "arm64" ? input : null;
}

/**
 * Detect Windows architecture from User-Agent header.
 * ARM64 Windows reports "ARM64" or "ARM" in the UA string.
 * Default to x64 for all other cases (most common).
 */
function detectWindowsArch(userAgent: string): WindowsArch {
  const ua = userAgent.toLowerCase();

  if (ua.includes("arm64") || ua.includes("arm")) {
    return "arm64";
  }

  return "x64";
}

async function getLatestDownloadUrl(
  origin: string,
  platform: Platform,
  userAgent: string,
  explicitArch?: WindowsArch | null,
): Promise<string | null> {
  try {
    const appcastUrl = `${origin}/${getAppcastFilename(platform)}`;
    const res = await fetch(appcastUrl, {
      cache: "no-store",
      signal: AbortSignal.timeout(APPCAST_FETCH_TIMEOUT_MS),
    });

    if (!res.ok) return null;

    const xml = await res.text();

    let match: RegExpMatchArray | null;

    if (platform === "windows") {
      // For Windows, use explicit arch if provided, otherwise detect from User-Agent
      const arch = explicitArch || detectWindowsArch(userAgent);
      const osValue = `windows-${arch}`;
      // Match item with sparkle:os matching the architecture
      const itemRegex = new RegExp(
        `<item>[\\s\\S]*?<sparkle:os>${osValue}</sparkle:os>[\\s\\S]*?<enclosure[^>]*url="([^"]+)"`,
        "i",
      );

      match = xml.match(itemRegex);
    } else {
      // For macOS, just get the first item
      match = xml.match(/<item>[\s\S]*?<enclosure[^>]*url="([^"]+)"/i);
    }

    if (!match || !match[1]) return null;

    const latestUrl = new URL(match[1]);

    // Serve via CDN for better performance
    latestUrl.hostname = "builds-cdn.hyperwhisper.com";

    return latestUrl.toString();
  } catch (error) {
    console.error("Error parsing appcast:", error);

    return null;
  }
}

function normalizePlatform(input: string | null): Platform {
  return input === "windows" ? "windows" : "mac";
}

/**
 * GET /api/download
 *
 * Derives the latest download URL from appcast.xml, swaps the
 * hostname to the CDN and issues a redirect.
 *
 * Query params:
 * - platform: "mac" (default) or "windows"
 * - arch: "x64" or "arm64" (optional, for Windows only - overrides User-Agent detection)
 */
export async function GET(request: Request) {
  try {
    const url = new URL(request.url);
    const { origin, searchParams } = url;
    const platform = normalizePlatform(searchParams.get("platform"));
    const userAgent = request.headers.get("user-agent") || "";
    const archParam = parseWindowsArch(searchParams.get("arch"));
    const downloadUrl = await getLatestDownloadUrl(
      origin,
      platform,
      userAgent,
      archParam,
    );

    if (!downloadUrl) {
      return NextResponse.json(
        { error: "Failed to get latest download URL" },
        { status: 500 },
      );
    }

    // Redirect to the latest URL
    return NextResponse.redirect(downloadUrl);
  } catch (error) {
    console.error("Error generating download redirect:", error);

    return NextResponse.json(
      { error: "Internal server error" },
      { status: 500 },
    );
  }
}
