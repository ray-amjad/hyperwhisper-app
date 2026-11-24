// BACKGROUND WORK AND LIVE SESSIONS — WHAT A SHUTDOWN WAITS FOR (#2031)
//
// The registry `graceful-shutdown.ts` drains. The modules that make the work
// register into it, so the shutdown imports none of them:
//   - fire-and-forget writes made after the response is flushed (credit
//     deductions, credit-hold releases, latency reports): each module makes one
//     `backgroundWork(kind)` tracker at load and `track`s every promise;
//   - live streaming sessions, billed only when they end: each one registers
//     its shutdown handler with `registerLiveSession` once it has opened.

type Tracked = Set<Promise<unknown>>;

const trackers = new Map<string, Tracked>();

export interface BackgroundWork {
  /** Register a promise the shutdown must wait for. Returns it unchanged. */
  track<T>(work: Promise<T>): Promise<T>;
  /** Wait for what is pending now, up to `timeoutMs`; see {@link drainTracked}. */
  drain(timeoutMs: number): Promise<number>;
}

/**
 * Resolve when every promise pending when it is called has settled, or after
 * `timeoutMs`, whichever comes first. Resolves to how many were pending; none
 * resolves at once.
 */
async function drainTracked(pending: Tracked, timeoutMs: number): Promise<number> {
  const pendingCount = pending.size;
  if (pendingCount === 0) return 0;

  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<void>((resolve) => { timer = setTimeout(resolve, timeoutMs); });
  await Promise.race([Promise.allSettled([...pending]), timeout]);
  clearTimeout(timer);
  return pendingCount;
}

/** The tracker for one kind of background work; one per kind, made at module load. */
export function backgroundWork(kind: string): BackgroundWork {
  let pending = trackers.get(kind);
  if (!pending) {
    pending = new Set();
    trackers.set(kind, pending);
  }
  const set = pending;
  return {
    track(work) {
      set.add(work);
      work
        .catch(() => {}) // the owner logs its own failures
        .finally(() => set.delete(work));
      return work;
    },
    drain: (timeoutMs) => drainTracked(set, timeoutMs),
  };
}

/**
 * Drain every kind together, each up to `timeoutMs` (the grace period is one
 * budget, not one per kind). Resolves to how many were pending, per kind.
 */
export async function drainBackgroundWork(timeoutMs: number): Promise<Record<string, number>> {
  const kinds = [...trackers.keys()];
  const counts = await Promise.all(kinds.map((kind) => drainTracked(trackers.get(kind)!, timeoutMs)));
  return Object.fromEntries(kinds.map((kind, i) => [kind, counts[i]]));
}

// Every live session past its API-key check, keyed by its shutdown handler.
// Bun runs no socket `close` handler on `process.exit`, so the shutdown ends
// them itself (#1235). An entry removes itself when its session ends.
const liveSessions = new Set<() => Promise<void>>();
/** Set once by `endLiveSessions`; no live session opens after it. */
let liveSessionsRefused = false;

/** Register an open live session's shutdown handler. Returns its unregister. */
export function registerLiveSession(shutdown: () => Promise<void>): () => void {
  liveSessions.add(shutdown);
  return () => { liveSessions.delete(shutdown); };
}

/** True once a shutdown has begun ending live sessions: refuse new ones. */
export function liveSessionsClosed(): boolean {
  return liveSessionsRefused;
}

/**
 * End every open live session for a machine shutdown, by running each one's
 * shutdown handler. Call it BEFORE the background-work drain, so the drain
 * awaits these sessions' charges. Resolves to the number of handlers that
 * resolved; a handler that rejects is not counted (it logs its own failure).
 */
export async function endLiveSessions(): Promise<number> {
  // Latch first: the machine keeps accepting upgrades during the drain, and a
  // preflight already awaiting auth can open after this snapshot.
  liveSessionsRefused = true;
  const sessions = [...liveSessions];
  // Settled, not `Promise.all`: one session's fault must not reject this and
  // make the shutdown skip the drains and the exit.
  const results = await Promise.allSettled(sessions.map((shutdown) => shutdown()));
  return results.filter((result) => result.status === 'fulfilled').length;
}

/** Test-only: bun runs every test file in one process, and the latch is module state. */
export function resetLiveSessionsForTests(): void {
  liveSessionsRefused = false;
}
