"use client";

import { useTranslations } from "next-intl";

import Reveal from "./Reveal";

type App = {
  /** File in /public/landing/logos. */
  file: string;
  name: string;
  /** Tile colour: the brand's own colour, or white for a multicolour logo. */
  bg: string;
  /** The image is already a whole app icon, so it fills the tile unpadded. */
  fullBleed?: boolean;
};

/**
 * Apps people dictate into. These are only "works in" examples: HyperWhisper
 * types wherever there is a text cursor. No partnership or endorsement is
 * implied, and the copy around them must not claim one.
 *
 * One rule for every tile, so the row reads like a dock: a full-bleed rounded
 * square in the app's own colours, its mark centred at the same size. A brand
 * with a one-colour mark gets its brand colour as the tile and the mark in
 * white (Spotify keeps its green mark on black, as its icon does). A brand
 * whose logo is multicolour, or black on white like Notion, gets a white tile
 * with the logo in its own colours. The SVG marks come from Simple Icons
 * (CC0), which takes each colour from the brand's guidelines.
 */
const APPS: ReadonlyArray<App> = [
  { file: "slack.webp", name: "Slack", bg: "#fff" },
  { file: "notion.webp", name: "Notion", bg: "#fff" },
  { file: "cursor.svg", name: "Cursor", bg: "#000" },
  { file: "vscode.webp", name: "VS Code", bg: "#fff" },
  { file: "chatgpt.webp", name: "ChatGPT", bg: "#000" },
  { file: "claude.svg", name: "Claude", bg: "#D97757" },
  { file: "linear.svg", name: "Linear", bg: "#5E6AD2" },
  { file: "figma.webp", name: "Figma", bg: "#fff" },
  { file: "discord.svg", name: "Discord", bg: "#5865F2" },
  { file: "whatsapp.svg", name: "WhatsApp", bg: "#25D366" },
  { file: "github.svg", name: "GitHub", bg: "#181717" },
  { file: "obsidian.webp", name: "Obsidian", bg: "#fff" },
  { file: "google.webp", name: "Google", bg: "#fff" },
  { file: "office.webp", name: "Microsoft Office", bg: "#fff" },
  { file: "x.svg", name: "X", bg: "#000" },
  { file: "zoom.svg", name: "Zoom", bg: "#0B5CFF" },
  { file: "telegram.svg", name: "Telegram", bg: "#26A5E4" },
  { file: "arc.webp", name: "Arc", bg: "#fff" },
  { file: "raycast.svg", name: "Raycast", bg: "#FF6363" },
  { file: "superhuman.webp", name: "Superhuman", bg: "#000", fullBleed: true },
  { file: "perplexity.svg", name: "Perplexity", bg: "#1FB8CD" },
  { file: "zed.svg", name: "Zed", bg: "#084CCF" },
  { file: "warp.svg", name: "Warp", bg: "#01A4FF" },
  { file: "spotify.svg", name: "Spotify", bg: "#000" },
  { file: "atlassian.svg", name: "Atlassian", bg: "#0052CC" },
];

function Tile({ app, hidden }: { app: App; hidden?: boolean }) {
  return (
    <li
      aria-hidden={hidden || undefined}
      className={`me-7 size-[60px] flex-none overflow-hidden rounded-[15px] ring-1 ring-inset ring-white/10 shadow-[0_8px_24px_rgba(0,0,0,.45)] transition-transform duration-200 ease-out hover:-translate-y-1 ${app.fullBleed ? "" : "p-[13px]"}`}
      style={{ backgroundColor: app.bg }}
    >
      <img
        alt={hidden ? "" : app.name}
        className="block size-full object-contain"
        decoding="async"
        height={34}
        // Eager: a lazy image inside the moving, clipped track is only
        // fetched once it nears the viewport, so tiles slid in blank. The
        // logos are small SVGs and 96px WebPs.
        loading="eager"
        src={`/landing/logos/${app.file}`}
        width={34}
      />
    </li>
  );
}

export default function LogoMarquee() {
  const t = useTranslations("demo");

  return (
    <section className="pb-20 pt-[30px] text-center md:pb-[110px]">
      <Reveal
        as="p"
        className="mb-[30px] px-6 text-[12.5px] uppercase tracking-[2.4px] text-[var(--dusk-dim)]"
      >
        {t("worksWherever")}
      </Reveal>
      {/* dir="ltr": the track slides by -50% of its own width, which is only a
          seamless loop when it is laid out left to right. */}
      <Reveal
        className="dusk-marquee overflow-hidden py-5"
        delay={100}
        dir="ltr"
      >
        <ul className="dusk-marquee-track flex w-max">
          {APPS.map((app) => (
            <Tile key={app.file} app={app} />
          ))}
          {/* The second copy only makes the loop seamless. Each tile carries its
              own trailing margin (no flex gap), so -50% is exactly one copy. */}
          {APPS.map((app) => (
            <Tile key={`${app.file}-2`} hidden app={app} />
          ))}
        </ul>
      </Reveal>
    </section>
  );
}
