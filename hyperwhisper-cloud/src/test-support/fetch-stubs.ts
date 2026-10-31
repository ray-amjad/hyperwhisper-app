/**
 * The shared `globalThis.fetch` stubs for the cloud tests that stand in for an
 * upstream HTTP call. Each helper REPLACES `globalThis.fetch`; it never puts
 * the real one back, so every caller keeps its own `originalFetch` save and
 * `afterEach` restore. Test code only: nothing in `src/index.ts` imports this
 * module.
 */

import { mock } from 'bun:test';

/** What `captureMultipartRequest` saw on the last upstream call. */
export interface CapturedMultipartRequest {
  url?: string;
  headers?: HeadersInit;
  form?: FormData;
}

/**
 * Answer every fetch with `body` and expose the multipart request it was sent.
 * A string body is sent as-is (status 200 unless `init` says otherwise); any
 * other body goes through `Response.json`. `init` adds response status or
 * headers, e.g. a JSON content type on a raw string body.
 */
export function captureMultipartRequest(body: unknown, init?: ResponseInit): CapturedMultipartRequest {
  const captured: CapturedMultipartRequest = {};
  globalThis.fetch = mock(async (url: string, requestInit: RequestInit) => {
    captured.url = url;
    captured.headers = requestInit.headers;
    captured.form = requestInit.body as FormData;
    return typeof body === 'string'
      ? new Response(body, { status: 200, ...init })
      : Response.json(body, init);
  }) as unknown as typeof fetch;
  return captured;
}

/**
 * Answer every fetch with a plain-text `text` body and `status`. Returns the
 * probe that says whether fetch was called at all, for the gates that must
 * fail before any upstream call.
 */
export function stubErrorResponse(status: number, text = 'upstream said no'): () => boolean {
  let called = false;
  globalThis.fetch = mock(async () => {
    called = true;
    return new Response(text, { status });
  }) as unknown as typeof fetch;
  return () => called;
}

/**
 * Answer fetch with `handler` and record the last call's URL and RequestInit.
 * `params` reads the URL's query string; `formData` reads the request body as
 * the multipart form it was sent as.
 */
export function mockFetchOnce(handler: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  let capturedUrl = '';
  let capturedInit: RequestInit | undefined;
  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input);
    capturedInit = init;
    return handler(capturedUrl, init);
  }) as unknown as typeof fetch;
  return {
    url: () => capturedUrl,
    init: () => capturedInit,
    params: () => new URL(capturedUrl).searchParams,
    formData: () => capturedInit!.body as FormData,
  };
}

/** A fetch that throws `message` if it is ever called. Assign it to `globalThis.fetch`. */
export function neverFetch(message: string): typeof fetch {
  return mock(async () => {
    throw new Error(message);
  }) as unknown as typeof fetch;
}
