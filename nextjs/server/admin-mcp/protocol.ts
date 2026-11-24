/**
 * A minimal, stateless MCP server over Streamable HTTP, JSON replies only.
 *
 * Hand-rolled rather than `@modelcontextprotocol/sdk`: the SDK's HTTP
 * transports are built for sessions and SSE and pull in a stack of
 * dependencies this Worker bundle does not need, while the stateless,
 * JSON-only subset used here (initialize, ping, notifications, tools/list,
 * tools/call) is a few dozen lines of JSON-RPC 2.0.
 *
 * Per the Streamable HTTP transport: a POST carrying only notifications or
 * responses is answered 202 with no body; a POST carrying requests is answered
 * 200 with `application/json` (one response, or an array for a batch).
 */
import { ADMIN_MCP_TOOLS, callAdminTool } from "./tools";

/** Newest first; the first entry is offered when the client asks for another. */
export const SUPPORTED_PROTOCOL_VERSIONS = [
  "2025-11-25",
  "2025-06-18",
  "2025-03-26",
  "2024-11-05",
] as const;

export const SERVER_INFO = { name: "hyperwhisper-admin", version: "1.0.0" };

const INSTRUCTIONS =
  "Admin tools for HyperWhisper Cloud credits. Look a customer up with find_customer first, then add credits to one of their granted license keys with add_credits (up to 30,000 credits = $30) or add_credits_large (more, needs human approval). 1,000 credits = $1. Always pass a fresh idempotencyKey per intended grant and reuse it only to retry that same grant.";

export const JSON_RPC = {
  PARSE_ERROR: -32700,
  INVALID_REQUEST: -32600,
  METHOD_NOT_FOUND: -32601,
  INVALID_PARAMS: -32602,
  INTERNAL_ERROR: -32603,
} as const;

type JsonRpcId = string | number | null;

export interface JsonRpcResponse {
  jsonrpc: "2.0";
  id: JsonRpcId;
  result?: unknown;
  error?: { code: number; message: string };
}

export function rpcError(
  id: JsonRpcId,
  code: number,
  message: string,
): JsonRpcResponse {
  return { jsonrpc: "2.0", id, error: { code, message } };
}

function rpcResult(id: JsonRpcId, result: unknown): JsonRpcResponse {
  return { jsonrpc: "2.0", id, result };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isValidId(value: unknown): value is string | number {
  return (
    typeof value === "string" ||
    (typeof value === "number" && Number.isFinite(value))
  );
}

/**
 * Handles one JSON-RPC message. Returns the response to send, or null when the
 * message needs none (a notification, or a client's response to us).
 */
export async function handleMessage(
  message: unknown,
): Promise<JsonRpcResponse | null> {
  if (!isObject(message) || message.jsonrpc !== "2.0") {
    return rpcError(
      null,
      JSON_RPC.INVALID_REQUEST,
      "Invalid JSON-RPC 2.0 message",
    );
  }

  const hasId = "id" in message;
  const method = message.method;

  // A response from the client (we never send requests, so just accept it).
  if (
    method === undefined &&
    hasId &&
    ("result" in message || "error" in message)
  ) {
    return null;
  }

  if (typeof method !== "string") {
    return rpcError(
      hasId && isValidId(message.id) ? message.id : null,
      JSON_RPC.INVALID_REQUEST,
      "Missing method",
    );
  }

  // A notification (no id): nothing to answer. notifications/initialized,
  // notifications/cancelled, … all land here.
  if (!hasId) return null;

  if (!isValidId(message.id)) {
    return rpcError(
      null,
      JSON_RPC.INVALID_REQUEST,
      "id must be a string or a number",
    );
  }
  const id = message.id;
  const params = message.params;

  if (params !== undefined && !isObject(params)) {
    return rpcError(id, JSON_RPC.INVALID_PARAMS, "params must be an object");
  }

  switch (method) {
    case "initialize": {
      const requested = params?.protocolVersion;
      const protocolVersion =
        typeof requested === "string" &&
        (SUPPORTED_PROTOCOL_VERSIONS as readonly string[]).includes(requested)
          ? requested
          : SUPPORTED_PROTOCOL_VERSIONS[0];

      return rpcResult(id, {
        protocolVersion,
        capabilities: { tools: { listChanged: false } },
        serverInfo: SERVER_INFO,
        instructions: INSTRUCTIONS,
      });
    }
    case "ping":
      return rpcResult(id, {});
    case "tools/list":
      return rpcResult(id, { tools: ADMIN_MCP_TOOLS });
    case "tools/call": {
      const name = params?.name;

      if (typeof name !== "string") {
        return rpcError(
          id,
          JSON_RPC.INVALID_PARAMS,
          "tools/call needs a string `name`",
        );
      }
      try {
        const result = await callAdminTool(name, params?.arguments);

        if (result === null) {
          return rpcError(id, JSON_RPC.INVALID_PARAMS, `Unknown tool: ${name}`);
        }

        return rpcResult(id, result);
      } catch (error) {
        // A tool threw past its own handling (a database read in
        // find_customer, say). Report it as a tool error the agent can see,
        // without the error's text (a driver error can embed query params).
        console.error(
          JSON.stringify({
            event: "admin.mcp.tool_error",
            tool: name,
            errorName: error instanceof Error ? error.name : typeof error,
          }),
        );

        return rpcResult(id, {
          content: [
            { type: "text", text: `Internal error while running ${name}` },
          ],
          isError: true,
        });
      }
    }
    default:
      return rpcError(
        id,
        JSON_RPC.METHOD_NOT_FOUND,
        `Method not found: ${method}`,
      );
  }
}
