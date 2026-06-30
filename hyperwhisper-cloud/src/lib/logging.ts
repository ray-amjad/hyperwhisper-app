const PROCESS_START_MS = performance.now();

export function machineUptimeMs(): number {
  return Math.round(performance.now() - PROCESS_START_MS);
}

export function logEvent(
  requestId: string,
  startTime: number,
  event: string,
  details: Record<string, unknown> = {},
) {
  console.log(JSON.stringify({
    event,
    requestId,
    elapsedMs: Math.round(performance.now() - startTime),
    ...details,
  }));
}

// Parses Fly-Request-Start (unix ms or RFC1123 date) and returns proxy overhead ms.
// Fly's edge sets this header when it accepts the connection; comparing to handler
// start reveals time spent in the Fly proxy / queue before user code runs.
export function flyProxyOverheadMs(header: string | undefined): number | undefined {
  if (!header) return undefined;
  const asNumber = Number(header);
  const proxyMs = Number.isFinite(asNumber) ? asNumber : Date.parse(header);
  if (!Number.isFinite(proxyMs)) return undefined;
  const overhead = Date.now() - proxyMs;
  return overhead >= 0 && overhead < 60_000 ? overhead : undefined;
}
