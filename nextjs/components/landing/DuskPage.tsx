import type { CSSProperties, ReactNode } from "react";

import { WRAP } from "./dusk";
import { PAGE_H1 } from "./dusk-pages";

/** `--d` is the entrance delay that `.dusk-enter` in globals.css reads. */
export const delay = (ms: number) => ({ "--d": `${ms}ms` }) as CSSProperties;

const SKIES = {
  dusk: {
    src: "/landing/dusk-1672.webp",
    srcSet: "/landing/dusk-960.webp 960w, /landing/dusk-1672.webp 1672w",
    position: "object-[center_72%]",
  },
  night: {
    src: "/landing/night-1672.webp",
    srcSet: "/landing/night-960.webp 960w, /landing/night-1672.webp 1672w",
    position: "object-[center_40%]",
  },
} as const;

/**
 * The page shell of a Dusk sub-page: night background, light ink, and a clip
 * on sideways overflow so the bleeding sky never adds a horizontal scrollbar.
 * `data-dusk-page` tells LayoutWrapper's container to drop its width and
 * gutter (a CSS `:has()`), so each section centres itself with WRAP.
 */
export function DuskPage({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={`min-h-screen overflow-x-clip bg-[var(--dusk-night)] text-[var(--dusk-ink)] ${className}`}
      data-dusk-page=""
    >
      {children}
    </div>
  );
}

/**
 * The sub-page header: a band of the homepage sky fading into night, with an
 * eyebrow, the page's h1 and a lead. The entrance is CSS (`dusk-enter`), so it
 * plays from the server HTML; Reduce motion turns it off (globals.css). The
 * band's height comes from its text alone (the image is absolutely placed), so
 * nothing shifts when the image arrives.
 */
export function PageHero({
  eyebrow,
  title,
  lead,
  sky = "dusk",
  titleId,
  children,
}: {
  eyebrow?: ReactNode;
  title: ReactNode;
  lead?: ReactNode;
  sky?: keyof typeof SKIES;
  titleId?: string;
  /** Extra content under the lead (a date line, a button). */
  children?: ReactNode;
}) {
  const image = SKIES[sky];

  return (
    <header className="relative isolate overflow-hidden pb-20 pt-16 text-center md:pb-[90px] md:pt-[100px]">
      <div aria-hidden="true" className="absolute inset-[-10%_-4%] -z-20">
        <img
          alt=""
          className={`h-full w-full object-cover opacity-95 ${image.position}`}
          decoding="async"
          sizes="100vw"
          src={image.src}
          srcSet={image.srcSet}
        />
      </div>
      <div
        aria-hidden="true"
        className="absolute inset-0 -z-10 bg-[linear-gradient(rgba(5,6,13,.15),rgba(5,6,13,.35)_55%,var(--dusk-night))]"
      />
      <div className={WRAP}>
        {eyebrow ? (
          <p
            className="dusk-enter text-[12.5px] uppercase tracking-[2.4px] text-[var(--dusk-lav)]/80"
            style={delay(0)}
          >
            {eyebrow}
          </p>
        ) : null}
        <h1
          className={`dusk-enter mx-auto mt-3.5 max-w-[900px] ${PAGE_H1}`}
          id={titleId}
          style={delay(80)}
        >
          {title}
        </h1>
        {lead ? (
          <p
            className="dusk-enter mx-auto mt-4 max-w-[620px] text-base leading-[1.55] text-white/[.85] [text-shadow:0_1px_14px_rgba(20,10,40,.5)] md:text-lg"
            style={delay(160)}
          >
            {lead}
          </p>
        ) : null}
        {children ? (
          <div className="dusk-enter mt-6" style={delay(240)}>
            {children}
          </div>
        ) : null}
      </div>
    </header>
  );
}
