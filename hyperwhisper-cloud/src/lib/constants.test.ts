import { describe, expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';

import { CREDITS_PER_MINUTE } from './constants';

// #1657: the cloud, the customer dashboard and the admin Customers page must
// turn credits into minutes at ONE rate. nextjs-ci pins the same pair from its
// side; this copy runs in cloud-ci, so a cloud-only change cannot drift alone.
describe('CREDITS_PER_MINUTE', () => {
  test('equals the rate the nextjs portal pages use', () => {
    const portalSource = readFileSync(
      `${import.meta.dir}/../../../nextjs/lib/credits-per-minute.ts`,
      'utf8',
    );
    const match = portalSource.match(/export const CREDITS_PER_MINUTE = ([0-9.]+);/);
    expect(match).not.toBeNull();
    expect(CREDITS_PER_MINUTE).toBe(Number(match![1]));
  });
});
