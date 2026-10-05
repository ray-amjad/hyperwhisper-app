/**
 * Whether /credits shows `buyCredits.errorEmail` under the email field (#967).
 *
 * Only once the buyer has left the field, and never for a blank one: an
 * untouched or empty field is unfinished, not wrong. `valid` is the
 * component's own `EMAIL_RE` result, which also keeps checkout disabled.
 */
export function showEmailError(
  email: string,
  touched: boolean,
  valid: boolean,
): boolean {
  return touched && email.trim() !== "" && !valid;
}
