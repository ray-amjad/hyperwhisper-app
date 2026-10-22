/**
 * Shared helpers for the provider adapter tests. Test code only: nothing in
 * `src/index.ts` imports this module, so it never ships.
 */

/**
 * Swaps `console.log` for the duration of `run` and returns the details object
 * of the first `eventName` line it logged. The suite deliberately swaps the
 * global instead of adding a process-wide mock module or a spy library.
 */
export async function captureProviderEvent(
  eventName: string,
  run: () => Promise<unknown>,
): Promise<Record<string, unknown>> {
  const logged: unknown[][] = [];
  const originalLog = console.log;
  console.log = ((...args: unknown[]) => { logged.push(args); }) as typeof console.log;
  try {
    await run();
  } finally {
    console.log = originalLog;
  }
  const event = logged.find((args) => args[0] === eventName);
  if (!event) throw new Error(`${eventName} event was not logged`);
  return event[1] as Record<string, unknown>;
}

/** Return the details object from the adapter's `provider.no_speech` event. */
export async function captureNoSpeechEvent(run: () => Promise<unknown>): Promise<Record<string, unknown>> {
  return captureProviderEvent('provider.no_speech', run);
}
