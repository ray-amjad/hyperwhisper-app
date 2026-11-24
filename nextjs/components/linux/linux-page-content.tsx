import type { ReactNode } from "react";

import {
  Cloud,
  Cpu,
  Download,
  ExternalLink,
  Github,
  Globe,
  Keyboard,
  Plus,
  Terminal,
} from "lucide-react";
import { useTranslations } from "next-intl";

import { BTN_GLASS, BTN_LG, BTN_WHITE, WRAP } from "@/components/landing/dusk";
import {
  GLASS,
  GLASS_ON,
  ICON_TILE,
  LINK_LAV,
  SECTION,
} from "@/components/landing/dusk-pages";
import { DuskPage, PageHero, delay } from "@/components/landing/DuskPage";
import linuxLatest from "@/public/linux-latest.json";

// The /linux landing page body. It reads the `linuxPage` namespace with
// useTranslations, which works in a server component and under a
// NextIntlClientProvider alike, so a test can render it in every locale.
//
// It is a Dusk sub-page (DuskPage + PageHero, the tokens in landing/dusk*.ts)
// built from plain markup and a native <details> FAQ, so it ships no client
// JavaScript of its own: no Reveal, no HeroUI.
//
// The version and the .deb URL come from public/linux-latest.json, imported at
// build time. linux-release.yml rewrites and commits that file on every Linux
// release, so the deploy that follows picks up the new version.

export const LINUX_DOCS_URL =
  "https://www.hyperwhisper.com/docs/linux-installation";

export const LINUX_LATEST = {
  version: linuxLatest.version,
  deb: linuxLatest.deb,
  releasePage: linuxLatest.releasePage,
};

export const LINUX_INSTALL_COMMAND = `sudo apt install ./hyperwhisper_${LINUX_LATEST.version}_amd64.deb`;
export const LINUX_INPUT_GROUP_COMMAND =
  'sudo usermod -aG hyperwhisper-input "$USER"';

/** The terminal panel, as on the download page's Linux and Homebrew cards. */
const TERMINAL =
  "overflow-hidden rounded-2xl border border-[var(--dusk-line)] bg-[#070814] text-start";

const H2_PAGE =
  "text-2xl font-semibold tracking-[-0.02em] text-white md:text-[32px] [overflow-wrap:anywhere] [text-wrap:balance]";

const BODY = "leading-[1.6] text-[var(--dusk-mute)]";

function CommandBlock({ label, command }: { label: string; command: string }) {
  return (
    <div className={TERMINAL}>
      <div
        aria-hidden="true"
        className="flex items-center gap-[7px] border-b border-[var(--dusk-line)] px-3.5 py-[11px]"
      >
        <i className="size-2.5 rounded-full bg-[#2c2f46]" />
        <i className="size-2.5 rounded-full bg-[#2c2f46]" />
        <i className="size-2.5 rounded-full bg-[#2c2f46]" />
      </div>
      <div
        aria-label={label}
        className="overflow-x-auto px-4 py-4 focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-[var(--dusk-lav)] sm:px-5"
        role="region"
        // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- scrollable region needs a tab stop (WCAG 2.1.1, axe scrollable-region-focusable)
        tabIndex={0}
      >
        <code
          className="block whitespace-pre font-mono text-[13.5px] leading-8 text-[#e4e4f5]"
          dir="ltr"
        >
          <span
            aria-hidden="true"
            className="me-2 select-none text-[var(--dusk-lav)]"
          >
            $
          </span>
          {command}
        </code>
      </div>
    </div>
  );
}

function FeatureCard({
  icon,
  heading,
  body,
}: {
  icon: ReactNode;
  heading: string;
  body: string;
}) {
  return (
    <li className={`${GLASS} p-[26px] text-start`}>
      <span aria-hidden="true" className={ICON_TILE}>
        {icon}
      </span>
      <h3 className="mt-5 text-[19px] font-semibold leading-snug text-white">
        {heading}
      </h3>
      <p className="mt-2 text-[14.5px] leading-[1.55] text-[var(--dusk-mute)]">
        {body}
      </p>
    </li>
  );
}

const FAQ_KEYS = ["Wispr", "Distro", "Offline", "Wayland"] as const;

export default function LinuxPageContent() {
  const t = useTranslations("linuxPage");

  return (
    <DuskPage>
      <PageHero eyebrow={t("eyebrow")} lead={t("lead")} title={t("heading")}>
        <div className="flex flex-col items-center gap-3">
          <a className={`${BTN_WHITE} ${BTN_LG}`} href={LINUX_LATEST.deb}>
            <Download aria-hidden="true" />
            {t("downloadCta", { version: LINUX_LATEST.version })}
          </a>
          <p className="text-sm text-white/[.75]">{t("requirementsShort")}</p>
          <a
            className={LINK_LAV}
            href={LINUX_LATEST.releasePage}
            rel="noopener noreferrer"
            target="_blank"
          >
            {t("releaseNotes")}
          </a>
        </div>
      </PageHero>

      <section className={SECTION}>
        <div className={WRAP}>
          {/* Install and requirements, lifted onto the bottom of the sky band.
              Above the fold, so a CSS entrance that plays from the server HTML. */}
          <div
            className="dusk-enter relative z-[3] -mt-[30px] grid gap-4 md:grid-cols-[1.35fr_1fr] md:gap-[18px]"
            style={delay(320)}
          >
            <div
              className={`${GLASS} min-w-0 p-6 text-start backdrop-blur-[14px] md:p-9`}
            >
              <div className="flex items-center gap-3">
                <span aria-hidden="true" className={ICON_TILE}>
                  <Terminal />
                </span>
                <h2 className="text-xl font-semibold tracking-[-0.02em] text-white md:text-2xl">
                  {t("installHeading")}
                </h2>
              </div>
              <p className={`mt-3 ${BODY}`}>{t("installBody")}</p>
              <div className="mt-5">
                <CommandBlock
                  command={LINUX_INSTALL_COMMAND}
                  label={t("installCommandLabel")}
                />
              </div>
              <h3 className="mt-7 text-base font-semibold text-white">
                {t("cudaHeading")}
              </h3>
              <p className={`mt-1.5 text-[14.5px] ${BODY}`}>{t("cudaBody")}</p>
            </div>

            <div
              className={`${GLASS} min-w-0 p-6 text-start backdrop-blur-[14px] md:p-9`}
            >
              <h2 className="text-xl font-semibold tracking-[-0.02em] text-white md:text-2xl">
                {t("requirementsHeading")}
              </h2>
              <ul className="mt-4 space-y-3 text-[15px] leading-[1.55] text-[#d6d8ea]">
                {(
                  [
                    "requirementArch",
                    "requirementDistro",
                    "requirementDesktop",
                  ] as const
                ).map((key) => (
                  <li key={key} className="flex gap-3">
                    <span
                      aria-hidden="true"
                      className="mt-[9px] size-1.5 shrink-0 rounded-full bg-[var(--dusk-lav)]"
                    />
                    <span>{t(key)}</span>
                  </li>
                ))}
              </ul>
            </div>
          </div>

          {/* Features */}
          <h2 className={`mt-20 text-center ${H2_PAGE}`}>
            {t("featuresHeading")}
          </h2>
          <ul className="mt-8 grid gap-4 sm:grid-cols-2">
            <FeatureCard
              body={t("localBody")}
              heading={t("localHeading")}
              icon={<Cpu />}
            />
            <FeatureCard
              body={t("byokBody")}
              heading={t("byokHeading")}
              icon={<Cloud />}
            />
            <FeatureCard
              body={t("languagesBody")}
              heading={t("languagesHeading")}
              icon={<Globe />}
            />
            <FeatureCard
              body={t("openSourceBody")}
              heading={t("openSourceHeading")}
              icon={<Github />}
            />
          </ul>

          {/* Global shortcuts: the highlighted panel. */}
          <div
            className={`${GLASS_ON} mt-4 grid items-center gap-6 p-6 text-start md:grid-cols-[1fr_1fr] md:gap-10 md:p-9`}
          >
            <div className="min-w-0">
              <div className="flex items-center gap-3">
                <span aria-hidden="true" className={ICON_TILE}>
                  <Keyboard />
                </span>
                <h2 className="text-xl font-semibold tracking-[-0.02em] text-white md:text-2xl">
                  {t("hotkeysHeading")}
                </h2>
              </div>
              <p className={`mt-3 ${BODY}`}>{t("hotkeysBody")}</p>
            </div>
            <div className="min-w-0">
              <CommandBlock
                command={LINUX_INPUT_GROUP_COMMAND}
                label={t("hotkeysCommandLabel")}
              />
              <p className="mt-4 text-sm leading-[1.55] text-[var(--dusk-mute)]">
                {t("hotkeysFallback")}
              </p>
              <p className="mt-2 text-sm leading-[1.55] text-[var(--dusk-mute)]">
                {t("hotkeysDefault")}
              </p>
            </div>
          </div>

          {/* Wispr Flow */}
          <div className="mx-auto mt-20 max-w-3xl text-center">
            <h2 className={H2_PAGE}>{t("compareHeading")}</h2>
            <p className={`mt-4 text-base md:text-lg ${BODY}`}>
              {t("compareBody")}
            </p>
          </div>

          {/* FAQ: native <details>, styled like the homepage FAQ list. */}
          <div className="mx-auto mt-20 max-w-[820px]">
            <h2 className={`text-center ${H2_PAGE}`}>{t("faqHeading")}</h2>
            <div className="mt-8 border-t border-[var(--dusk-line)]">
              {FAQ_KEYS.map((key) => (
                <details
                  key={key}
                  className="group border-b border-[var(--dusk-line)]"
                >
                  <summary className="flex cursor-pointer list-none items-center justify-between gap-4 rounded px-1 py-5 text-start text-base font-medium text-[var(--dusk-ink)] transition-colors hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] md:py-[22px] md:text-[17px] [&::-webkit-details-marker]:hidden">
                    <span>{t(`faq${key}Question`)}</span>
                    <Plus
                      aria-hidden="true"
                      className="size-[18px] flex-none text-[var(--dusk-mute)] transition-[rotate] duration-[250ms] ease-[var(--dusk-ease)] group-open:rotate-45 motion-reduce:transition-none"
                    />
                  </summary>
                  <p className="max-w-[700px] px-1 pb-[22px] text-[15.5px] leading-[1.6] text-[var(--dusk-mute)]">
                    {t(`faq${key}Answer`)}
                  </p>
                </details>
              ))}
            </div>
          </div>

          {/* Docs */}
          <div className="mx-auto mt-20 max-w-3xl text-center">
            <h2 className="text-2xl font-semibold tracking-[-0.02em] text-white md:text-[28px]">
              {t("docsHeading")}
            </h2>
            <p className={`mt-3 ${BODY}`}>{t("docsBody")}</p>
            <a
              className={`${BTN_GLASS} ${BTN_LG} mt-6`}
              href={LINUX_DOCS_URL}
              rel="noopener noreferrer"
              target="_blank"
            >
              {t("docsCta")}
              <ExternalLink aria-hidden="true" />
            </a>
          </div>
        </div>
      </section>
    </DuskPage>
  );
}
