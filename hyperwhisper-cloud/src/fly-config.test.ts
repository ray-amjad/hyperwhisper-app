// The Fly app configs carry the limits #2011 set: a kill_timeout the shutdown
// drain fits inside, a per-machine concurrency limit, and a /health check.
// fly.dev.toml is a manual playground and is not pinned here.

import { describe, expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

function readConfig(name: string): Record<string, any> {
  return Bun.TOML.parse(readFileSync(join(import.meta.dir, '..', name), 'utf8')) as Record<string, any>;
}

for (const name of ['fly.prod.toml', 'fly.staging.toml']) {
  describe(name, () => {
    const config = readConfig(name);

    // index.test.ts pins SHUTDOWN_DRAIN_MS under this value.
    test('kill_timeout is 120 s', () => {
      expect(config.kill_timeout).toBe('120s');
    });

    test('caps in-flight requests per machine at soft 20 / hard 25', () => {
      expect(config.http_service.concurrency).toEqual({
        type: 'requests',
        soft_limit: 20,
        hard_limit: 25,
      });
    });

    test('checks /health every 15 s with a 5 s timeout', () => {
      const checks = config.http_service.checks;
      expect(checks).toHaveLength(1);
      expect(checks[0]).toMatchObject({ method: 'GET', path: '/health', interval: '15s', timeout: '5s' });
    });

    test('keeps the 1 GB machines', () => {
      expect(config.vm[0].memory).toBe('1gb');
    });
  });
}
