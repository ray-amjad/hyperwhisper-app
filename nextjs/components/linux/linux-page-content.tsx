import type { ReactNode } from "react";

import {
  Cloud,
  Cpu,
  Download,
  ExternalLink,
  Github,
  Globe,
  Keyboard,
  Terminal,
} from "lucide-react";
import { useTranslations } from "next-intl";

import linuxLatest from "@/public/linux-latest.json";

// The /linux landing page body. It reads the `linuxPage` namespace with
// useTranslations, which works in a server component and under a
// NextIntlClientProvider alike, so a test can render it in every locale.
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

const CARD_CLASS_NAME =
  "rounded-2xl border border-gray-800 bg-gray-900/50 p-6 backdrop-blur-xl";
const CTA_CLASS_NAME =
  "inline-flex items-center justify-center gap-2 rounded-xl bg-gradient-to-r from-purple-600 to-blue-600 px-8 py-3 text-lg font-semibold text-white transition-shadow hover:shadow-lg focus-visible:outline-2 focus-visible:outline-focus focus-visible:outline-offset-2";
const SECTION_HEADING_CLASS_NAME =
  "mb-8 text-center text-3xl font-bold text-white md:text-4xl";

function CommandBlock({ label, command }: { label: string; command: string }) {
  return (
    <div
      aria-label={label}
      className="overflow-x-auto rounded-lg border border-gray-700 bg-gray-800/80 p-3 focus-visible:outline-2 focus-visible:outline-focus focus-visible:outline-offset-2"
      role="region"
      // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- scrollable region needs a tab stop (WCAG 2.1.1, axe scrollable-region-focusable)
      tabIndex={0}
    >
      <code className="whitespace-pre font-mono text-sm text-gray-200">
        {command}
      </code>
    </div>
  );
}

function FeatureCard({
  icon,
  gradient,
  heading,
  body,
}: {
  icon: ReactNode;
  gradient: string;
  heading: string;
  body: string;
}) {
  return (
    <div className={`${CARD_CLASS_NAME} h-full`}>
      <div
        className={`mb-4 flex h-12 w-12 items-center justify-center rounded-xl bg-gradient-to-r ${gradient}`}
      >
        {icon}
      </div>
      <h3 className="mb-2 text-xl font-semibold text-white">{heading}</h3>
      <p className="text-sm leading-6 text-gray-400">{body}</p>
    </div>
  );
}

const FAQ_KEYS = ["Wispr", "Distro", "Offline", "Wayland"] as const;

export default function LinuxPageContent() {
  const t = useTranslations("linuxPage");

  return (
    <article className="mx-auto w-full max-w-5xl pt-16 pb-24 md:pt-24">
      {/* Hero */}
      <header className="space-y-6 text-center">
        <p className="text-sm font-semibold uppercase tracking-widest text-purple-400">
          {t("eyebrow")}
        </p>
        <h1 className="mx-auto max-w-3xl bg-gradient-to-r from-white to-gray-400 bg-clip-text text-4xl font-bold leading-tight text-transparent md:text-6xl">
          {t("heading")}
        </h1>
        <p className="mx-auto max-w-2xl text-lg leading-8 text-gray-300">
          {t("lead")}
        </p>
        <div className="flex flex-col items-center gap-3 pt-2">
          <a className={CTA_CLASS_NAME} href={LINUX_LATEST.deb}>
            <Download aria-hidden="true" className="h-5 w-5" />
            {t("downloadCta", { version: LINUX_LATEST.version })}
          </a>
          <p className="text-sm text-gray-400">{t("requirementsShort")}</p>
          <a
            className="text-sm text-gray-400 underline underline-offset-4 transition-colors hover:text-gray-200"
            href={LINUX_LATEST.releasePage}
            rel="noopener noreferrer"
            target="_blank"
          >
            {t("releaseNotes")}
          </a>
        </div>
      </header>

      {/* Install */}
      <section className="mt-20 grid gap-6 md:grid-cols-2">
        <div className={CARD_CLASS_NAME}>
          <div className="mb-4 flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-lg border border-purple-500/30 bg-gradient-to-br from-purple-500/20 to-blue-500/20">
              <Terminal
                aria-hidden="true"
                className="h-5 w-5 text-purple-300"
              />
            </div>
            <h2 className="text-lg font-semibold text-white">
              {t("installHeading")}
            </h2>
          </div>
          <p className="mb-4 text-sm text-gray-400">{t("installBody")}</p>
          <CommandBlock
            command={LINUX_INSTALL_COMMAND}
            label={t("installCommandLabel")}
          />
          <h3 className="mt-6 mb-2 text-base font-semibold text-white">
            {t("cudaHeading")}
          </h3>
          <p className="text-sm text-gray-400">{t("cudaBody")}</p>
        </div>
        <div className={CARD_CLASS_NAME}>
          <h2 className="mb-4 text-lg font-semibold text-white">
            {t("requirementsHeading")}
          </h2>
          <ul className="list-disc space-y-2 ps-5 text-sm text-gray-300">
            <li>{t("requirementArch")}</li>
            <li>{t("requirementDistro")}</li>
            <li>{t("requirementDesktop")}</li>
          </ul>
        </div>
      </section>

      {/* Features */}
      <section className="mt-20">
        <h2 className={SECTION_HEADING_CLASS_NAME}>{t("featuresHeading")}</h2>
        <div className="grid gap-6 md:grid-cols-2">
          <FeatureCard
            body={t("localBody")}
            gradient="from-emerald-500 to-teal-500"
            heading={t("localHeading")}
            icon={<Cpu aria-hidden="true" className="h-6 w-6 text-white" />}
          />
          <FeatureCard
            body={t("byokBody")}
            gradient="from-purple-500 to-blue-500"
            heading={t("byokHeading")}
            icon={<Cloud aria-hidden="true" className="h-6 w-6 text-white" />}
          />
          <FeatureCard
            body={t("languagesBody")}
            gradient="from-orange-500 to-pink-500"
            heading={t("languagesHeading")}
            icon={<Globe aria-hidden="true" className="h-6 w-6 text-white" />}
          />
          <FeatureCard
            body={t("openSourceBody")}
            gradient="from-gray-600 to-gray-800"
            heading={t("openSourceHeading")}
            icon={<Github aria-hidden="true" className="h-6 w-6 text-white" />}
          />
        </div>
      </section>

      {/* Global shortcuts */}
      <section className="mt-20">
        <div className="rounded-2xl border border-purple-800/50 bg-gradient-to-r from-purple-900/20 to-blue-900/20 p-8 backdrop-blur-xl md:p-12">
          <div className="mb-4 flex items-center gap-3">
            <Keyboard aria-hidden="true" className="h-8 w-8 text-purple-400" />
            <h2 className="text-2xl font-bold text-white md:text-3xl">
              {t("hotkeysHeading")}
            </h2>
          </div>
          <p className="mb-6 text-gray-300">{t("hotkeysBody")}</p>
          <CommandBlock
            command={LINUX_INPUT_GROUP_COMMAND}
            label={t("hotkeysCommandLabel")}
          />
          <p className="mt-6 text-sm text-gray-400">{t("hotkeysFallback")}</p>
          <p className="mt-2 text-sm text-gray-400">{t("hotkeysDefault")}</p>
        </div>
      </section>

      {/* Wispr Flow */}
      <section className="mx-auto mt-20 max-w-3xl text-center">
        <h2 className={SECTION_HEADING_CLASS_NAME}>{t("compareHeading")}</h2>
        <p className="text-lg leading-8 text-gray-300">{t("compareBody")}</p>
      </section>

      {/* FAQ */}
      <section className="mx-auto mt-20 max-w-3xl">
        <h2 className={SECTION_HEADING_CLASS_NAME}>{t("faqHeading")}</h2>
        <div className="overflow-hidden rounded-2xl border border-gray-800 bg-gray-900/50 backdrop-blur-xl">
          {FAQ_KEYS.map((key) => (
            <details
              key={key}
              className="group border-b border-gray-800 last:border-b-0"
            >
              <summary className="cursor-pointer list-none px-6 py-4 font-medium text-gray-200 transition-colors hover:bg-gray-800/50 [&::-webkit-details-marker]:hidden">
                {t(`faq${key}Question`)}
              </summary>
              <p className="px-6 pt-2 pb-6 text-gray-400">
                {t(`faq${key}Answer`)}
              </p>
            </details>
          ))}
        </div>
      </section>

      {/* Docs */}
      <section className="mx-auto mt-20 max-w-3xl text-center">
        <h2 className="mb-3 text-2xl font-bold text-white">
          {t("docsHeading")}
        </h2>
        <p className="mb-6 text-gray-400">{t("docsBody")}</p>
        <a
          className="inline-flex items-center gap-2 rounded-xl bg-gray-800 px-6 py-3 font-medium text-gray-200 transition-colors hover:bg-gray-700 focus-visible:outline-2 focus-visible:outline-focus focus-visible:outline-offset-2"
          href={LINUX_DOCS_URL}
          rel="noopener noreferrer"
          target="_blank"
        >
          {t("docsCta")}
          <ExternalLink aria-hidden="true" className="h-4 w-4" />
        </a>
      </section>
    </article>
  );
}
