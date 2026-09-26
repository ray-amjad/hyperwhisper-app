// The by-value `secrets` pass of `toRedactedLogLine`, directly. The ip and
// licenseKey passes, and passes 1-4 and 6, are pinned through their real
// callers in redis-core.test.ts; the Google-token write catch that feeds
// `secrets` is pinned in google-auth-core.test.ts. These are the parts no
// caller exercises: more than one secret, both sides of the length gate, a
// secret holding whitespace, and every shape the `google_oauth_token` key pass
// and the `ya29.` format pass must reach. Pure module, nothing mocks it.

import { describe, expect, test } from 'bun:test';
import { toRedactedLogLine } from './log-redaction';

describe('toRedactedLogLine — by-value secrets', () => {
  test('redacts every secret handed over, wherever it sits', () => {
    const error = new Error('proxy echoed opaque-FIRST-TOKEN and then sk-SECOND-TOKEN twice: sk-SECOND-TOKEN');

    expect(toRedactedLogLine(error, { secrets: ['opaque-FIRST-TOKEN', 'sk-SECOND-TOKEN'] })).toBe(
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

describe('toRedactedLogLine — the by-value length gate boundary', () => {
  // `MIN_BY_VALUE_KEY_CHARS` is 8. Every other fixture is far from it, so an
  // off-by-one (`>=` → `>`) would leak an exactly-8-character secret with the
  // rest of the suite green. Pin both sides of the boundary, for both fields.
  test('an exactly-8-character secret IS redacted, a 7-character one is not', () => {
    expect(toRedactedLogLine(new Error('echoed ABCDEFGH back'), { secrets: ['ABCDEFGH'] })).toBe(
      'Error: echoed <redacted-secret> back',
    );
    expect(toRedactedLogLine(new Error('echoed ABCDEFG back'), { secrets: ['ABCDEFG'] })).toBe(
      'Error: echoed ABCDEFG back',
    );
  });

  test('an exactly-8-character licence key IS redacted, a 7-character one is not', () => {
    expect(
      toRedactedLogLine(new Error('quota exceeded for account ABCDEFGH'), { licenseKey: 'ABCDEFGH' }),
    ).toBe('Error: quota exceeded for account <redacted-license-key>');
    expect(
      toRedactedLogLine(new Error('quota exceeded for account ABCDEFG'), { licenseKey: 'ABCDEFG' }),
    ).toBe('Error: quota exceeded for account ABCDEFG');
  });
});

describe('toRedactedLogLine — a by-value secret that holds whitespace', () => {
  // Pass 1 collapses the LINE's whitespace before pass 5 runs, so the needle
  // must be collapsed the same way or it no longer matches.
  test('a secret with a newline in it still matches after the line is collapsed', () => {
    expect(
      toRedactedLogLine(new Error('tok: abcdefgh\nijkl'), { secrets: ['abcdefgh\nijkl'] }),
    ).toBe('Error: tok: <redacted-secret>');
  });

  test('a licence key with a run of spaces, or padding, still matches', () => {
    expect(
      toRedactedLogLine(new Error('quota exceeded for account HW-ABCD   EFGH\tIJKL'), {
        licenseKey: ' HW-ABCD   EFGH\tIJKL ',
      }),
    ).toBe('Error: quota exceeded for account <redacted-license-key>');
  });

  test('the length gate applies to the collapsed needle, so padding cannot carry a short value past it', () => {
    // 11 characters as given, 7 once trimmed: substituting `ABCDEFG` would be
    // the same risk the gate exists for.
    expect(toRedactedLogLine(new Error('echoed ABCDEFG back'), { secrets: ['  ABCDEFG  '] })).toBe(
      'Error: echoed ABCDEFG back',
    );
  });
});

describe('toRedactedLogLine — the google_oauth_token key and the ya29. token format', () => {
  // The Google cache READ and DELETE catches hold no token to hand over by
  // value, yet auto-pipelining can co-batch a `set google_oauth_token <TOKEN>`
  // into their request, and a proxy can echo that request back as plain text.
  // These fixtures use a token WITHOUT the `ya29.` prefix, so they pin the key
  // pass on its own; the format pass is pinned separately below.
  const TOKEN = 'OPAQUE-TOKEN-1234';

  test.each([
    ['plain-text command echo', `502: set google_oauth_token ${TOKEN} ex 3540`, '502: set google_oauth_token <redacted> ex 3540'],
    ['a pipeline echoed over two lines', `get google_oauth_token\nset google_oauth_token ${TOKEN} ex 3540`, 'get google_oauth_token <redacted> google_oauth_token <redacted> ex 3540'],
    ['key=value', `google_oauth_token=${TOKEN}&x=1`, 'google_oauth_token=<redacted>&x=1'],
    ['key: value', `google_oauth_token: ${TOKEN}`, 'google_oauth_token: <redacted>'],
    ['a quoted pair with its brackets lost', `"google_oauth_token","${TOKEN}","ex"`, '"google_oauth_token","<redacted>","ex"'],
    ['a quoted pair with a space after the comma', `"google_oauth_token", "${TOKEN}"`, '"google_oauth_token", "<redacted>"'],
    ['a backslash-escaped quoted pair', `\\"google_oauth_token\\",\\"${TOKEN}\\"`, '\\"google_oauth_token\\",\\"<redacted>\\"'],
    ['an HTML-escaped quoted pair', `<p>&quot;google_oauth_token&quot;,&quot;${TOKEN}&quot;</p>`, '<p>&quot;google_oauth_token&quot;,&quot;<redacted>&quot;</p>'],
    ['a single-quoted pair', `'google_oauth_token': '${TOKEN}'`, "'google_oauth_token': '<redacted>'"],
    ['inside an HTML page', `<html>413: set google_oauth_token ${TOKEN} ex 3540</html>`, '<html>413: set google_oauth_token <redacted> ex 3540</html>'],
  ])('redacts the value after the key: %s', (_shape, message, expected) => {
    expect(toRedactedLogLine(new Error(message))).toBe(`Error: ${expected}`);
  });

  test('leaves a bare key, and an unquoted comma after it, alone', () => {
    // A comma separates only a QUOTED pair, so the Upstash suffix keeps its
    // wording; and a key with nothing after it has no value to take.
    expect(toRedactedLogLine(new Error('ERR google_oauth_token, command was: del'))).toBe(
      'Error: ERR google_oauth_token, command was: del',
    );
    expect(toRedactedLogLine(new Error('502: del google_oauth_token'))).toBe(
      'Error: 502: del google_oauth_token',
    );
  });

  test('is a fixed point on its own output', () => {
    const once = toRedactedLogLine(new Error(`set google_oauth_token ${TOKEN} ex 3540`));
    expect(toRedactedLogLine(once)).toBe(once);
  });

  test('redacts a ya29. Google access token by format, with no key in front of it', () => {
    expect(toRedactedLogLine(new Error('401 for Bearer ya29.a0AfB_by-X.y_z9 at upstream'))).toBe(
      'Error: 401 for Bearer ya29.<redacted> at upstream',
    );
    // An encoding the key pass does not parse.
    expect(toRedactedLogLine(new Error('body=%22google_oauth_token%22%2C%22ya29.a0AfB_by%22'))).toBe(
      'Error: body=%22google_oauth_token%22%2C%22ya29.<redacted>%22',
    );
  });

  test('the key pass wins over the format pass, so a ya29. token after the key leaves one whole marker', () => {
    expect(toRedactedLogLine(new Error('set google_oauth_token ya29.a0AfB_by ex 3540'))).toBe(
      'Error: set google_oauth_token <redacted> ex 3540',
    );
  });
});
