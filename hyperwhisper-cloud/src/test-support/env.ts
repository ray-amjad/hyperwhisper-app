/**
 * The one way the cloud tests change an environment variable and put it back.
 * `stubEnv` reads the old value at the moment it is called, so call it where
 * the test used to read `process.env.X` into a local. Test code only: nothing
 * in `src/index.ts` imports this module.
 */

/** Set (or clear) an env var and return the function that puts the old value back. */
export function stubEnv(name: string, value: string | undefined): () => void {
  const previous = process.env[name];
  writeEnv(name, value);
  return () => writeEnv(name, previous);
}

/** Write `value` to `process.env[name]`, or delete the key when it is undefined. */
function writeEnv(name: string, value: string | undefined): void {
  if (value === undefined) {
    delete process.env[name];
  } else {
    process.env[name] = value;
  }
}
