"use client";

import { useEffect, useState } from "react";

/**
 * False while the tab is hidden. An IntersectionObserver (`useInView`) does
 * not fire when a tab goes to the background, so a decorative loop gated on
 * view alone would keep running there.
 */
export function usePageVisible(): boolean {
  const [visible, setVisible] = useState(true);

  useEffect(() => {
    const onChange = () => setVisible(!document.hidden);

    onChange();
    document.addEventListener("visibilitychange", onChange);

    return () => document.removeEventListener("visibilitychange", onChange);
  }, []);

  return visible;
}
