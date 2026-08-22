// Tests for the caller-identity helpers in request-id.ts.
//
// `getClientIP` is an abuse control, not a logging convenience: every route
// (`transcribe`, `post-process`, `assistant`, `usage`, `ws-streaming-deepgram`)
// hands its result to the per-IP daily quota and the `ip_blocked` list. Both
// stop being enforceable the moment a caller can choose its own IP, so the rule
// this file protects is:
//
//   Fly-Client-IP / X-Forwarded-For are honoured ONLY when the request came
//   through Fly's public edge proxy. A caller that reaches the Machine directly
//   over 6PN (private IPv6, `fdaa::/16`) writes its own headers, so its headers
//   are refused and the IP resolves to 'unknown'.
//
// The trust signal is the TCP peer address, which the kernel reports and the
// caller cannot forge. `Fly-Request-Id` deliberately does NOT get the same
// treatment — it is a log correlation id, never a trust input — so the two
// functions must disagree on a 6PN request, and a test here holds them apart.
//
// Nothing is mocked. The peer address is supplied the same way Bun supplies it
// in production: as the `env` argument to `app.fetch`, which hono/bun's
// getConnInfo reads `server.requestIP(request)` from.

import { describe, expect, test } from 'bun:test';
import { Hono } from 'hono';
import { generateRequestId, getClientIP, getFlyRequestId } from './request-id';

type PeerInfo = { address?: string; family?: string; port?: number } | null;

const app = new Hono();
app.get('/probe', (c) =>
  c.json({
    clientIP: getClientIP(c),
    flyRequestId: getFlyRequestId(c) ?? null,
  })
);

/** The 2nd `app.fetch` argument, shaped as Bun.serve passes it. */
function edgeEnv(peer: PeerInfo) {
  return { server: { requestIP: () => peer } };
}

async function probe(
  headers: Record<string, string> = {},
  env?: unknown
): Promise<{ clientIP: string; flyRequestId: string | null }> {
  const request = new Request('https://transcribe.example/probe', { headers });
  const response = await app.fetch(request, env as never);
  return (await response.json()) as { clientIP: string; flyRequestId: string | null };
}

// A public edge request: the peer is Fly's proxy, so the headers are trusted.
const EDGE_PEER = edgeEnv({ address: '2a09:8280:1::1:abcd', family: 'IPv6', port: 443 });
// A 6PN request: another Machine or a colocated process, reaching us off-edge.
const SIX_PN_PEER = edgeEnv({ address: 'fdaa:0:1234:a7b:1::2', family: 'IPv6', port: 9001 });

describe('getClientIP on an edge request', () => {
  test('uses Fly-Client-IP, which the edge proxy sets and cannot be client-supplied', async () => {
    const result = await probe({ 'Fly-Client-IP': '203.0.113.7' }, EDGE_PEER);
    expect(result.clientIP).toBe('203.0.113.7');
  });

  test('prefers Fly-Client-IP over X-Forwarded-For when both are present', async () => {
    const result = await probe(
      { 'Fly-Client-IP': '203.0.113.7', 'X-Forwarded-For': '198.51.100.9' },
      EDGE_PEER
    );
    expect(result.clientIP).toBe('203.0.113.7');
  });

  test('falls back to the first X-Forwarded-For entry, which is the original caller', async () => {
    const result = await probe(
      { 'X-Forwarded-For': '198.51.100.9, 203.0.113.1, 203.0.113.2' },
      EDGE_PEER
    );
    expect(result.clientIP).toBe('198.51.100.9');
  });

  test('trims the X-Forwarded-For entry so one IP cannot occupy two quota buckets', async () => {
    const padded = await probe({ 'X-Forwarded-For': '  198.51.100.9 , 203.0.113.1' }, EDGE_PEER);
    const bare = await probe({ 'X-Forwarded-For': '198.51.100.9' }, EDGE_PEER);
    expect(padded.clientIP).toBe('198.51.100.9');
    expect(padded.clientIP).toBe(bare.clientIP);
  });

  test('an empty Fly-Client-IP falls through to X-Forwarded-For instead of becoming an empty key', async () => {
    const result = await probe(
      { 'Fly-Client-IP': '', 'X-Forwarded-For': '198.51.100.9' },
      EDGE_PEER
    );
    expect(result.clientIP).toBe('198.51.100.9');
  });

  test("resolves to 'unknown' when neither header is present", async () => {
    const result = await probe({}, EDGE_PEER);
    expect(result.clientIP).toBe('unknown');
  });

  test("resolves to 'unknown' when X-Forwarded-For's first entry is blank", async () => {
    const result = await probe({ 'X-Forwarded-For': '   , 198.51.100.9' }, EDGE_PEER);
    expect(result.clientIP).toBe('unknown');
  });
});

describe('getClientIP on an off-edge 6PN request', () => {
  test("refuses a forged Fly-Client-IP from a 6PN peer and resolves to 'unknown'", async () => {
    const result = await probe({ 'Fly-Client-IP': '203.0.113.7' }, SIX_PN_PEER);
    expect(result.clientIP).toBe('unknown');
  });

  test("refuses a forged X-Forwarded-For from a 6PN peer and resolves to 'unknown'", async () => {
    const result = await probe({ 'X-Forwarded-For': '198.51.100.9' }, SIX_PN_PEER);
    expect(result.clientIP).toBe('unknown');
  });

  test('matches the fdaa::/16 prefix case-insensitively, so an uppercased peer is still off-edge', async () => {
    const result = await probe(
      { 'Fly-Client-IP': '203.0.113.7' },
      edgeEnv({ address: 'FDAA:0:1234:A7B:1::2', family: 'IPv6', port: 9001 })
    );
    expect(result.clientIP).toBe('unknown');
  });

  test('does not treat a public IPv6 peer as 6PN just because it is IPv6', async () => {
    const result = await probe(
      { 'Fly-Client-IP': '203.0.113.7' },
      edgeEnv({ address: '2a09:8280:1::1:abcd', family: 'IPv6', port: 443 })
    );
    expect(result.clientIP).toBe('203.0.113.7');
  });

  test('does not treat an address that merely contains fdaa as 6PN', async () => {
    // The prefix is anchored at the start; `2a09:...:fdaa::1` is a public address.
    const result = await probe(
      { 'Fly-Client-IP': '203.0.113.7' },
      edgeEnv({ address: '2a09:8280:fdaa::1', family: 'IPv6', port: 443 })
    );
    expect(result.clientIP).toBe('203.0.113.7');
  });
});

describe('getClientIP when the peer address is unavailable', () => {
  // Failing closed here would resolve every request to 'unknown' and collapse
  // the whole service into one shared quota bucket, so the documented choice is
  // to fall back to the headers. These pin that choice.

  test('honours the headers when the runtime reports no connection info', async () => {
    const result = await probe({ 'Fly-Client-IP': '203.0.113.7' }, edgeEnv(null));
    expect(result.clientIP).toBe('203.0.113.7');
  });

  test('honours the headers when the peer entry carries no address', async () => {
    const result = await probe({ 'Fly-Client-IP': '203.0.113.7' }, edgeEnv({ family: 'IPv6' }));
    expect(result.clientIP).toBe('203.0.113.7');
  });

  test('honours the headers when there is no Bun server at all, rather than throwing', async () => {
    // This is the `app.request()` / non-Bun-runtime path: getConnInfo throws and
    // the helper has to swallow it. A throw here would 500 every route.
    const result = await probe({ 'Fly-Client-IP': '203.0.113.7' });
    expect(result.clientIP).toBe('203.0.113.7');
  });

  test("still resolves to 'unknown' with no headers and no connection info", async () => {
    const result = await probe({});
    expect(result.clientIP).toBe('unknown');
  });
});

describe('getFlyRequestId', () => {
  test('reads the header the Fly edge stamps', async () => {
    const result = await probe({ 'Fly-Request-Id': '01JABCDE-lhr' }, EDGE_PEER);
    expect(result.flyRequestId).toBe('01JABCDE-lhr');
  });

  test('reads it whatever case the sender used', async () => {
    const lower = await probe({ 'fly-request-id': '01JABCDE-lhr' }, EDGE_PEER);
    const upper = await probe({ 'FLY-REQUEST-ID': '01JABCDE-lhr' }, EDGE_PEER);
    expect(lower.flyRequestId).toBe('01JABCDE-lhr');
    expect(upper.flyRequestId).toBe('01JABCDE-lhr');
  });

  test('reports absent rather than empty when the header is missing', async () => {
    const result = await probe({}, EDGE_PEER);
    expect(result.flyRequestId).toBeNull();
  });

  test('reports absent rather than empty when the header is present but blank', async () => {
    // An empty-string correlation id in a log line reads as a real id.
    const result = await probe({ 'Fly-Request-Id': '' }, EDGE_PEER);
    expect(result.flyRequestId).toBeNull();
  });

  test('is a log label, not a trust signal: a 6PN peer can still set it', async () => {
    // The same request has its IP refused. The two must not converge — folding
    // the peer check into getFlyRequestId would drop the correlation id from
    // exactly the logs where it is most wanted.
    const result = await probe(
      { 'Fly-Request-Id': 'forged-by-the-caller', 'Fly-Client-IP': '203.0.113.7' },
      SIX_PN_PEER
    );
    expect(result.flyRequestId).toBe('forged-by-the-caller');
    expect(result.clientIP).toBe('unknown');
  });
});

describe('generateRequestId', () => {
  test('issues a distinct id per call, since it is the join key across a request log', async () => {
    const ids = new Set(Array.from({ length: 100 }, () => generateRequestId()));
    expect(ids.size).toBe(100);
  });
});
