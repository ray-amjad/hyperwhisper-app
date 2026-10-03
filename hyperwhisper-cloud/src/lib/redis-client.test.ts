// UPSTASH REDIS CLIENT FACTORY
//
// `./redis` is replaced process-wide by `mock.module` in a dozen suites, so
// this suite imports `./redis-client` directly. Nothing mocks it. Every test but
// the last passes a recording `makeClient`; the last builds a real, idle client.

import { describe, expect, test } from 'bun:test';
import { Redis } from '@upstash/redis';
import { createRedisGetter, type RedisClientFactory } from './redis-client';

const MISSING_ENV = 'UPSTASH_REDIS_CLOUD_URL and UPSTASH_REDIS_CLOUD_TOKEN are required';

function recordingFactory() {
  const calls: Array<{ url: string; token: string }> = [];
  const makeClient: RedisClientFactory = (opts) => {
    calls.push(opts);
    return { builtFrom: opts } as unknown as Redis;
  };
  return { calls, makeClient };
}

describe('createRedisGetter', () => {
  test('throws the exact message and builds nothing when the URL or the token is missing', () => {
    const envs: Array<Record<string, string | undefined>> = [
      { UPSTASH_REDIS_CLOUD_TOKEN: 'cloud-test-value' },
      { UPSTASH_REDIS_CLOUD_URL: 'https://cloud.example' },
      { UPSTASH_REDIS_CLOUD_URL: '', UPSTASH_REDIS_CLOUD_TOKEN: 'cloud-test-value' },
    ];
    for (const env of envs) {
      const { calls, makeClient } = recordingFactory();
      const get = createRedisGetter(env, makeClient);
      expect(() => get()).toThrow(new Error(MISSING_ENV));
      expect(calls).toHaveLength(0);
    }
  });

  test('builds the client once and returns the same object on every call', () => {
    const { calls, makeClient } = recordingFactory();
    const get = createRedisGetter(
      { UPSTASH_REDIS_CLOUD_URL: 'https://cloud.example', UPSTASH_REDIS_CLOUD_TOKEN: 'cloud-test-value' },
      makeClient
    );
    const first = get();
    for (let i = 0; i < 4; i++) expect(get()).toBe(first);
    expect(calls).toHaveLength(1);
  });

  test('reads the CLOUD pair, not the website SITE pair, when both are set', () => {
    const { calls, makeClient } = recordingFactory();
    const get = createRedisGetter(
      {
        UPSTASH_REDIS_SITE_URL: 'https://site.example',
        UPSTASH_REDIS_SITE_TOKEN: 'site-test-value',
        UPSTASH_REDIS_CLOUD_URL: 'https://cloud.example',
        UPSTASH_REDIS_CLOUD_TOKEN: 'cloud-test-value',
      },
      makeClient
    );
    get();
    expect(calls).toEqual([{ url: 'https://cloud.example', token: 'cloud-test-value' }]);
  });

  test('the default factory builds a real @upstash/redis client from the CLOUD pair', () => {
    // Constructing the client sends nothing; only a command would reach the network.
    const get = createRedisGetter({
      UPSTASH_REDIS_CLOUD_URL: 'https://cloud.example',
      UPSTASH_REDIS_CLOUD_TOKEN: 'cloud-test-value',
    });
    expect(get()).toBeInstanceOf(Redis);
  });
});
