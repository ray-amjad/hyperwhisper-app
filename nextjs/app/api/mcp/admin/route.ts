import { NextRequest, NextResponse } from "next/server";

import { env } from "@/src/env/server.mjs";
import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import { readBoundedText } from "@/src/lib/bounded-body";
import {
  handleMessage,
  JSON_RPC,
  rpcError,
  type JsonRpcResponse,
} from "@/server/admin-mcp/protocol";

/**
 * Admin MCP endpoint (stateless MCP over Streamable HTTP, JSON replies only).
 *
 * The door an agent gateway uses to look customers up and add HyperWhisper
 * Cloud credits (see `server/admin-mcp/tools.ts`). Every request, of every
 * method, needs `Authorization: Bearer <HYPERWHISPER_ADMIN_MCP_TOKEN>`,
 * compared timing-safe. With the variable unset or empty, everything is 401.
 */

// A JSON-RPC message here is a few hundred bytes; a batch a few KB.
const MAX_BODY_BYTES = 64 * 1024;

const NO_STORE = { "Cache-Control": "no-store" };

function unauthorized(): NextResponse {
  return NextResponse.json(
    { error: "Unauthorized" },
    {
      status: 401,
      headers: {
        ...NO_STORE,
        "WWW-Authenticate": 'Bearer realm="hyperwhisper-admin-mcp"',
      },
    },
  );
}

function isAuthorized(request: NextRequest): boolean {
  const header = request.headers.get("authorization") ?? "";
  const match = /^Bearer\s+(.+)$/i.exec(header);
  const token = match?.[1]?.trim();

  return timingSafeEqualSecret(token, env.HYPERWHISPER_ADMIN_MCP_TOKEN);
}

function methodNotAllowed(request: NextRequest): NextResponse {
  if (!isAuthorized(request)) return unauthorized();

  // No SSE stream (GET) and no sessions to end (DELETE): POST only.
  return new NextResponse(null, {
    status: 405,
    headers: { ...NO_STORE, Allow: "POST" },
  });
}

export const GET = methodNotAllowed;
export const DELETE = methodNotAllowed;
export const PUT = methodNotAllowed;
export const PATCH = methodNotAllowed;

function json(
  body: JsonRpcResponse | JsonRpcResponse[],
  status = 200,
): NextResponse {
  return NextResponse.json(body, { status, headers: NO_STORE });
}

export async function POST(request: NextRequest): Promise<NextResponse> {
  if (!isAuthorized(request)) return unauthorized();

  const text = await readBoundedText(request, MAX_BODY_BYTES);

  if (typeof text !== "string") return text;

  let payload: unknown;

  try {
    payload = JSON.parse(text);
  } catch {
    return json(rpcError(null, JSON_RPC.PARSE_ERROR, "Parse error"), 400);
  }

  if (Array.isArray(payload)) {
    if (payload.length === 0) {
      return json(rpcError(null, JSON_RPC.INVALID_REQUEST, "Empty batch"), 400);
    }
    // Sequential, so two grants in one batch never race each other.
    const responses: JsonRpcResponse[] = [];

    for (const message of payload) {
      const response = await handleMessage(message);

      if (response) responses.push(response);
    }
    if (responses.length === 0) return accepted();

    return json(responses);
  }

  const response = await handleMessage(payload);

  if (!response) return accepted();

  return json(response);
}

/** Notifications and client responses only: 202, no body. */
function accepted(): NextResponse {
  return new NextResponse(null, { status: 202, headers: NO_STORE });
}
