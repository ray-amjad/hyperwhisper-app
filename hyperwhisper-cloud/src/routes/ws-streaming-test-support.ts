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

export function fakeContext(auth: AuthContext, url: string): Context {
  const vars: Record<string, unknown> = { wsAuth: auth, wsClientIP: '203.0.113.7' };
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
