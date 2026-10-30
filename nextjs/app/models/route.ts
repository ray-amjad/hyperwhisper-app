// `GET /models` is a second URL for `GET /api/internal/models`. Re-export the
// handler so the two cannot drift; the segment config stays literal here
// because Next.js reads it statically from each route file.
export { GET } from "../api/internal/models/route";

export const dynamic = "force-dynamic";
export const runtime = "nodejs";
