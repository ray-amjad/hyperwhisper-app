/**
 * The tRPC HTTP entry point and the admin routers behind it: who may reach
 * which procedure over a real HTTP request, what the admin `devices` and
 * `stats` procedures ask their collaborators, and which failures get logged.
 *
 * See `trpc-http-harness.ts` for what is mocked and why. The route handler,
 * the fetch adapter, superjson, the context factory, the auth middleware and
 * the zod parsers all run for real.
 */
import assert from "node:assert/strict";
import { afterEach, beforeEach, describe, test } from "node:test";

import {
  behaviour,
  calls,
  httpMutation,
  httpQuery,
  loadRoot,
  plainUser,
  resetHarness,
} from "./trpc-http-harness";

const LICENSE_ID = "33333333-3333-4333-8333-333333333333";

/** Every collaborator call an admin procedure could make. */
function collaboratorCalls() {
  return {
    deviceCounts: calls.deviceCounts.length,
    devicesForLicense: calls.devicesForLicense.length,
    stripeCustomersList: calls.stripeCustomersList.length,
    otherDb: calls.otherDb.length,
  };
}

const NO_CALLS = {
  deviceCounts: 0,
  devicesForLicense: 0,
  stripeCustomersList: 0,
  otherDb: 0,
};

/** Captures console output for one test, then restores it. */
function captureConsole() {
  const original = { error: console.error, debug: console.debug };
  const lines = { error: [] as string[], debug: [] as string[] };
  console.error = (...args: unknown[]) => {
    lines.error.push(args.map(String).join(" "));
  };
  console.debug = (...args: unknown[]) => {
    lines.debug.push(args.map(String).join(" "));
  };
  return {
    lines,
    restore() {
      console.error = original.error;
      console.debug = original.debug;
    },
  };
}

/** Sets NODE_ENV for one test. `process.env` typing makes it read-only. */
function setNodeEnv(value: string | undefined): () => void {
  const env = process.env as Record<string, string | undefined>;
  const before = env.NODE_ENV;
  if (value === undefined) delete env.NODE_ENV;
  else env.NODE_ENV = value;
  return () => {
    if (before === undefined) delete env.NODE_ENV;
    else env.NODE_ENV = before;
  };
}

let restoreEnv: () => void = () => {};
let consoleCapture: ReturnType<typeof captureConsole>;

beforeEach(() => {
  resetHarness();
  restoreEnv = setNodeEnv("production");
  consoleCapture = captureConsole();
});

afterEach(() => {
  consoleCapture.restore();
  restoreEnv();
  resetHarness();
});

describe("the admin namespace over HTTP", () => {
  test("the root router mounts every admin procedure the dashboard calls", async () => {
    const { appRouter } = await loadRoot();
    const adminPaths = Object.keys(appRouter._def.procedures)
      .filter((path) => path.startsWith("admin."))
      .sort();

    assert.deepEqual(adminPaths, [
      "admin.customers.addCredits",
      "admin.customers.grant",
      "admin.customers.list",
      "admin.customers.refund",
      "admin.customers.updateEmail",
      "admin.devices.forLicense",
      "admin.devices.list",
      "admin.stats.get",
    ]);
  });

  test("every admin procedure answers 401 to a caller with no session and reaches nothing", async () => {
    const { appRouter } = await loadRoot();
    const procedures = Object.entries(appRouter._def.procedures).filter(([path]) =>
      path.startsWith("admin."),
    );

    for (const [path, procedure] of procedures) {
      const type = (procedure as unknown as { _def: { type: string } })._def.type;
      const result =
        type === "mutation"
          ? await httpMutation(path, {}, null)
          : await httpQuery(path, undefined, null);

      assert.equal(result.status, 401, `${path} status`);
      assert.equal(result.error?.data.code, "UNAUTHORIZED", `${path} code`);
      assert.equal(result.data, undefined, `${path} leaked data`);
    }
    assert.deepEqual(collaboratorCalls(), NO_CALLS);
  });

  test("every admin procedure answers 403 to a signed-in customer and reaches nothing", async () => {
    const { appRouter } = await loadRoot();
    const procedures = Object.entries(appRouter._def.procedures).filter(([path]) =>
      path.startsWith("admin."),
    );

    for (const [path, procedure] of procedures) {
      const type = (procedure as unknown as { _def: { type: string } })._def.type;
      const result =
        type === "mutation"
          ? await httpMutation(path, {}, plainUser())
          : await httpQuery(path, undefined, plainUser());

      assert.equal(result.status, 403, `${path} status`);
      assert.equal(result.error?.data.code, "FORBIDDEN", `${path} code`);
      assert.equal(result.error?.message, "Admin access required", `${path} message`);
    }
    assert.deepEqual(collaboratorCalls(), NO_CALLS);
  });

  test("a role that only contains the word admin is still refused", async () => {
    const result = await httpQuery("admin.devices.list", undefined, plainUser({ role: "Admin" }));

    assert.equal(result.status, 403);
    assert.deepEqual(collaboratorCalls(), NO_CALLS);
  });

  test("the session is read from the request's own headers", async () => {
    await httpQuery("admin.stats.get");

    assert.equal(calls.getSession.length, 1);
    assert.equal(calls.getSession[0].get("cookie"), "hw-session=abc");
  });

  test("a customer procedure still needs a session over HTTP", async () => {
    const result = await httpQuery("customer.credits", undefined, null);

    assert.equal(result.status, 401);
    assert.equal(result.error?.data.code, "UNAUTHORIZED");
    assert.deepEqual(collaboratorCalls(), NO_CALLS);
  });

  test("an unknown procedure path is a 404, not a 500", async () => {
    const result = await httpQuery("admin.devices.dropAll");

    assert.equal(result.status, 404);
    assert.equal(result.error?.data.code, "NOT_FOUND");
    assert.deepEqual(collaboratorCalls(), NO_CALLS);
  });
});

describe("admin.devices.list", () => {
  test("defaults the last-active window to 30 days and returns the rows", async () => {
    behaviour.deviceCounts = [
      {
        licenseKeyId: LICENSE_ID,
        email: "buyer@example.com",
        licenseKey: "HW-FAKE-0000-0003",
        deviceCount: 3,
      },
    ];

    const result = await httpQuery("admin.devices.list");

    assert.equal(result.status, 200);
    assert.deepEqual(calls.deviceCounts, [30]);
    assert.deepEqual(result.data, { devices: behaviour.deviceCounts, days: 30 });
  });

  test("passes an explicit window through and echoes it back", async () => {
    const result = await httpQuery("admin.devices.list", { days: 7 });

    assert.equal(result.status, 200);
    assert.deepEqual(calls.deviceCounts, [7]);
    assert.deepEqual(result.data, { devices: [], days: 7 });
  });

  for (const days of [0, -5]) {
    test(`refuses a window of ${days} days before it reaches the database`, async () => {
      const result = await httpQuery("admin.devices.list", { days });

      assert.equal(result.status, 400);
      assert.equal(result.error?.data.code, "BAD_REQUEST");
      assert.deepEqual(calls.deviceCounts, []);
    });
  }

  test("a database failure is a 500 that carries the error message and is logged", async () => {
    behaviour.dbError = new Error("connection reset");

    const result = await httpQuery("admin.devices.list");

    assert.equal(result.status, 500);
    assert.equal(result.error?.data.code, "INTERNAL_SERVER_ERROR");
    assert.equal(result.error?.message, "connection reset");
    assert.ok(
      consoleCapture.lines.error.some((line) =>
        line.includes(
          "tRPC failed on query admin.devices.list: INTERNAL_SERVER_ERROR - connection reset",
        ),
      ),
      consoleCapture.lines.error.join("\n"),
    );
  });

  test("a non-Error rejection gets the generic message", async () => {
    behaviour.dbError = "boom";

    const result = await httpQuery("admin.devices.list");

    assert.equal(result.status, 500);
    assert.equal(result.error?.message, "Failed to fetch device counts");
  });
});

describe("admin.devices.forLicense", () => {
  test("asks for one license's devices with the given window", async () => {
    const seen = new Date("2026-09-01T12:00:00Z");
    behaviour.devices = [
      {
        deviceId: "device-a",
        deviceName: "Test Mac",
        createdAt: new Date("2026-08-01T00:00:00Z"),
        lastValidatedAt: seen,
      },
    ];

    const result = await httpQuery("admin.devices.forLicense", {
      licenseKeyId: LICENSE_ID,
      days: 14,
    });

    assert.equal(result.status, 200);
    assert.deepEqual(calls.devicesForLicense, [{ licenseKeyId: LICENSE_ID, days: 14 }]);
    const data = result.data as { devices: Array<{ lastValidatedAt: Date }> };
    assert.deepEqual(data, { devices: behaviour.devices });
    // superjson keeps the Date a Date across the wire.
    assert.ok(data.devices[0].lastValidatedAt instanceof Date);
  });

  test("leaves the window unset when the caller gives none", async () => {
    await httpQuery("admin.devices.forLicense", { licenseKeyId: LICENSE_ID });

    assert.deepEqual(calls.devicesForLicense, [{ licenseKeyId: LICENSE_ID, days: undefined }]);
  });

  test("refuses a license id that is not a UUID before it reaches the database", async () => {
    const result = await httpQuery("admin.devices.forLicense", {
      licenseKeyId: "1 OR 1=1",
    });

    assert.equal(result.status, 400);
    assert.equal(result.error?.data.code, "BAD_REQUEST");
    assert.deepEqual(calls.devicesForLicense, []);
  });

  test("a database failure is a 500 with the generic message for a non-Error", async () => {
    behaviour.dbError = { reason: "opaque" };

    const result = await httpQuery("admin.devices.forLicense", { licenseKeyId: LICENSE_ID });

    assert.equal(result.status, 500);
    assert.equal(result.error?.message, "Failed to fetch devices for license");
  });

  test("a database Error keeps its message", async () => {
    behaviour.dbError = new Error("timeout");

    const result = await httpQuery("admin.devices.forLicense", { licenseKeyId: LICENSE_ID });

    assert.equal(result.status, 500);
    assert.equal(result.error?.message, "timeout");
  });
});

describe("admin.stats.get", () => {
  test("counts the Stripe customers from one page of 100", async () => {
    behaviour.stripeCustomers = [{ id: "cus_a" }, { id: "cus_b" }, { id: "cus_c" }];

    const result = await httpQuery("admin.stats.get");

    assert.equal(result.status, 200);
    assert.deepEqual(calls.stripeCustomersList, [{ limit: 100 }]);
    assert.deepEqual(result.data, {
      totalCustomers: 3,
      totalCreditsUsed: 0,
      stripeCustomers: 3,
    });
  });

  test("a Stripe failure reads as zero customers, not an error", async () => {
    behaviour.stripeError = new Error("No API key provided");

    const result = await httpQuery("admin.stats.get");

    assert.equal(result.status, 200);
    assert.deepEqual(result.data, {
      totalCustomers: 0,
      totalCreditsUsed: 0,
      stripeCustomers: 0,
    });
  });
});

describe("error logging in the route", () => {
  test("in production a 4xx is not logged at all", async () => {
    await httpQuery("admin.stats.get", undefined, plainUser());
    await httpQuery("admin.devices.list", { days: 0 });

    assert.deepEqual(consoleCapture.lines.error, []);
    assert.deepEqual(consoleCapture.lines.debug, []);
  });

  test("in development a 4xx is a debug line, not an error line", async () => {
    restoreEnv();
    restoreEnv = setNodeEnv("development");

    await httpQuery("admin.stats.get", undefined, plainUser());

    assert.ok(
      consoleCapture.lines.debug.some((line) =>
        line.includes("tRPC failed on query admin.stats.get: FORBIDDEN - Admin access required"),
      ),
      consoleCapture.lines.debug.join("\n"),
    );
    assert.ok(
      !consoleCapture.lines.error.some((line) => line.startsWith("tRPC failed")),
      "a 4xx must not be promoted to console.error",
    );
    // The stack is printed in development, and only there.
    assert.ok(
      consoleCapture.lines.error.some((line) => line.includes("TRPCError")),
      consoleCapture.lines.error.join("\n"),
    );
  });

  test("in production a 5xx is logged without the stack", async () => {
    behaviour.dbError = new Error("connection reset");

    await httpQuery("admin.devices.list");

    const trpcLines = consoleCapture.lines.error.filter((line) => line.startsWith("tRPC failed"));
    assert.equal(trpcLines.length, 1);
    assert.ok(
      !consoleCapture.lines.error.some((line) => /\n\s+at /.test(line)),
      "production must not print a stack trace",
    );
  });

  test("the logged path names the procedure that failed, for a mutation too", async () => {
    restoreEnv();
    restoreEnv = setNodeEnv("development");

    await httpMutation("admin.customers.grant", { email: "x@example.com" }, plainUser());

    assert.ok(
      consoleCapture.lines.debug.some((line) =>
        line.startsWith("tRPC failed on mutation admin.customers.grant: FORBIDDEN"),
      ),
      consoleCapture.lines.debug.join("\n"),
    );
  });
});

