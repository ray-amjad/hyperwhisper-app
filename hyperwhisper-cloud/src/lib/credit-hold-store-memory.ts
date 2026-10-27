// CREDIT HOLDS — IN-MEMORY TWIN OF THE UPSTASH STORE (test support)
//
// The route suites mock `../lib/redis` and run no Redis, so they install this
// store through `setCreditHoldStoreForTests`. It implements the same contract
// as `RESERVE_HOLD_SCRIPT` line for line, including which cache entries it caps
// the balance by (the asking key's and the pool's, through `holdKeys`), and
// each call completes without an `await` in between its read and its write,
// which is the atomicity the Lua script gets from Redis. `credit-hold-store.test.ts` runs the real script
// against a local redis-server to keep the two honest.

import { holdKeys, type CreditHoldStore, type ReserveHoldRequest, type ReserveHoldResult } from './credit-hold-store';

interface MemoryHold {
  milli: number;
  expiresAtMs: number;
}

export interface MemoryCreditHoldStore extends CreditHoldStore {
  /** Live holds on one account, by id, in milli-credits. */
  holds(account: string): Map<string, number>;
  /** Sum of live holds on one account, in milli-credits. */
  outstandingMilli(account: string): number;
  readonly calls: Array<{ op: 'reserve' | 'release'; account: string; holdId: string }>;
}

export function createMemoryCreditHoldStore(
  options: {
    /**
     * The cached balance, in credits, stored under one Redis key, as the script
     * reads it: `license:<key>` and `credit_pool_balance:<pool>` (see
     * `holdKeys`). Undefined is an absent or unparseable entry.
     */
    cachedCredits?: (redisKey: string) => number | undefined;
  } = {},
): MemoryCreditHoldStore {
  const accounts = new Map<string, Map<string, MemoryHold>>();
  const calls: MemoryCreditHoldStore['calls'] = [];

  function holdsOf(account: string): Map<string, MemoryHold> {
    let holds = accounts.get(account);
    if (!holds) {
      holds = new Map();
      accounts.set(account, holds);
    }
    return holds;
  }

  return {
    calls,
    async reserve(request: ReserveHoldRequest): Promise<ReserveHoldResult> {
      calls.push({ op: 'reserve', account: request.account, holdId: request.holdId });
      const holds = holdsOf(request.account);
      for (const [id, hold] of holds) {
        if (hold.expiresAtMs <= request.nowMs) holds.delete(id);
      }

      // The script's KEYS[3..4]: the asking key's cached balance, then the
      // pool's (the key's again on an unpooled hold). The lowest wins.
      let balance = request.balanceMilli;
      const keys = holdKeys(request.account, request.licenseKey);
      for (const key of [keys.license, keys.poolBalance]) {
        const credits = options.cachedCredits?.(key);
        if (credits === undefined || !Number.isFinite(credits)) continue;
        const cached = Math.floor(credits * 1000 + 0.5);
        if (cached < balance) balance = cached;
      }

      let own = 0;
      let others = 0;
      for (const [id, hold] of holds) {
        if (id === request.holdId) own = hold.milli;
        else others += hold.milli;
      }

      let available = Math.max(0, balance - others);
      available -= available % 100;

      const grant = Math.min(request.maxMilli, available);
      if (grant < request.minMilli || grant <= 0) {
        return { granted: false, holdMilli: own, availableMilli: available };
      }
      holds.set(request.holdId, { milli: grant, expiresAtMs: request.nowMs + request.ttlMs });
      return { granted: true, holdMilli: grant, availableMilli: available };
    },
    async release(account: string, holdId: string): Promise<void> {
      calls.push({ op: 'release', account, holdId });
      accounts.get(account)?.delete(holdId);
    },
    holds(account: string): Map<string, number> {
      return new Map([...(accounts.get(account) ?? new Map<string, MemoryHold>())].map(([id, hold]) => [id, hold.milli]));
    },
    outstandingMilli(account: string): number {
      let sum = 0;
      for (const hold of accounts.get(account)?.values() ?? []) sum += hold.milli;
      return sum;
    },
  };
}
