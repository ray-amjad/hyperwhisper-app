// Where this process runs. Fly sets FLY_REGION and FLY_MACHINE_ID on every
// machine; on a laptop or in a test they are unset. Read at call time, never
// at import, because tests set them per case.

/** The Fly region code (`nrt`, `iad`, …), or undefined off Fly. */
export function flyRegion(): string | undefined {
  return process.env.FLY_REGION || undefined;
}

/** The Fly machine id, or undefined off Fly. */
export function flyMachineId(): string | undefined {
  return process.env.FLY_MACHINE_ID || undefined;
}

/** The region and machine for a log line: `local` off Fly, so the field is never empty. */
export function machineLabels(): { region: string; machineId: string } {
  return { region: flyRegion() ?? 'local', machineId: flyMachineId() ?? 'local' };
}
