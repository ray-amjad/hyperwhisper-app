/**
 * #1099: an iPhone or iPad on /download landed on the macOS tab and the 5 s
 * countdown downloaded the DMG. The page now asks `isMobileDevice` before it
 * starts the countdown. These are the inputs real browsers report, taken from
 * Playwright's device descriptors (iPhone 13, iPad Pro 11, Pixel 7) and a
 * desktop Mac and Windows browser.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { isMobileDevice } from "@/src/lib/mobile-device";

const IPHONE_13_UA =
  "Mozilla/5.0 (iPhone; CPU iPhone OS 15_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.0 Mobile/15E148 Safari/604.1";
// iPadOS 13+ asks for the desktop site: a Mac user agent and platform MacIntel.
const IPAD_PRO_DESKTOP_UA =
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Safari/605.1.15";
// The older iPad UA that names the device, as Playwright's descriptor sends.
const IPAD_PRO_11_UA =
  "Mozilla/5.0 (iPad; CPU OS 12_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.0 Mobile/15E148 Safari/604.1";
const PIXEL_7_UA =
  "Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
const DESKTOP_MAC_UA =
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
const WINDOWS_UA =
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
const DESKTOP_LINUX_UA =
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

test("an iPhone is mobile", () => {
  assert.equal(isMobileDevice(IPHONE_13_UA, "iPhone", 5), true);
});

test("an iPad Pro with a desktop Mac UA is mobile by its touch points", () => {
  assert.equal(isMobileDevice(IPAD_PRO_DESKTOP_UA, "MacIntel", 5), true);
});

test("an iPad that names itself in the UA is mobile", () => {
  assert.equal(isMobileDevice(IPAD_PRO_11_UA, "MacIntel", 5), true);
});

test("an Android phone is mobile although its platform says Linux", () => {
  assert.equal(isMobileDevice(PIXEL_7_UA, "Linux armv81", 5), true);
});

test("a desktop Mac is not mobile", () => {
  assert.equal(isMobileDevice(DESKTOP_MAC_UA, "MacIntel", 0), false);
});

test("a Mac with one touch point (a trackpad report) is not mobile", () => {
  assert.equal(isMobileDevice(DESKTOP_MAC_UA, "MacIntel", 1), false);
});

test("a Windows touch laptop is not mobile", () => {
  assert.equal(isMobileDevice(WINDOWS_UA, "Win32", 10), false);
});

test("a desktop Linux browser is not mobile", () => {
  assert.equal(isMobileDevice(DESKTOP_LINUX_UA, "Linux x86_64", 0), false);
});
