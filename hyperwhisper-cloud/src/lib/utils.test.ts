import { describe, expect, test } from 'bun:test';

import { isRecord, retryWithBackoff, roundToTenth, roundUpToTenth, safeReadText } from './utils';
import { CREDITS_PER_MINUTE } from './constants';

// `lib/utils.ts` is small but sits under the two things that cost money:
// `roundUpToTenth` is the round-up every credit charge goes through
// (`creditsForCost`, `estimateCreditsFromSize`), `roundToTenth` is the balance
// the credit gate compares against, and `retryWithBackoff` is the retry engine
// behind `callWithRetry` for the LLM providers.

describe('isRecord', () => {
  test('accepts plain objects', () => {
    expect(isRecord({})).toBe(true);
    expect(isRecord({ choices: [] })).toBe(true);
  });

  test('rejects null, which is the case the typeof check alone gets wrong', () => {
    expect(isRecord(null)).toBe(false);
  });

  test('rejects primitives', () => {
    expect(isRecord(undefined)).toBe(false);
    expect(isRecord('{"a":1}')).toBe(false);
    expect(isRecord(0)).toBe(false);
    expect(isRecord(false)).toBe(false);
  });

  test('also accepts arrays — callers must not use it as an "is not an array" gate', () => {
    // Documented, not wished for: `typeof [] === 'object'`, so an upstream body
    // that decodes to a JSON array passes this guard. Call sites that need a
    // real object (e.g. openai-compat-chat reading `json['usage']`) get
    // `undefined` from the index read rather than a throw, which is why this is
    // safe today — but a caller that relies on it rejecting arrays would break.
    expect(isRecord([])).toBe(true);
    expect(isRecord([1, 2])).toBe(true);
  });
});

describe('safeReadText', () => {
  test('returns the body text', async () => {
    expect(await safeReadText(new Response('upstream said no'))).toBe('upstream said no');
  });

  test('returns an empty string for an empty body, not undefined', async () => {
    // Call sites log `errorText ?? '<none>'`, so '' and undefined are different
    // outcomes: '' means the upstream answered with nothing, undefined means
    // the body could not be read at all.
    expect(await safeReadText(new Response(''))).toBe('');
  });

  test('returns undefined instead of throwing when the body is already consumed', async () => {
    const response = new Response('read once');
    await response.text();

    expect(await safeReadText(response)).toBeUndefined();
  });

  test('returns undefined instead of throwing when the body stream errors', async () => {
    const body = new ReadableStream({
      start(controller) {
        controller.error(new Error('connection reset'));
      },
    });

    expect(await safeReadText(new Response(body))).toBeUndefined();
  });
});

describe('roundUpToTenth', () => {
  test('rounds up to the next tenth, so a charge is never billed short', () => {
    expect(roundUpToTenth(0.01)).toBe(0.1);
    expect(roundUpToTenth(0.11)).toBe(0.2);
    expect(roundUpToTenth(1.001)).toBe(1.1);
    expect(roundUpToTenth(6.31)).toBe(6.4);
  });

  test('leaves an exact tenth alone', () => {
    // The `- Number.EPSILON` exists for this: a plain Math.ceil would push
    // every exact tenth up one and over-bill by 0.1 credits on every request.
    expect(roundUpToTenth(0.1)).toBe(0.1);
    expect(roundUpToTenth(0.3)).toBe(0.3);
    expect(roundUpToTenth(2.5)).toBe(2.5);
    expect(roundUpToTenth(CREDITS_PER_MINUTE)).toBe(CREDITS_PER_MINUTE);
  });

  test('absorbs binary-float noise instead of billing an extra tenth for it', () => {
    // 0.1 + 0.1 + 0.1 === 0.30000000000000004 in IEEE-754.
    expect(roundUpToTenth(0.1 + 0.1 + 0.1)).toBe(0.3);
  });

  test('clamps zero and negatives to 0 rather than emitting -0 or a negative charge', () => {
    expect(roundUpToTenth(0)).toBe(0);
    expect(roundUpToTenth(-5)).toBe(0);
    expect(Object.is(roundUpToTenth(-0.04), 0)).toBe(true);
  });

  test('returns 0 for non-finite input rather than propagating NaN into a charge', () => {
    expect(roundUpToTenth(NaN)).toBe(0);
    expect(roundUpToTenth(Infinity)).toBe(0);
    expect(roundUpToTenth(-Infinity)).toBe(0);
  });

  test('never rounds down: the result is >= the input and less than a tenth above it', () => {
    const samples = [0.05, 0.4, 0.75, 1.23, 6.3, 12.0, 99.999];

    for (const value of samples) {
      const rounded = roundUpToTenth(value);
      expect(rounded).toBeGreaterThanOrEqual(value);
      expect(rounded - value).toBeLessThan(0.1);
    }
  });
});

describe('roundToTenth', () => {
  test('rounds to the nearest tenth', () => {
    expect(roundToTenth(1.24)).toBe(1.2);
    expect(roundToTenth(1.26)).toBe(1.3);
    expect(roundToTenth(6.35)).toBe(6.4);
  });

  test('rounds a halfway value up', () => {
    expect(roundToTenth(1.25)).toBe(1.3);
    expect(roundToTenth(0.05)).toBe(0.1);
  });

  test('collapses a dust balance to 0 so the credit gate rejects it', () => {
    // `validateCredits` compares `roundToTenth(auth.credits)` against the
    // estimate, and the estimate is floored at 0.1 — so anything under 0.05
    // must round to 0 and fail the gate rather than sneak a free request.
    expect(roundToTenth(0.04)).toBe(0);
    expect(roundToTenth(0.001)).toBe(0);
  });

  test('normalises negative zero, which would otherwise print as "-0.0 credits"', () => {
    // The 402 body formats the balance with `.toFixed(1)`, and `(-0).toFixed(1)`
    // is the string '-0.0'.
    expect(Object.is(roundToTenth(-0.01), 0)).toBe(true);
    expect(roundToTenth(-0.01).toFixed(1)).toBe('0.0');
  });

  test('keeps a real negative balance negative', () => {
    expect(roundToTenth(-1.24)).toBe(-1.2);
  });

  test('returns 0 for non-finite input', () => {
    expect(roundToTenth(NaN)).toBe(0);
    expect(roundToTenth(Infinity)).toBe(0);
  });
});

describe('retryWithBackoff', () => {
  test('calls the operation once and returns its value when it succeeds', async () => {
    let calls = 0;
    const onRetry = [] as number[];

    const result = await retryWithBackoff(
      async () => {
        calls += 1;
        return 'ok';
      },
      { initialDelayMs: 1, onRetry: (attempt) => onRetry.push(attempt) }
    );

    expect(result).toBe('ok');
    expect(calls).toBe(1);
    expect(onRetry).toEqual([]);
  });

  test('retries a failing operation and returns the eventual success', async () => {
    let calls = 0;

    const result = await retryWithBackoff(
      async () => {
        calls += 1;
        if (calls < 3) throw new Error(`upstream 503 #${calls}`);
        return 'recovered';
      },
      { initialDelayMs: 1 }
    );

    expect(result).toBe('recovered');
    expect(calls).toBe(3);
  });

  test('makes maxRetries + 1 attempts and throws the last error', async () => {
    let calls = 0;

    const attempt = retryWithBackoff(
      async () => {
        calls += 1;
        throw new Error(`attempt ${calls} failed`);
      },
      { maxRetries: 2, initialDelayMs: 1 }
    );

    await expect(attempt).rejects.toThrow('attempt 3 failed');
    expect(calls).toBe(3);
  });

  test('defaults to 3 retries, i.e. 4 attempts', async () => {
    let calls = 0;

    await expect(
      retryWithBackoff(
        async () => {
          calls += 1;
          throw new Error('always down');
        },
        { initialDelayMs: 1 }
      )
    ).rejects.toThrow('always down');

    expect(calls).toBe(4);
  });

  test('makes a single attempt when maxRetries is 0', async () => {
    let calls = 0;
    let retried = false;

    await expect(
      retryWithBackoff(
        async () => {
          calls += 1;
          throw new Error('no retries here');
        },
        { maxRetries: 0, initialDelayMs: 1, onRetry: () => { retried = true; } }
      )
    ).rejects.toThrow('no retries here');

    expect(calls).toBe(1);
    expect(retried).toBe(false);
  });

  test('reports the attempt number, the error, and a doubling delay to onRetry', async () => {
    const seen: Array<{ attempt: number; message: string; delayMs: number }> = [];
    let calls = 0;

    await expect(
      retryWithBackoff(
        async () => {
          calls += 1;
          throw new Error(`fail ${calls}`);
        },
        {
          maxRetries: 3,
          initialDelayMs: 1,
          onRetry: (attempt, error, delayMs) => seen.push({ attempt, message: error.message, delayMs }),
        }
      )
    ).rejects.toThrow('fail 4');

    // One callback per retry (not per attempt), 1-based, with the default
    // multiplier of 2 applied to the initial delay.
    expect(seen).toEqual([
      { attempt: 1, message: 'fail 1', delayMs: 1 },
      { attempt: 2, message: 'fail 2', delayMs: 2 },
      { attempt: 3, message: 'fail 3', delayMs: 4 },
    ]);
  });

  test('honours an explicit backoff multiplier', async () => {
    const delays: number[] = [];

    await expect(
      retryWithBackoff(
        async () => {
          throw new Error('down');
        },
        {
          maxRetries: 3,
          initialDelayMs: 2,
          backoffMultiplier: 3,
          onRetry: (_attempt, _error, delayMs) => delays.push(delayMs),
        }
      )
    ).rejects.toThrow('down');

    expect(delays).toEqual([2, 6, 18]);
  });

  test('defaults the first delay to 1000ms', async () => {
    // Pinned because `callWithRetry` relies on the default and the total retry
    // budget has to stay inside the client's own request timeout. This test
    // pays one real 1s sleep — keep it to a single retry.
    const delays: number[] = [];

    const result = await retryWithBackoff(
      async () => {
        if (delays.length === 0) throw new Error('first call fails');
        return 'ok';
      },
      { maxRetries: 1, onRetry: (_attempt, _error, delayMs) => delays.push(delayMs) }
    );

    expect(result).toBe('ok');
    expect(delays).toEqual([1000]);
  });

  test('actually waits between attempts', async () => {
    const started = performance.now();

    await expect(
      retryWithBackoff(
        async () => {
          throw new Error('down');
        },
        { maxRetries: 2, initialDelayMs: 30, backoffMultiplier: 1 }
      )
    ).rejects.toThrow('down');

    // Two retries at 30ms each. Allow a little slack for timer granularity.
    expect(performance.now() - started).toBeGreaterThanOrEqual(50);
  });

  test('wraps a non-Error throw so onRetry and the caller always get an Error', async () => {
    const messages: string[] = [];

    const attempt = retryWithBackoff(
      async () => {
        // eslint-disable-next-line no-throw-literal
        throw 'upstream returned status 503';
      },
      {
        maxRetries: 1,
        initialDelayMs: 1,
        onRetry: (_attempt, error) => messages.push(error.message),
      }
    );

    await expect(attempt).rejects.toBeInstanceOf(Error);
    await expect(attempt).rejects.toThrow('upstream returned status 503');
    expect(messages).toEqual(['upstream returned status 503']);
  });
});
