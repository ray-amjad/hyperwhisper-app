/**
 * Fly.io region codes with their approximate coordinates and city names.
 *
 * This is a lookup table, not a declaration of where we run. The /latency page
 * draws its region columns from the rows the edge service actually wrote; this
 * map only turns a code into a city name and lets the geo endpoint pick the
 * nearest one. A region missing from here still renders — it just shows its raw
 * code and never wins the "nearest" contest.
 */
export const FLY_REGIONS: Record<string, { city: string; lat: number; lon: number }> = {
  ams: { city: "Amsterdam", lat: 52.37, lon: 4.89 },
  arn: { city: "Stockholm", lat: 59.65, lon: 17.92 },
  atl: { city: "Atlanta", lat: 33.64, lon: -84.43 },
  bog: { city: "Bogotá", lat: 4.7, lon: -74.15 },
  bom: { city: "Mumbai", lat: 19.09, lon: 72.87 },
  bos: { city: "Boston", lat: 42.37, lon: -71.02 },
  cdg: { city: "Paris", lat: 49.01, lon: 2.55 },
  den: { city: "Denver", lat: 39.86, lon: -104.67 },
  dfw: { city: "Dallas", lat: 32.9, lon: -97.04 },
  ewr: { city: "Secaucus", lat: 40.69, lon: -74.17 },
  eze: { city: "Buenos Aires", lat: -34.82, lon: -58.54 },
  fra: { city: "Frankfurt", lat: 50.03, lon: 8.56 },
  gdl: { city: "Guadalajara", lat: 20.52, lon: -103.31 },
  gig: { city: "Rio de Janeiro", lat: -22.81, lon: -43.25 },
  gru: { city: "São Paulo", lat: -23.44, lon: -46.48 },
  hkg: { city: "Hong Kong", lat: 22.31, lon: 113.91 },
  iad: { city: "Ashburn", lat: 38.95, lon: -77.46 },
  jnb: { city: "Johannesburg", lat: -26.13, lon: 28.24 },
  lax: { city: "Los Angeles", lat: 33.94, lon: -118.41 },
  lhr: { city: "London", lat: 51.47, lon: -0.45 },
  maa: { city: "Chennai", lat: 12.99, lon: 80.17 },
  mad: { city: "Madrid", lat: 40.47, lon: -3.56 },
  mia: { city: "Miami", lat: 25.79, lon: -80.29 },
  nrt: { city: "Tokyo", lat: 35.55, lon: 140.39 },
  ord: { city: "Chicago", lat: 41.98, lon: -87.9 },
  otp: { city: "Bucharest", lat: 44.57, lon: 26.1 },
  phx: { city: "Phoenix", lat: 33.43, lon: -112.01 },
  qro: { city: "Querétaro", lat: 20.62, lon: -100.19 },
  scl: { city: "Santiago", lat: -33.39, lon: -70.79 },
  sea: { city: "Seattle", lat: 47.45, lon: -122.31 },
  sin: { city: "Singapore", lat: 1.35, lon: 103.99 },
  sjc: { city: "San Jose", lat: 37.36, lon: -121.93 },
  syd: { city: "Sydney", lat: -33.94, lon: 151.18 },
  waw: { city: "Warsaw", lat: 52.17, lon: 20.97 },
  yul: { city: "Montreal", lat: 45.47, lon: -73.74 },
  yyz: { city: "Toronto", lat: 43.68, lon: -79.63 },
  local: { city: "Local machine", lat: 0, lon: 0 },
};

const EARTH_RADIUS_KM = 6371;

function toRadians(degrees: number): number {
  return (degrees * Math.PI) / 180;
}

/** Great-circle distance in kilometres between two points. */
export function haversineKm(
  a: { lat: number; lon: number },
  b: { lat: number; lon: number },
): number {
  const dLat = toRadians(b.lat - a.lat);
  const dLon = toRadians(b.lon - a.lon);
  const lat1 = toRadians(a.lat);
  const lat2 = toRadians(b.lat);

  const h =
    Math.sin(dLat / 2) ** 2 +
    Math.cos(lat1) * Math.cos(lat2) * Math.sin(dLon / 2) ** 2;

  return 2 * EARTH_RADIUS_KM * Math.asin(Math.min(1, Math.sqrt(h)));
}

/**
 * Picks the closest region to a point, considering only the candidate codes it
 * is given — normally the regions the matrix actually has data for. Returns null
 * when no candidate has known coordinates, so the caller can leave the page
 * un-highlighted rather than guess.
 */
export function nearestRegion(
  point: { lat: number; lon: number },
  candidates: string[],
): { region: string; city: string; distanceKm: number } | null {
  let best: { region: string; city: string; distanceKm: number } | null = null;

  for (const code of candidates) {
    // 'local' is a real row value off Fly, but it is not a place — it must never
    // win a distance contest against a real region.
    if (code === "local") continue;
    const region = FLY_REGIONS[code];
    if (!region) continue;

    const distanceKm = haversineKm(point, region);
    if (!best || distanceKm < best.distanceKm) {
      best = { region: code, city: region.city, distanceKm };
    }
  }

  return best;
}

/** Human name for a region code, falling back to the code itself. */
export function regionCity(code: string): string {
  return FLY_REGIONS[code]?.city ?? code;
}