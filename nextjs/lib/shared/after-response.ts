import { after } from "next/server";

/**
 * Run work after the response is sent: Next's `after()` (on Cloudflare Workers
 * OpenNext keeps the invocation alive with `ctx.waitUntil` until the task
 * settles, up to about 30 s after the response).
 *
 * Only inside a request (a Route Handler, which every tRPC call runs in, or a
 * Server Function). The task must not throw: nobody is left to answer.
 *
 * Its own module so a test that runs a route outside Next's request scope can
 * replace it and await the queued work.
 */
export function afterResponse(task: () => Promise<void>): void {
  after(task);
}
