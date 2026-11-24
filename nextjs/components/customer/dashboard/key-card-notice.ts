/**
 * The Account Keys card's one success line.
 *
 * The rotate, delete and "add a new key" controls each say what just
 * happened ("Key rotated", "Key deleted", "New key added", "Sign-in link
 * sent"). Kept in each control, an older line stayed on screen beside a newer
 * one from another control. So the card owns a single notice, and each
 * control only posts to it: the latest post replaces whatever was there, in
 * any order of add, delete and rotate.
 *
 * `at` is where the line renders: under the control that posted it, or on
 * the card itself after a delete (the deleted row, and its control, are gone).
 *
 * Pure and hook-free so a test can run any sequence through it.
 */
export interface KeyCardNotice {
  at: string;
  text: string;
}

export type KeyCardNoticeState = KeyCardNotice | null;

/** The places a notice can render. */
export const NOTICE_AT = {
  rotate: (licenseId: string) => `rotate:${licenseId}`,
  deleteKey: (licenseId: string) => `delete:${licenseId}`,
  issue: "issue",
  card: "card",
} as const;

/**
 * The card's notice after a control at `at` posts `text`. A text replaces
 * any notice, wherever it was. `null` (a control starting over) clears the
 * notice only if that control posted it, so starting an action never wipes
 * another control's line before there is a newer one.
 */
export function postKeyCardNotice(
  prev: KeyCardNoticeState,
  at: string,
  text: string | null,
): KeyCardNoticeState {
  if (text === null) return prev?.at === at ? null : prev;

  return { at, text };
}

/** The line to render at `at`, or null. */
export function keyCardNoticeAt(
  state: KeyCardNoticeState,
  at: string,
): string | null {
  return state?.at === at ? state.text : null;
}
