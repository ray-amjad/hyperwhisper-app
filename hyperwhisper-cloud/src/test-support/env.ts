/**
 * The one way the cloud tests change an environment variable and put it back.
 * `stubEnv` reads the old value at the moment it is called, so call it where
 * the test used to read `process.env.X` into a local; `saveEnv` is the same
 * capture without a write. Test code only: nothing in `src/index.ts` imports
 * this module.
 */

/** Set (or clear) an env var and return the function that puts the old value back. */
export function stubEnv(name: string, value: string | undefined): () => void {
  const previous = process.env[name];
  writeEnv(name, value);
  return () => writeEnv(name, previous);
}

/**
 * Remember an env var's current value and return the function that puts it
 * back. For a suite that saves the value once, lets tests write it freely, and
 * restores it after every test: the returned function can be called again.
 */
export function saveEnv(name: string): () => void {
  return stubEnv(name, process.env[name]);
}

/** Write `value` to `process.env[name]`, or delete the key when it is undefined. */
function writeEnv(name: string, value: string | undefined): void {
  if (value === undefined) {
    delete process.env[name];
  } else {
    process.env[name] = value;
  }
}
