/**
 * Is this visitor on a phone or a tablet? (#1099)
 *
 * The download page auto-downloads the macOS DMG after a 5 s countdown. Its OS
 * detection only knows Windows and Linux, so an iPhone or iPad fell through to
 * the macOS default and downloaded a desktop installer it can never open.
 *
 * iPadOS reports a desktop Mac user agent and `navigator.platform` "MacIntel",
 * so the user agent alone misses it. A real Mac reports 0 touch points; an iPad
 * reports 5. Android reports "Linux" as its platform but names itself in the
 * user agent.
 */
export function isMobileDevice(
  userAgent: string,
  platform: string,
  maxTouchPoints: number,
): boolean {
  if (/iphone|ipad|ipod|android/i.test(userAgent)) return true;

  return platform === "MacIntel" && maxTouchPoints > 1;
}
