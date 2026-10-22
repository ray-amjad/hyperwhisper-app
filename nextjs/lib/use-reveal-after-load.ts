"use client";

import { useEffect, useState } from "react";

/**
 * Flips true two frames after `loading` ends, so content paints at its
 * hidden or dimmed opacity first and a CSS transition has a start value.
 * Resets to false whenever `loading` turns true again. Shared by the
 * skeleton-reveal on /older-versions and the admin Devices table (#863).
 */
export function useRevealAfterLoad(loading: boolean): boolean {
  const [revealed, setRevealed] = useState(false);

  useEffect(() => {
    if (loading) {
      setRevealed(false);

      return;
    }
    let frame = requestAnimationFrame(() => {
      frame = requestAnimationFrame(() => setRevealed(true));
    });

    return () => cancelAnimationFrame(frame);
  }, [loading]);

  return revealed;
}
