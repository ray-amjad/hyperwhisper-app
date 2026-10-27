/**
 * The first value of a query parameter.
 *
 * Next hands a page every query parameter as `string | string[] | undefined`:
 * a parameter that appears twice (`?license_key=A&license_key=B`) arrives as an
 * array. Reading it as a plain string and calling a string method on it threw
 * and answered 500 (#2061). A repeated parameter uses its first value.
 */
export function firstParam(
  value: string | string[] | undefined,
): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
