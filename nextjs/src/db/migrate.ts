import { Pool } from "pg";
import { drizzle } from "drizzle-orm/node-postgres";
import { migrate } from "drizzle-orm/node-postgres/migrator";
// The schema module, not `src/env/server.mjs`: this is a standalone script
// (`npm run db:migrate`) that needs the database URL alone, and server.mjs
// would refuse to run it without every Stripe, Resend and Upstash secret too.
import { serverEnv } from "../env/schema.server.mjs";

async function main() {
  const connectionString = serverEnv.PLANETSCALE_DATABASE_URL;
  if (!connectionString) {
    throw new Error("PLANETSCALE_DATABASE_URL is not set");
  }
  const pool = new Pool({ connectionString });
  const db = drizzle(pool);
  await migrate(db, { migrationsFolder: "./drizzle" });
  await pool.end();
  process.exit(0);
}

main().catch((error: unknown) => {
  console.error("Database migration failed", error);
  process.exitCode = 1;
});
