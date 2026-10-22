// The fake socket harness the WebSocket streaming route tests share. Each
// vendor test subclasses `FakeUpstreamSocketBase` for its own frame shape and
// installs the subclass as `globalThis.WebSocket`. Test code only: nothing in
// `src/index.ts` imports this file.

import type { Context } from 'hono';
import type { WSMessageReceive } from 'hono/ws';
import type { AuthContext } from '../middleware/auth';

export interface CloseCall {
  code?: number;
  reason?: string;
}

/** Everything both vendors' fake upstream sockets share. No vendor frame shape. */
export class FakeUpstreamSocketBase {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;

  readyState = 0;
  bufferedAmount = 0;
  readonly sent: Array<string | ArrayBuffer> = [];
  readonly closes: CloseCall[] = [];
  private readonly listeners = new Map<string, Array<(evt: unknown) => void>>();

  constructor(readonly url: string, readonly protocols?: string | string[]) {}

  addEventListener(type: string, handler: (evt: unknown) => void): void {
    const existing = this.listeners.get(type) ?? [];
    existing.push(handler);
    this.listeners.set(type, existing);
  }

  send(data: string | ArrayBuffer): void {
    this.sent.push(data);
  }

  close(code?: number, reason?: string): void {
    this.readyState = FakeUpstreamSocketBase.CLOSED;
    this.closes.push({ code, reason });
  }

  emit(type: string, evt: unknown): void {
    for (const handler of this.listeners.get(type) ?? []) handler(evt);
  }

  /** Simulate the vendor accepting the WebSocket handshake. */
  handshake(): void {
    this.readyState = FakeUpstreamSocketBase.OPEN;
    this.emit('open', {});
  }

  /** Simulate one upstream frame arriving; an object is sent as JSON. */
  deliver(payload: unknown): void {
    this.emit('message', { data: typeof payload === 'string' ? payload : JSON.stringify(payload) });
  }
}

export class FakeClientSocket {
  readyState = 1;
  readonly sent: string[] = [];
  readonly closes: CloseCall[] = [];

  send(data: string | ArrayBuffer | Uint8Array): void {
    this.sent.push(typeof data === 'string' ? data : '<binary>');
  }

  close(code?: number, reason?: string): void {
    this.readyState = 3;
    this.closes.push({ code, reason });
  }

  messages(): Array<Record<string, unknown>> {
    return this.sent.map((raw) => JSON.parse(raw) as Record<string, unknown>);
  }

  messagesOfType(type: string): Array<Record<string, unknown>> {
    return this.messages().filter((message) => message.type === type);
  }
}

/** `extraVars` adds context variables a route reads beyond auth and IP (e.g. `wsCreditHold`). */
export function fakeContext(auth: AuthContext, url: string, extraVars: Record<string, unknown> = {}): Context {
  const vars: Record<string, unknown> = { wsAuth: auth, wsClientIP: '203.0.113.7', ...extraVars };
  return {
    get: (key: string) => vars[key],
    req: { url },
  } as unknown as Context;
}

export function binaryMessage(data: ArrayBuffer): MessageEvent<WSMessageReceive> {
  return { data } as MessageEvent<WSMessageReceive>;
}

export function textMessage(data: string): MessageEvent<WSMessageReceive> {
  return { data } as MessageEvent<WSMessageReceive>;
}

interface FakeTimer {
  fn: () => void;
  ms: number;
  cleared: boolean;
}

/**
 * A recorded clock for `createStreamingEventsFor`'s `timers` seam. Nothing
 * fires on its own: a test advances `nowMs` and fires a timer by hand.
 */
export class FakeStreamingTimers {
  nowMs = 1_000_000;
  readonly intervals: FakeTimer[] = [];
  readonly timeouts: FakeTimer[] = [];

  readonly timers = {
    now: () => this.nowMs,
    setInterval: (fn: () => void, ms: number) => this.record(this.intervals, fn, ms),
    clearInterval: (handle: ReturnType<typeof setInterval>) => this.clear(handle),
    setTimeout: (fn: () => void, ms: number) => this.record(this.timeouts, fn, ms),
    clearTimeout: (handle: ReturnType<typeof setTimeout>) => this.clear(handle),
  };

  private record(list: FakeTimer[], fn: () => void, ms: number): ReturnType<typeof setInterval> {
    const timer = { fn, ms, cleared: false };
    list.push(timer);
    return timer as unknown as ReturnType<typeof setInterval>;
  }

  private clear(handle: unknown): void {
    (handle as FakeTimer).cleared = true;
  }

  /** The one interval the session armed (the ping), fired once unless cleared. */
  tickInterval(): void {
    expectOne(this.intervals, 'interval').fireIfLive();
  }

  /** The one timeout the session armed (the open deadline), fired unless cleared. */
  fireTimeout(): void {
    expectOne(this.timeouts, 'timeout').fireIfLive();
  }
}

function expectOne(list: FakeTimer[], kind: string): { fireIfLive: () => void } {
  if (list.length !== 1) throw new Error(`expected exactly one ${kind}, got ${list.length}`);
  const timer = list[0]!;
  return { fireIfLive: () => { if (!timer.cleared) timer.fn(); } };
}
