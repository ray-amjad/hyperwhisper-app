"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { AnimatePresence, m } from "framer-motion";

import { ICON_SWAP } from "@/lib/icon-swap";
import { ErrorReveal } from "@/components/ErrorReveal";
import {
  CARD,
  CHIP,
  CHIP_LAV,
  CHIP_MUTE,
  CODE_BOX,
  ICON_BTN,
  LABEL,
} from "@/components/user/panel-styles";

interface License {
  id: string;
  key: string;
  status: string;
}

interface LicenseKeysCardProps {
  licenses: License[];
  /**
   * Per-key controls under the key's row (the dashboard passes the rotate
   * control). A render slot, not a hook here: this card holds only useState,
   * useRef and useEffect, so a test can call it as a plain function and
   * render it without any provider. `reveal` shows that key unmasked (used
   * after a rotation, so the new key is on screen to copy).
   */
  renderActions?: (license: License, reveal: () => void) => ReactNode;
  /**
   * Account-level controls under the list (the dashboard passes the "Add a
   * new key" control and the line a delete leaves behind).
   */
  footer?: ReactNode;
}

/**
 * License Keys Card
 *
 * Displays all license keys in a single card with show/hide and copy functionality.
 * Active keys come first, so an inactive one never sits between two live ones
 * (#1664).
 */
export default function LicenseKeysCard({
  licenses,
  renderActions,
  footer,
}: LicenseKeysCardProps) {
  const [showKeys, setShowKeys] = useState<Record<string, boolean>>({});
  const [copiedKey, setCopiedKey] = useState<string | null>(null);
  // The row whose last copy was refused (#872). Cleared by the next copy click.
  const [copyFailedKey, setCopyFailedKey] = useState<string | null>(null);
  // One tick-reset timer at a time, so an earlier copy's timer never cuts a
  // later copy's tick short (as CustomersClient's copyKey does).
  const copyTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    return () => {
      if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    };
  }, []);

  const maskLicenseKey = (key: string) => {
    const segments = key.split("-");
    if (segments.length <= 2) return key;
    return segments.slice(0, 2).join("-") + "-" + segments.slice(2).map(() => "••••").join("-");
  };

  const copyToClipboard = async (key: string) => {
    setCopyFailedKey(null);
    if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    copyTimerRef.current = null;
    try {
      await navigator.clipboard.writeText(key);
      setCopiedKey(key);
      copyTimerRef.current = setTimeout(() => setCopiedKey(null), 2000);
    } catch {
      // Insecure context, denied permission or an unfocused document (#872).
      // Drop an earlier success's tick so it never sits beside the failure; its
      // pending timer was already cleared above.
      setCopiedKey(null);
      setCopyFailedKey(key);
    }
  };

  const toggleShowKey = (id: string) => {
    setShowKeys((prev) => ({ ...prev, [id]: !prev[id] }));
  };

  // A stable sort: each group keeps the order the server sent.
  const ordered = [...licenses].sort(
    (a, b) => Number(b.status === "granted") - Number(a.status === "granted"),
  );

  return (
    <div className={`${CARD} p-5 sm:p-6`}>
      <p className={`${LABEL} mb-3.5`}>Account Keys</p>
      <div className="space-y-3">
        {ordered.map((license) => {
          const isActive = license.status === "granted";
          const isShown = showKeys[license.id];
          const isCopied = copiedKey === license.key;
          const copyFailed = copyFailedKey === license.key;

          return (
            <div key={license.id}>
              {/* Both rows wrap, so on a phone the buttons and the badge drop
                  under the key instead of squeezing it to a prefix every key
                  shares, or pushing the page wider than the screen (#1664). */}
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="flex flex-wrap items-center gap-2 min-w-0 flex-1">
                  {/* Shown: wrap so a phone sees every character (#1325). Masked: one line.
                      dir="ltr": an RTL page must not reverse the segments (#1659). */}
                  <code
                    dir="ltr"
                    className={`font-mono text-[var(--dusk-ink)] ${CODE_BOX} px-3 py-1.5 rounded-lg text-sm tracking-[.02em] ${
                      isShown ? "break-all" : "truncate"
                    }`}
                  >
                    {isShown ? license.key : maskLicenseKey(license.key)}
                  </code>
                  <button
                    onClick={() => toggleShowKey(license.id)}
                    className={`relative p-1.5 shrink-0 ${ICON_BTN}`}
                    title={isShown ? "Hide Account Key" : "Show Account Key"}
                  >
                    <AnimatePresence initial={false} mode="popLayout">
                      <m.span
                        key={isShown ? "hide" : "show"}
                        className="flex"
                        {...ICON_SWAP}
                      >
                        {isShown ? (
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13.875 18.825A10.05 10.05 0 0112 19c-4.478 0-8.268-2.943-9.543-7a9.97 9.97 0 011.563-3.029m5.858.908a3 3 0 114.243 4.243M9.878 9.878l4.242 4.242M9.88 9.88l-3.29-3.29m7.532 7.532l3.29 3.29M3 3l3.59 3.59m0 0A9.953 9.953 0 0112 5c4.478 0 8.268 2.943 9.543 7a10.025 10.025 0 01-4.132 5.411m0 0L21 21" />
                          </svg>
                        ) : (
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
                          </svg>
                        )}
                      </m.span>
                    </AnimatePresence>
                  </button>
                  <button
                    onClick={() => {
                      void copyToClipboard(license.key);
                    }}
                    className={`relative p-1.5 shrink-0 ${ICON_BTN}`}
                    title="Copy to clipboard"
                  >
                    <AnimatePresence initial={false} mode="popLayout">
                      <m.span
                        key={isCopied ? "copied" : "copy"}
                        className="flex"
                        {...ICON_SWAP}
                      >
                        {isCopied ? (
                          <svg className="w-4 h-4 text-[var(--dusk-lav)]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                          </svg>
                        ) : (
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                          </svg>
                        )}
                      </m.span>
                    </AnimatePresence>
                  </button>
                </div>
                <span
                  className={`${CHIP} shrink-0 ${isActive ? CHIP_LAV : CHIP_MUTE}`}
                >
                  {isActive ? "Active" : "Inactive"}
                </span>
              </div>
              {/* Its own line, so the message never takes the key's width (#1325).
                  #2286: it reveals instead of arriving in one frame. The live
                  region stays mounted, so the wrapper opens on `open`, and its
                  gap is padding inside the clip. */}
              <ErrorReveal className="" open={copyFailed}>
                <p
                  aria-live="polite"
                  className="pt-1.5 text-xs text-rose-300 empty:hidden"
                  role="status"
                >
                  {copyFailed
                    ? "Copy failed — reveal the key and copy it by hand"
                    : null}
                </p>
              </ErrorReveal>
              {renderActions
                ? renderActions(license, () =>
                    setShowKeys((prev) => ({ ...prev, [license.id]: true })),
                  )
                : null}
            </div>
          );
        })}
      </div>
      {footer ? <div className="mt-3 border-t border-white/10 pt-1">{footer}</div> : null}
    </div>
  );
}
