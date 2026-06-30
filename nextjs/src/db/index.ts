import { drizzle } from "drizzle-orm/node-postgres";
import * as schema from "./schema";

export const db = drizzle(process.env.PLANETSCALE_DATABASE_URL!, { schema });
