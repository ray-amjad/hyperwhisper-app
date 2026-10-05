/**
 * The Account Key and device queries in `src/lib/db-layer.ts`, run against a
 * real Postgres (PGlite, see `credit-ledger-harness.ts`) with the real
 * migrations.
 *
 * These rows are the licence: `findAccountByKey` decides whether a desktop
 * app is entitled, `revokeAccountKey` takes that away (and must sign the
 * holder out of the web portal in the same transaction), and the device rows
 * are what the admin reads to spot one key shared across many machines.
 */
import { before, beforeEach, describe, test } from "node:test";
import assert from "node:assert/strict";

import { sql } from "drizzle-orm";

import { db, loadDbLayer, resetDatabase, seedUser } from "./credit-ledger-harness";
import * as schema from "../src/db/schema";

const DAY = 24 * 60 * 60 * 1000;
const daysAgo = (n: number) => new Date(Date.now() - n * DAY);

let L: Awaited<ReturnType<typeof loadDbLayer>>;

before(async () => {
  L = await loadDbLayer();
});

beforeEach(async () => {
  await resetDatabase();
  await db.execute(sql`TRUNCATE device_validations`);
});

async function seedSession(id: string, userId: string): Promise<void> {
  await db.insert(schema.session).values({
    id,
    token: `token-${id}`,
    userId,
    expiresAt: new Date(Date.now() + 90 * DAY),
  });
}

async function sessionIds(): Promise<string[]> {
  const rows = await db.select({ id: schema.session.id }).from(schema.session);
  return rows.map((r) => r.id).sort();
}

async function seedKey(
  key: string,
  userId: string,
  extra: { email?: string; status?: string; stripeSessionId?: string; createdAt?: Date } = {},
): Promise<string> {
  const [row] = await db
    .insert(schema.accountKeys)
    .values({
      key,
      userId,
      email: extra.email ?? `${userId}@example.test`,
      status: extra.status ?? "granted",
      stripeSessionId: extra.stripeSessionId ?? null,
      ...(extra.createdAt ? { createdAt: extra.createdAt } : {}),
    })
    .returning({ id: schema.accountKeys.id });
  return row.id;
}

async function keyRow(id: string) {
  const row = await db.query.accountKeys.findFirst({ where: (t, { eq }) => eq(t.id, id) });
  assert.ok(row, `no account key ${id}`);
  return row;
}

async function seedDevice(
  licenseKeyId: string,
  deviceId: string,
  lastValidatedAt: Date,
  deviceName: string | null = null,
): Promise<void> {
  await db.insert(schema.deviceValidations).values({
    licenseKeyId,
    deviceId,
    deviceName,
    // Seed createdAt from the test clock too. The column default is the
    // database's now(), which can land in the same millisecond as (or after)
    // a later JS `new Date()`, so a strict ordering check flaked (#1174).
    createdAt: lastValidatedAt,
    lastValidatedAt,
  });
}

describe("insertAccountKey", () => {
  test("a key with no status is stored as granted, with null Stripe ids", async () => {
    await seedUser("u1");

    const row = await L.insertAccountKey({ key: "HW-AAAA", email: "u1@example.test", userId: "u1" });

    assert.ok(row);
    assert.equal(row.key, "HW-AAAA");
    assert.equal(row.email, "u1@example.test");
    assert.equal(row.userId, "u1");
    assert.equal(row.status, "granted");
    assert.equal(row.stripeCustomerId, null);
    assert.equal(row.stripeSessionId, null);
    assert.equal(row.polarLicenseKeyId, null);
    assert.ok(row.createdAt instanceof Date);
    const stored = await keyRow(row.id);
    assert.equal(stored.status, "granted");
  });

  test("an explicit status and Stripe ids are stored as given", async () => {
    await seedUser("u1");

    const row = await L.insertAccountKey({
      key: "HW-BBBB",
      email: "u1@example.test",
      userId: "u1",
      status: "revoked",
      stripeCustomerId: "cus_1",
      stripeSessionId: "cs_1",
    });

    assert.ok(row);
    const stored = await keyRow(row.id);
    assert.equal(stored.status, "revoked");
    assert.equal(stored.stripeCustomerId, "cus_1");
    assert.equal(stored.stripeSessionId, "cs_1");
  });
});

describe("updateAccountKey", () => {
  test("changes only the fields it is given", async () => {
    await seedUser("u1");
    const id = await seedKey("HW-CCCC", "u1", { email: "old@example.test" });

    await L.updateAccountKey(id, { stripeCustomerId: "cus_9" });

    let row = await keyRow(id);
    assert.equal(row.stripeCustomerId, "cus_9");
    assert.equal(row.status, "granted");
    assert.equal(row.email, "old@example.test");

    await L.updateAccountKey(id, { status: "revoked", email: "new@example.test" });

    row = await keyRow(id);
    assert.equal(row.status, "revoked");
    assert.equal(row.email, "new@example.test");
    assert.equal(row.stripeCustomerId, "cus_9");
  });

  test("an explicit null clears the Stripe customer", async () => {
    await seedUser("u1");
    const id = await seedKey("HW-DDDD", "u1");
    await L.updateAccountKey(id, { stripeCustomerId: "cus_9" });

    await L.updateAccountKey(id, { stripeCustomerId: null });

    assert.equal((await keyRow(id)).stripeCustomerId, null);
  });

  test("touches no other key", async () => {
    await seedUser("u1");
    const a = await seedKey("HW-EEEE", "u1");
    const b = await seedKey("HW-FFFF", "u1");

    await L.updateAccountKey(a, { status: "revoked" });

    assert.equal((await keyRow(a)).status, "revoked");
    assert.equal((await keyRow(b)).status, "granted");
  });
});

describe("revokeAccountKey", () => {
  test("revokes the key and deletes every session of its owner, and only theirs", async () => {
    await seedUser("owner");
    await seedUser("other");
    const id = await seedKey("HW-REV1", "owner");
    const kept = await seedKey("HW-REV2", "owner");
    await seedSession("s-owner-1", "owner");
    await seedSession("s-owner-2", "owner");
    await seedSession("s-other", "other");

    await L.revokeAccountKey(id, "owner");

    assert.equal((await keyRow(id)).status, "revoked");
    assert.equal((await keyRow(kept)).status, "granted");
    assert.deepEqual(await sessionIds(), ["s-other"]);
  });

  test("an admin revoking a key on their own account keeps their session", async () => {
    await seedUser("admin");
    const id = await seedKey("HW-REV3", "admin");
    await seedSession("s-admin", "admin");

    await L.revokeAccountKey(id, "admin", { actingUserId: "admin" });

    assert.equal((await keyRow(id)).status, "revoked");
    assert.deepEqual(await sessionIds(), ["s-admin"]);
  });

  test("an admin revoking someone else's key still signs that user out", async () => {
    await seedUser("admin");
    await seedUser("owner");
    const id = await seedKey("HW-REV4", "owner");
    await seedSession("s-admin", "admin");
    await seedSession("s-owner", "owner");

    await L.revokeAccountKey(id, "owner", { actingUserId: "admin" });

    assert.equal((await keyRow(id)).status, "revoked");
    assert.deepEqual(await sessionIds(), ["s-admin"]);
  });

  test("a failed session delete rolls the status write back", async () => {
    await seedUser("owner");
    const id = await seedKey("HW-REV5", "owner");
    await seedSession("s-owner", "owner");
    await db.execute(sql`
      CREATE FUNCTION refuse_session_delete() RETURNS trigger AS $$
      BEGIN RAISE EXCEPTION 'session delete refused'; END;
      $$ LANGUAGE plpgsql
    `);
    await db.execute(sql`
      CREATE TRIGGER refuse_session_delete BEFORE DELETE ON session
      FOR EACH ROW EXECUTE FUNCTION refuse_session_delete()
    `);

    try {
      await assert.rejects(L.revokeAccountKey(id, "owner"), (err: Error) => {
        // Drizzle wraps the Postgres error; the trigger's message is the cause.
        assert.match(String((err.cause as Error | undefined)?.message), /session delete refused/);
        return true;
      });
      assert.equal((await keyRow(id)).status, "granted");
      assert.deepEqual(await sessionIds(), ["s-owner"]);
    } finally {
      await db.execute(sql`DROP TRIGGER refuse_session_delete ON session`);
      await db.execute(sql`DROP FUNCTION refuse_session_delete()`);
    }
  });
});

describe("revokeWebAccess", () => {
  test("deletes the user's sessions outside a transaction too", async () => {
    await seedUser("u1");
    await seedUser("u2");
    await seedSession("a", "u1");
    await seedSession("b", "u2");

    await L.revokeWebAccess("u1");

    assert.deepEqual(await sessionIds(), ["b"]);
  });

  test("skips the sweep when the acting user is the user", async () => {
    await seedUser("u1");
    await seedSession("a", "u1");

    await L.revokeWebAccess("u1", { actingUserId: "u1" });

    assert.deepEqual(await sessionIds(), ["a"]);
  });
});

describe("Account Key lookups", () => {
  test("findAccountByKey trims the key and returns null for an unknown key", async () => {
    await seedUser("u1");
    const id = await seedKey("HW-LOOK", "u1");

    const found = await L.findAccountByKey("  HW-LOOK \n");
    assert.equal(found?.id, id);
    assert.equal(found?.userId, "u1");
    assert.equal(await L.findAccountByKey("HW-NOPE"), null);
    assert.equal(await L.findAccountByKey("hw-look"), null);
  });

  test("findAccountById returns the row, or null", async () => {
    await seedUser("u1");
    const id = await seedKey("HW-BYID", "u1");

    assert.equal((await L.findAccountById(id))?.key, "HW-BYID");
    assert.equal(await L.findAccountById("00000000-0000-0000-0000-000000000000"), null);
  });

  test("findAccountByStripeSession matches the session id exactly", async () => {
    await seedUser("u1");
    const id = await seedKey("HW-SESS", "u1", { stripeSessionId: "cs_test_1" });
    await seedKey("HW-SESS2", "u1", { stripeSessionId: "cs_test_2" });

    assert.equal((await L.findAccountByStripeSession("cs_test_1"))?.id, id);
    assert.equal(await L.findAccountByStripeSession("cs_test_3"), null);
  });

  test("getAccountKeysByEmail lowercases the input and lists the newest key first", async () => {
    await seedUser("u1");
    await seedUser("u2");
    await seedKey("HW-OLD", "u1", { email: "same@example.test", createdAt: daysAgo(10) });
    await seedKey("HW-NEW", "u1", { email: "same@example.test", createdAt: daysAgo(1) });
    await seedKey("HW-MID", "u1", { email: "same@example.test", createdAt: daysAgo(5) });
    await seedKey("HW-ELSE", "u2", { email: "else@example.test" });

    const rows = await L.getAccountKeysByEmail("Same@Example.TEST");

    assert.deepEqual(
      rows.map((r) => r.key),
      ["HW-NEW", "HW-MID", "HW-OLD"],
    );
    assert.deepEqual(await L.getAccountKeysByEmail("nobody@example.test"), []);
  });
});

describe("getGrantedEmails", () => {
  test("lists each email with a granted key once, normalised, and drops blanks", async () => {
    await seedUser("u1");
    await seedUser("u2");
    await seedUser("u3");
    await seedKey("HW-G1", "u1", { email: "a@example.test" });
    await seedKey("HW-G2", "u1", { email: "a@example.test" });
    await seedKey("HW-G3", "u2", { email: " B@Example.test " });
    await seedKey("HW-R1", "u3", { email: "revoked@example.test", status: "revoked" });
    await seedKey("HW-BLANK", "u3", { email: "  " });

    const emails = await L.getGrantedEmails();

    assert.deepEqual([...emails].sort(), ["a@example.test", "b@example.test"]);
  });
});

describe("upsertDeviceValidation", () => {
  test("a new device is stored once; a repeat updates its name and time", async () => {
    await seedUser("u1");
    const lic = await seedKey("HW-DEV1", "u1");
    const seededAt = daysAgo(3);
    await seedDevice(lic, "dev-1", seededAt, "Old name");

    const before = Date.now();
    await L.upsertDeviceValidation(lic, "dev-1", "New name");
    await L.upsertDeviceValidation(lic, "dev-2");

    const rows = await db
      .select()
      .from(schema.deviceValidations)
      .orderBy(schema.deviceValidations.deviceId);
    assert.equal(rows.length, 2);
    assert.equal(rows[0].deviceId, "dev-1");
    assert.equal(rows[0].deviceName, "New name");
    assert.ok(rows[0].lastValidatedAt.getTime() >= before - 1000);
    assert.equal(rows[0].createdAt.getTime(), seededAt.getTime());
    assert.ok(rows[0].createdAt.getTime() < rows[0].lastValidatedAt.getTime());
    assert.equal(rows[1].deviceId, "dev-2");
    assert.equal(rows[1].deviceName, null);
  });

  test("a repeat with no name clears the stored name", async () => {
    await seedUser("u1");
    const lic = await seedKey("HW-DEV2", "u1");
    await L.upsertDeviceValidation(lic, "dev-1", "Laptop");

    await L.upsertDeviceValidation(lic, "dev-1", "");

    const rows = await db.select().from(schema.deviceValidations);
    assert.equal(rows.length, 1);
    assert.equal(rows[0].deviceName, null);
  });

  test("the same device id on two keys is two rows", async () => {
    await seedUser("u1");
    const a = await seedKey("HW-DEV3", "u1");
    const b = await seedKey("HW-DEV4", "u1");

    await L.upsertDeviceValidation(a, "dev-1", "A");
    await L.upsertDeviceValidation(b, "dev-1", "B");

    const rows = await db.select().from(schema.deviceValidations);
    assert.equal(rows.length, 2);
  });
});

describe("getDevicesForLicense", () => {
  test("lists only that key's devices, most recently validated first", async () => {
    await seedUser("u1");
    const lic = await seedKey("HW-DL1", "u1");
    const other = await seedKey("HW-DL2", "u1");
    await seedDevice(lic, "old", daysAgo(40), "Old");
    await seedDevice(lic, "new", daysAgo(1), "New");
    await seedDevice(lic, "mid", daysAgo(10));
    await seedDevice(other, "foreign", daysAgo(0));

    const rows = await L.getDevicesForLicense(lic);

    assert.deepEqual(
      rows.map((r) => [r.deviceId, r.deviceName]),
      [
        ["new", "New"],
        ["mid", null],
        ["old", "Old"],
      ],
    );
    assert.ok(rows[0].lastValidatedAt instanceof Date);
    assert.ok(rows[0].createdAt instanceof Date);
  });

  test("sinceDays drops devices last validated before the window", async () => {
    await seedUser("u1");
    const lic = await seedKey("HW-DL3", "u1");
    await seedDevice(lic, "old", daysAgo(40));
    await seedDevice(lic, "edge", daysAgo(29));
    await seedDevice(lic, "new", daysAgo(1));

    const rows = await L.getDevicesForLicense(lic, 30);

    assert.deepEqual(
      rows.map((r) => r.deviceId),
      ["new", "edge"],
    );
  });
});

describe("getDeviceCountsPerLicense", () => {
  test("counts devices per key, biggest first, as numbers, and skips keys with none", async () => {
    await seedUser("u1");
    await seedUser("u2");
    const one = await seedKey("HW-C1", "u1", { email: "one@example.test" });
    const three = await seedKey("HW-C3", "u2", { email: "three@example.test" });
    await seedKey("HW-C0", "u2");
    await seedDevice(one, "d1", daysAgo(1));
    await seedDevice(three, "d1", daysAgo(1));
    await seedDevice(three, "d2", daysAgo(2));
    await seedDevice(three, "d3", daysAgo(50));

    const rows = await L.getDeviceCountsPerLicense();

    assert.deepEqual(rows, [
      { licenseKeyId: three, email: "three@example.test", licenseKey: "HW-C3", deviceCount: 3 },
      { licenseKeyId: one, email: "one@example.test", licenseKey: "HW-C1", deviceCount: 1 },
    ]);
    assert.equal(typeof rows[0].deviceCount, "number");
  });

  test("sinceDays counts only devices validated inside the window", async () => {
    await seedUser("u1");
    const a = await seedKey("HW-W1", "u1");
    const b = await seedKey("HW-W2", "u1");
    await seedDevice(a, "d1", daysAgo(1));
    await seedDevice(a, "d2", daysAgo(2));
    await seedDevice(b, "d1", daysAgo(3));
    await seedDevice(b, "d2", daysAgo(60));
    await seedDevice(b, "d3", daysAgo(70));

    const rows = await L.getDeviceCountsPerLicense(30);

    assert.deepEqual(
      rows.map((r) => [r.licenseKey, r.deviceCount]),
      [
        ["HW-W1", 2],
        ["HW-W2", 1],
      ],
    );
  });
});
