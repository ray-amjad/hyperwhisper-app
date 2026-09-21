/**
 * The license key generator and its format check
 * (`lib/services/license-key.ts`).
 *
 * Every paid HyperWhisper install is identified by one of these strings, and
 * `insertAccountKey` stores whatever this function returns. Two faults here are
 * expensive and silent: a biased draw shrinks the key space, and a malformed
 * key is written to the database and then fails every later format check, so
 * the customer cannot activate the product they paid for.
 *
 * The tests below assert the alphabet, the shape, the draw bound and the
 * behaviour of the retry loop. `crypto` is replaced at the module boundary in
 * `license-key-generation-harness.ts`; the service itself is the real one.
 */
import assert from "node:assert/strict";
import { after, before, beforeEach, describe, test } from "node:test";

import {
  EXPECTED_ALPHABET,
  behaviour,
  calls,
  loadService,
  resetHarness,
  restoreRetryWarnings,
  silenceRetryWarnings,
  warnLines,
} from "./license-key-generation-harness";

const KEY_PATTERN = /^HW-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/;

before(() => {
  silenceRetryWarnings();
});

after(() => {
  restoreRetryWarnings();
});

beforeEach(() => {
  resetHarness();
  warnLines.length = 0;
});

describe("generateLicenseKey — the shape of a key", () => {
  test("produces the documented HW-XXXX-XXXX-XXXX-XXXX shape", async () => {
    const { generateLicenseKey, isValidKeyFormat } = await loadService();

    const key = generateLicenseKey();

    assert.match(key, KEY_PATTERN);
    // "HW" + 4 groups of a dash and 4 characters. The file header says 19,
    // which is wrong; 22 is what the format it describes actually measures.
    assert.equal(key.length, 22);
    assert.equal(isValidKeyFormat(key), true);
  });

  test("never emits a character a customer would misread", async () => {
    const { generateLicenseKey } = await loadService();
    const banned = new Set(["0", "O", "1", "I", "L"]);

    for (let i = 0; i < 200; i++) {
      for (const char of generateLicenseKey().slice(3).replace(/-/g, "")) {
        assert.equal(
          banned.has(char),
          false,
          `key ${i} used the ambiguous character ${char}`,
        );
        assert.equal(
          EXPECTED_ALPHABET.includes(char),
          true,
          `key ${i} used ${char}, which is outside the alphabet`,
        );
      }
    }
  });

  test("does not repeat a key over 500 draws", async () => {
    const { generateLicenseKey } = await loadService();

    const seen = new Set<string>();
    for (let i = 0; i < 500; i++) {
      seen.add(generateLicenseKey());
    }

    assert.equal(seen.size, 500);
  });
});

describe("generateLicenseKey — how a character is drawn", () => {
  test("draws 16 indices, each bounded by the 31-character alphabet", async () => {
    const { generateLicenseKey } = await loadService();

    generateLicenseKey();

    assert.equal(EXPECTED_ALPHABET.length, 31);
    assert.equal(calls.randomInt.length, 16);
    for (const call of calls.randomInt) {
      assert.equal(
        call.max,
        31,
        "an index must be drawn against the alphabet length, so that rejection sampling removes the modulo bias",
      );
    }
  });

  test("maps each drawn index onto the alphabet character at that index", async () => {
    const { generateLicenseKey } = await loadService();
    behaviour.script = [0, 1, 2, 3, 30, 29, 28, 27, 4, 5, 6, 7, 26, 25, 24, 23];

    const key = generateLicenseKey();

    assert.equal(key, "HW-ABCD-9876-EFGH-5432");
  });

  test("puts the first and the last alphabet character in a key unchanged", async () => {
    const { generateLicenseKey } = await loadService();
    behaviour.fixedIndex = 0;

    assert.equal(generateLicenseKey(), "HW-AAAA-AAAA-AAAA-AAAA");

    behaviour.fixedIndex = 30;

    assert.equal(generateLicenseKey(), "HW-9999-9999-9999-9999");
  });
});

describe("generateLicenseKey — the retry loop", () => {
  test("retries after a failed draw and still returns a valid key", async () => {
    const { generateLicenseKey, isValidKeyFormat } = await loadService();
    behaviour.script = [new Error("entropy pool drained")];

    const key = generateLicenseKey();

    assert.equal(isValidKeyFormat(key), true);
    assert.equal(warnLines.length, 1);
    assert.match(warnLines[0] ?? "", /attempt 1 failed, retrying/);
    assert.equal(
      calls.randomInt.length,
      17,
      "one failed draw, then a whole 16-draw key",
    );
  });

  test("survives two failed attempts and returns a key on the third", async () => {
    const { generateLicenseKey, isValidKeyFormat } = await loadService();
    behaviour.script = [new Error("first"), new Error("second")];

    const key = generateLicenseKey();

    assert.equal(isValidKeyFormat(key), true);
    assert.equal(warnLines.length, 2);
    assert.match(warnLines[0] ?? "", /attempt 1 failed, retrying/);
    assert.match(warnLines[1] ?? "", /attempt 2 failed, retrying/);
    assert.equal(
      calls.randomInt.length,
      18,
      "two failed draws, then a whole 16-draw key",
    );
  });

  test("gives up after 3 attempts and names the last fault", async () => {
    const { generateLicenseKey } = await loadService();
    behaviour.alwaysThrow = new Error("entropy pool drained");

    assert.throws(
      () => generateLicenseKey(),
      (error: unknown) => {
        const message = String((error as Error).message);
        assert.match(
          message,
          /Failed to generate valid license key after 3 attempts/,
        );
        assert.match(message, /entropy pool drained/);
        return true;
      },
    );
    assert.equal(
      warnLines.length,
      2,
      "the last attempt throws rather than warning",
    );
    assert.equal(calls.randomInt.length, 3, "one failed draw per attempt");
  });

  test("rejects an index past the end of the alphabet", async () => {
    const { generateLicenseKey } = await loadService();
    behaviour.fixedIndex = 31;

    assert.throws(
      () => generateLicenseKey(),
      (error: unknown) => {
        assert.match(
          String((error as Error).message),
          /Character index 31 out of bounds \(alphabet length: 31\)/,
        );
        return true;
      },
    );
  });

  test("rejects an in-range index that is not a whole number", async () => {
    const { generateLicenseKey } = await loadService();
    // 0.5 passes the bounds check, and `ALPHABET[0.5]` is `undefined`. This is
    // the fault the second guard exists for: a draw that is in range but is
    // not an index.
    behaviour.fixedIndex = 0.5;

    assert.throws(
      () => generateLicenseKey(),
      (error: unknown) => {
        assert.match(
          String((error as Error).message),
          /Invalid character at index 0\.5: got undefined \(type: undefined\)/,
        );
        return true;
      },
    );
  });

  test("rejects a negative index", async () => {
    const { generateLicenseKey } = await loadService();
    behaviour.fixedIndex = -1;

    assert.throws(
      () => generateLicenseKey(),
      (error: unknown) => {
        assert.match(
          String((error as Error).message),
          /Character index -1 out of bounds/,
        );
        return true;
      },
    );
  });
});

describe("isValidKeyFormat", () => {
  test("accepts a well-formed key in either case", async () => {
    const { isValidKeyFormat } = await loadService();

    assert.equal(isValidKeyFormat("HW-ABCD-2345-WXYZ-6789"), true);
    assert.equal(isValidKeyFormat("hw-abcd-2345-wxyz-6789"), true);
  });

  test("rejects a key holding a character the alphabet leaves out", async () => {
    const { isValidKeyFormat } = await loadService();

    for (const ambiguous of ["0", "O", "1", "I"]) {
      assert.equal(
        isValidKeyFormat(`HW-ABC${ambiguous}-2345-WXYZ-6789`),
        false,
        `${ambiguous} must not pass the format check`,
      );
    }
  });

  /**
   * KNOWN GAP, pinned here rather than changed.
   *
   * The alphabet is "ABCDEFGHJKMNPQRSTUVWXYZ23456789", which has no `L`, and
   * the file header lists `L` among the characters it removes. The validator's
   * character class is `[A-HJ-NP-Z2-9]`, and `J-N` puts `L` back. So the
   * validator admits one character the generator can never emit — 32 accepted
   * symbols against 31 issued ones.
   *
   * No issued key can hold an `L`, so nothing in production is mis-validated
   * today; the cost is that a typo of `1` as `L` reaches the database lookup
   * instead of stopping at the format check. Tightening the class is a source
   * change and belongs in its own pull request. This test states what the code
   * does now, so that change has to be deliberate.
   */
  test("accepts an `L`, although the generator can never emit one", async () => {
    const { isValidKeyFormat } = await loadService();

    assert.equal(isValidKeyFormat("HW-ABCL-2345-WXYZ-6789"), true);
    assert.equal(EXPECTED_ALPHABET.includes("L"), false);
  });

  test("rejects a wrong prefix", async () => {
    const { isValidKeyFormat } = await loadService();

    assert.equal(isValidKeyFormat("XX-ABCD-2345-WXYZ-6789"), false);
    assert.equal(isValidKeyFormat("ABCD-2345-WXYZ-6789"), false);
  });

  test("rejects a wrong number of segments or a wrong segment length", async () => {
    const { isValidKeyFormat } = await loadService();

    assert.equal(isValidKeyFormat("HW-ABCD-2345-WXYZ"), false);
    assert.equal(isValidKeyFormat("HW-ABCD-2345-WXYZ-6789-ABCD"), false);
    assert.equal(isValidKeyFormat("HW-ABC-2345-WXYZ-6789"), false);
    assert.equal(isValidKeyFormat("HW-ABCDE-2345-WXYZ-6789"), false);
  });

  test("rejects surrounding whitespace, so a pasted key is normalised first", async () => {
    const { isValidKeyFormat } = await loadService();

    assert.equal(isValidKeyFormat(" HW-ABCD-2345-WXYZ-6789"), false);
    assert.equal(isValidKeyFormat("HW-ABCD-2345-WXYZ-6789\n"), false);
  });

  test("rejects an empty string and a value that is not a string", async () => {
    const { isValidKeyFormat } = await loadService();

    assert.equal(isValidKeyFormat(""), false);
    assert.equal(isValidKeyFormat(null as unknown as string), false);
    assert.equal(isValidKeyFormat(undefined as unknown as string), false);
    assert.equal(isValidKeyFormat(12345 as unknown as string), false);
  });
});
