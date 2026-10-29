"use client";

import { useTranslations } from "next-intl";

import Reveal from "./Reveal";

/**
 * Apps people dictate into. These are only "works in" examples: HyperWhisper
 * types wherever there is a text cursor. No partnership or endorsement is
 * implied, and the copy around them must not claim one.
 */
const APPS: ReadonlyArray<readonly [file: string, name: string]> = [
  ["slack", "Slack"],
  ["notion", "Notion"],
  ["cursor", "Cursor"],
  ["vscode", "VS Code"],
  ["chatgpt", "ChatGPT"],
  ["claude", "Claude"],
  ["linear", "Linear"],
  ["figma", "Figma"],
  ["discord", "Discord"],
  ["whatsapp", "WhatsApp"],
  ["github", "GitHub"],
  ["obsidian", "Obsidian"],
  ["google", "Google"],
  ["office", "Microsoft Office"],
  ["x", "X"],
  ["zoom", "Zoom"],
  ["telegram", "Telegram"],
  ["arc", "Arc"],
  ["raycast", "Raycast"],
  ["superhuman", "Superhuman"],
  ["perplexity", "Perplexity"],
  ["zed", "Zed"],
  ["warp", "Warp"],
  ["spotify", "Spotify"],
  ["atlassian", "Atlassian"],
];

function Tile({
  file,
  name,
  hidden,
}: {
  file: string;
  name: string;
  hidden?: boolean;
}) {
  return (
    <li
      aria-hidden={hidden || undefined}
      className="me-7 size-[60px] flex-none rounded-[15px] bg-white p-[7px] shadow-[0_8px_24px_rgba(0,0,0,.45)] transition-transform duration-200 ease-out hover:-translate-y-1"
    >
      <img
        alt={hidden ? "" : name}
        className="block size-full rounded-[9px]"
        decoding="async"
        height={46}
        loading="lazy"
        src={`/landing/logos/${file}.webp`}
        width={46}
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
          {APPS.map(([file, name]) => (
            <Tile key={file} file={file} name={name} />
          ))}
          {/* The second copy only makes the loop seamless. Each tile carries its
              own trailing margin (no flex gap), so -50% is exactly one copy. */}
          {APPS.map(([file, name]) => (
            <Tile key={`${file}-2`} hidden file={file} name={name} />
          ))}
        </ul>
      </Reveal>
    </section>
  );
}
