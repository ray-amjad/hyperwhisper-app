import assert from "node:assert/strict";
import test from "node:test";

// Imported through a variable specifier inside each test, the way the other
// tests in this folder do: a static `.ts` import path is a type error under this
// tsconfig, but `node --test --experimental-strip-types` needs the extension.
const MODULE_PATH = "../lib/latency/fly-regions.ts";
const load = () => import(MODULE_PATH);

test("distance between a point and itself is zero", async () => {
  const { FLY_REGIONS, haversineKm } = await load();
  assert.equal(haversineKm(FLY_REGIONS.fra, FLY_REGIONS.fra), 0);
});

test("known distances land within a few percent", async () => {
  const { FLY_REGIONS, haversineKm } = await load();
  // London to Paris is about 350 km; London to Tokyo about 9600 km.
  const lhrToCdg = haversineKm(FLY_REGIONS.lhr, FLY_REGIONS.cdg);
  assert.ok(lhrToCdg > 320 && lhrToCdg < 380, `got ${lhrToCdg}`);

  const lhrToNrt = haversineKm(FLY_REGIONS.lhr, FLY_REGIONS.nrt);
  assert.ok(lhrToNrt > 9300 && lhrToNrt < 9900, `got ${lhrToNrt}`);
});

test("distance is symmetric", async () => {
  const { FLY_REGIONS, haversineKm } = await load();
  const there = haversineKm(FLY_REGIONS.syd, FLY_REGIONS.iad);
  const back = haversineKm(FLY_REGIONS.iad, FLY_REGIONS.syd);
  assert.ok(Math.abs(there - back) < 0.001);
});

test("picks the closest candidate region", async () => {
  const { nearestRegion } = await load();
  // Berlin.
  const point = { lat: 52.52, lon: 13.4 };
  const result = nearestRegion(point, ["iad", "fra", "syd"]);
  assert.equal(result?.region, "fra");
  assert.equal(result?.city, "Frankfurt");
});

test("only considers the candidates it is given", async () => {
  const { nearestRegion } = await load();
  // Frankfurt itself, but the matrix has no fra column.
  const point = { lat: 50.11, lon: 8.68 };
  const result = nearestRegion(point, ["iad", "syd"]);
  assert.equal(result?.region, "iad");
});

test("never picks the off-Fly 'local' pseudo-region", async () => {
  const { nearestRegion } = await load();
  // A point at 0,0 sits exactly on 'local' coordinates.
  const result = nearestRegion({ lat: 0, lon: 0 }, ["local", "iad"]);
  assert.equal(result?.region, "iad");
});

test("returns null when no candidate has coordinates", async () => {
  const { nearestRegion } = await load();
  assert.equal(nearestRegion({ lat: 0, lon: 0 }, ["nowhere", "local"]), null);
  assert.equal(nearestRegion({ lat: 0, lon: 0 }, []), null);
});

test("crossing the date line does not inflate distance", async () => {
  const { FLY_REGIONS, haversineKm } = await load();
  // Tokyo to Los Angeles is about 8800 km, not a trip the long way round.
  const distance = haversineKm(FLY_REGIONS.nrt, FLY_REGIONS.lax);
  assert.ok(distance > 8500 && distance < 9200, `got ${distance}`);
});

test("region code falls back to itself when unknown", async () => {
  const { regionCity } = await load();
  assert.equal(regionCity("fra"), "Frankfurt");
  assert.equal(regionCity("zzz"), "zzz");
});