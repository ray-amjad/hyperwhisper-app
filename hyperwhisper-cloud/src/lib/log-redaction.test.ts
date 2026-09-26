// The by-value `secrets` pass of `toRedactedLogLine`, directly. The ip and
// licenseKey passes, and passes 1-4 and 6, are pinned through their real
// callers in redis-core.test.ts; the Google-token write catch that feeds
// `secrets` is pinned in google-auth-core.test.ts. These are the parts of the
// new field no caller exercises: more than one secret, and the length gate.
// Pure module, nothing mocks it.

import { describe, expect, test } from 'bun:test';
import { toRedactedLogLine } from './log-redaction';

describe('toRedactedLogLine — by-value secrets', () => {
  test('redacts every secret handed over, wherever it sits', () => {
    const error = new Error('proxy echoed ya29.FIRST-TOKEN and then sk-SECOND-TOKEN twice: sk-SECOND-TOKEN');

    expect(toRedactedLogLine(error, { secrets: ['ya29.FIRST-TOKEN', 'sk-SECOND-TOKEN'] })).toBe(
      'Error: proxy echoed <redacted-secret> and then <redacted-secret> twice: <redacted-secret>',
    );
  });

  test('skips a secret too short to substitute safely, so it cannot rewrite ordinary words', () => {
    // An empty string would put a marker between every character, and a
    // short one rewrites English. Those are left to the grammar passes.
    const error = new Error('ERR the server refused the command');

    expect(toRedactedLogLine(error, { secrets: ['', 'the', 'server'] })).toBe(
      'Error: ERR the server refused the command',
    );
  });

  test('passing no secrets changes nothing', () => {
    expect(toRedactedLogLine(new Error('plain text'), {})).toBe('Error: plain text');
  });
});
