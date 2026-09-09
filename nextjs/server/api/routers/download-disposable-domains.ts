const RELOAD_INTERVAL_MS = 1000 * 60 * 60; // 1 hour

export type DisposableDomainCacheDeps = {
  now: () => number;
};

/**
 * Build an isolated disposable-domain cache. The clock defaults to the real
 * clock, while tests can supply a deterministic one and exercise the exact
 * reload boundary without changing global time.
 */
export function createDisposableDomainCache(
  domains: readonly string[],
  deps: Partial<DisposableDomainCacheDeps> = {},
): () => Promise<Set<string>> {
  const now = deps.now ?? Date.now;
  let cachedDomains: Set<string> | null = null;
  let lastLoadMs = 0;

  return async function getDisposableDomains(): Promise<Set<string>> {
    const currentTimeMs = now();

    if (cachedDomains && currentTimeMs - lastLoadMs < RELOAD_INTERVAL_MS) {
      return cachedDomains;
    }

    const lines = domains
      .map((domain) => domain.trim().toLowerCase())
      .filter(Boolean);

    cachedDomains = new Set(lines);
    lastLoadMs = currentTimeMs;

    return cachedDomains;
  };
}
