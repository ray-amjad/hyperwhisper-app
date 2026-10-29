"use client";

import { useEffect, useState } from "react";

import { GLASS, LABEL } from "@/components/landing/dusk-pages";

export type TocItem = { id: string; label: string };

const LINK =
  "block rounded-lg px-2.5 py-[7px] text-[14.5px] leading-snug transition-colors duration-150 motion-reduce:transition-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";
const LINK_ON = "bg-white/[.06] text-white";
const LINK_OFF = "text-[var(--dusk-mute)] hover:text-white";

/**
 * The section a reader is in: the last heading whose top has passed the line
 * a quarter of the way down the viewport (or the first heading, above them
 * all). Read again whenever a heading crosses that line.
 */
function useCurrentSection(ids: string[]): string | null {
  const [current, setCurrent] = useState<string | null>(null);

  useEffect(() => {
    const headings = ids
      .map((id) => document.getElementById(id))
      .filter((el): el is HTMLElement => el !== null);

    if (headings.length === 0) return;

    const update = () => {
      const line = window.innerHeight * 0.25;
      let active = headings[0].id;

      for (const heading of headings) {
        if (heading.getBoundingClientRect().top <= line) active = heading.id;
        else break;
      }
      setCurrent(active);
    };

    const observer = new IntersectionObserver(update, {
      rootMargin: "0px 0px -75% 0px",
    });

    headings.forEach((heading) => observer.observe(heading));
    update();

    return () => observer.disconnect();
  }, [ids]);

  return current;
}

/**
 * "On this page": a sticky glass list beside the document from lg up, and a
 * collapsed <details> above it on smaller screens. The current section is
 * marked with aria-current and a tint; nothing moves.
 */
export default function LegalToc({ items }: { items: TocItem[] }) {
  const [ids] = useState(() => items.map((item) => item.id));
  const current = useCurrentSection(ids);

  if (items.length === 0) return null;

  const list = (
    <ul className="grid gap-0.5">
      {items.map((item) => (
        <li key={item.id}>
          <a
            aria-current={current === item.id ? "location" : undefined}
            className={`${LINK} ${current === item.id ? LINK_ON : LINK_OFF}`}
            href={`#${item.id}`}
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
          <span className={LABEL}>On this page</span>
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
        <nav aria-label="On this page" className="px-3 pb-4">
          {list}
        </nav>
      </details>
      <nav
        aria-label="On this page"
        className={`${GLASS} sticky top-[var(--site-header-offset,7.5rem)] hidden max-h-[calc(100vh-var(--site-header-offset,7.5rem)-24px)] overflow-y-auto p-[22px] lg:block`}
      >
        <p className={`${LABEL} mb-2.5 px-2.5`}>On this page</p>
        {list}
      </nav>
    </>
  );
}
