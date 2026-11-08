"use client";

import { useEffect, useRef, useState } from "react";

import { GLASS, LABEL } from "@/components/landing/dusk-pages";

export type TocItem = { id: string; label: string };

const LINK =
  "block rounded-lg px-2.5 py-[7px] text-[14.5px] leading-snug transition-colors duration-150 motion-reduce:transition-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";
const LINK_ON = "bg-white/[.06] text-white";
const LINK_OFF = "text-[var(--dusk-mute)] hover:text-white";

/**
 * The section a reader is in: the last heading whose top has passed the line
 * a quarter of the way down the viewport (or the first heading, above them
 * all). At the very bottom of the page it is the last heading, since a short
 * closing section can end before its heading ever reaches that line. Read on
 * scroll and resize, at most once a frame.
 *
 * A clicked entry wins until the reader scrolls on their own (wheel, touch or
 * a key), so a jump to a short section near the end still marks that section,
 * not whichever heading the jump left under the line.
 */
function useCurrentSection(ids: string[]) {
  const [current, setCurrent] = useState<string | null>(null);
  const clicked = useRef<string | null>(null);

  useEffect(() => {
    const headings = ids
      .map((id) => document.getElementById(id))
      .filter((el): el is HTMLElement => el !== null);

    if (headings.length === 0) return;

    let frame = 0;
    const read = () => {
      frame = 0;
      if (clicked.current) {
        setCurrent(clicked.current);

        return;
      }
      const atBottom =
        window.innerHeight + window.scrollY >=
        document.documentElement.scrollHeight - 2;

      if (atBottom) {
        setCurrent(headings[headings.length - 1].id);

        return;
      }
      const line = window.innerHeight * 0.25;
      let active = headings[0].id;

      for (const heading of headings) {
        if (heading.getBoundingClientRect().top <= line) active = heading.id;
        else break;
      }
      setCurrent(active);
    };
    const update = () => {
      if (!frame) frame = requestAnimationFrame(read);
    };
    // Only a reader's own scrolling lets go of a clicked entry; the jump the
    // click itself causes fires scroll events, but none of these.
    const release = () => {
      if (!clicked.current) return;
      clicked.current = null;
      update();
    };
    const userEvents = ["wheel", "touchmove", "keydown"] as const;

    window.addEventListener("scroll", update, { passive: true });
    window.addEventListener("resize", update);
    userEvents.forEach((type) =>
      window.addEventListener(type, release, { passive: true }),
    );
    read();

    return () => {
      if (frame) cancelAnimationFrame(frame);
      window.removeEventListener("scroll", update);
      window.removeEventListener("resize", update);
      userEvents.forEach((type) => window.removeEventListener(type, release));
    };
  }, [ids]);

  const choose = (id: string) => {
    clicked.current = id;
    setCurrent(id);
  };

  return { current, choose };
}

/**
 * "On this page": a sticky glass list beside the document from lg up, and a
 * collapsed <details> above it on smaller screens. The current section is
 * marked with aria-current and a tint; nothing moves.
 */
export default function LegalToc({
  items,
  label,
}: {
  items: TocItem[];
  /** "On this page", translated: the heading and the nav's accessible name. */
  label: string;
}) {
  const [ids] = useState(() => items.map((item) => item.id));
  const { current, choose } = useCurrentSection(ids);

  if (items.length === 0) return null;

  const list = (
    <ul className="grid gap-0.5">
      {items.map((item) => (
        <li key={item.id}>
          <a
            aria-current={current === item.id ? "location" : undefined}
            className={`${LINK} ${current === item.id ? LINK_ON : LINK_OFF}`}
            href={`#${item.id}`}
            onClick={() => choose(item.id)}
          >
            {item.label}
          </a>
        </li>
      ))}
    </ul>
  );

  return (
    <>
      <details className={`${GLASS} group mb-10 lg:hidden`}>
        <summary className="flex cursor-pointer list-none items-center justify-between rounded-[22px] px-5 py-4 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] [&::-webkit-details-marker]:hidden">
          <span className={LABEL}>{label}</span>
          <svg
            aria-hidden="true"
            className="size-4 text-[var(--dusk-mute)] group-open:rotate-45"
            fill="none"
            stroke="currentColor"
            strokeLinecap="round"
            strokeWidth={2}
            viewBox="0 0 24 24"
          >
            <path d="M12 5v14M5 12h14" />
          </svg>
        </summary>
        <nav aria-label={label} className="px-3 pb-4">
          {list}
        </nav>
      </details>
      <nav
        aria-label={label}
        className={`${GLASS} sticky top-[var(--site-header-offset,7.5rem)] hidden max-h-[calc(100vh-var(--site-header-offset,7.5rem)-24px)] overflow-y-auto p-[22px] lg:block`}
      >
        <p className={`${LABEL} mb-2.5 px-2.5`}>{label}</p>
        {list}
      </nav>
    </>
  );
}
