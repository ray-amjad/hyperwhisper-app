import type { ReactNode } from "react";

import { Github } from "lucide-react";
import { useTranslations } from "next-intl";

import { BTN_WHITE, WRAP } from "@/components/landing/dusk";
import { GLASS, SECTION } from "@/components/landing/dusk-pages";
import { DuskPage, PageHero } from "@/components/landing/DuskPage";
import Reveal from "@/components/landing/Reveal";

// The /open-source page body (#1059). It reads the `openSource` namespace with
// useTranslations, which works in a server component and under a
// NextIntlClientProvider alike, so a test can render it in every locale.

export const GITHUB_URL = "https://github.com/ray-amjad/hyperwhisper-app";
export const LICENSE_URL =
  "https://github.com/ray-amjad/hyperwhisper-app/blob/main/LICENSE";
export const SUPPORT_EMAIL = "hi@support.hyperwhisper.com";
// The subjects stay English on purpose: they reach the support inbox, not the
// reader, and one wording keeps those emails easy to filter.
export const REFUND_MAILTO = `mailto:${SUPPORT_EMAIL}?subject=${encodeURIComponent(
  "HyperWhisper — refund request",
)}`;
export const CREDITS_MAILTO = `mailto:${SUPPORT_EMAIL}?subject=${encodeURIComponent(
  "HyperWhisper — convert my license to Cloud credits",
)}`;

/** Inline links in the copy: lavender, underlined on hover, a visible focus ring. */
const PROSE_LINKS =
  "[&_a]:rounded [&_a]:text-[var(--dusk-lav)] [&_a]:underline [&_a]:decoration-[rgba(196,181,253,.4)] [&_a]:underline-offset-4 [&_a]:transition-colors [&_a:hover]:text-white [&_a:focus-visible]:outline-2 [&_a:focus-visible]:outline-offset-2 [&_a:focus-visible]:outline-[var(--dusk-lav)]";

const externalLink = (href: string) => {
  const ExternalLink = (chunks: ReactNode) => (
    <a href={href} rel="noopener noreferrer" target="_blank">
      {chunks}
    </a>
  );

  return ExternalLink;
};

const mailLink = (href: string) => {
  const MailLink = (chunks: ReactNode) => <a href={href}>{chunks}</a>;

  return MailLink;
};

const strong = (chunks: ReactNode) => <strong>{chunks}</strong>;

const WHY = ["whyVerify", "whyNoLockIn", "whyFork", "whyOpen"] as const;

export default function OpenSourceContent() {
  const t = useTranslations("openSource");

  return (
    <DuskPage>
      <PageHero
        eyebrow={t("eyebrow")}
        lead={t("lead")}
        sky="night"
        title={t("heading")}
      />

      <section className={SECTION}>
        <div className={WRAP}>
          {/* The repository bar, lifted onto the bottom of the sky band. */}
          <Reveal
            className={`${GLASS} relative z-[3] -mt-[30px] flex flex-col gap-5 p-6 backdrop-blur-[14px] md:flex-row md:items-center md:justify-between md:px-7`}
          >
            <div className="flex min-w-0 items-center gap-4">
              <span
                aria-hidden="true"
                className="grid size-11 shrink-0 place-items-center rounded-full bg-white text-[#0b0b14]"
              >
                <Github className="size-6" />
              </span>
              <div className="min-w-0 text-start">
                <p className="font-mono text-base font-medium text-white [overflow-wrap:anywhere] md:text-lg">
                  ray-amjad/hyperwhisper-app
                </p>
                <p className="text-sm text-[var(--dusk-mute)]">
                  macOS · Windows · Linux · Cloud
                </p>
              </div>
            </div>
            <div className="flex flex-wrap items-center gap-4">
              <span className="rounded-full border border-[rgba(196,181,253,.3)] px-3 py-1.5 font-mono text-[13px] text-[var(--dusk-lav)]">
                Apache-2.0
              </span>
              <a
                className={`${BTN_WHITE} px-[18px] py-[11px] text-sm`}
                href={GITHUB_URL}
                rel="noopener noreferrer"
                target="_blank"
              >
                <Github aria-hidden="true" />
                {t("cta")}
              </a>
            </div>
          </Reveal>

          <Reveal
            as="p"
            className={`mx-auto mt-14 max-w-3xl text-center text-base leading-[1.7] text-[var(--dusk-mute)] md:text-lg ${PROSE_LINKS}`}
          >
            {t.rich("intro", {
              github: externalLink(GITHUB_URL),
              license: externalLink(LICENSE_URL),
            })}
          </Reveal>

          <h2 className="mt-16 text-center text-2xl font-semibold tracking-[-0.02em] text-white md:text-[32px]">
            {t("whyHeading")}
          </h2>
          <ul className="mt-8 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {WHY.map((key, i) => (
              <Reveal
                key={key}
                as="li"
                className={`${GLASS} p-[26px] text-start lg:min-h-[200px]`}
                delay={i * 100}
              >
                <span
                  aria-hidden="true"
                  className="font-mono text-[13px] text-[var(--dusk-lav)]"
                >
                  {String(i + 1).padStart(2, "0")}
                </span>
                {/* The <strong> lead-in reads as the card's title. */}
                <p className="mt-6 text-[14.5px] leading-[1.55] text-[var(--dusk-mute)] lg:mt-10 [&_strong]:mb-2 [&_strong]:block [&_strong]:text-[19px] [&_strong]:font-semibold [&_strong]:leading-snug [&_strong]:text-white">
                  {t.rich(key, { strong })}
                </p>
              </Reveal>
            ))}
          </ul>

          {/* Licence holders: the peach card. */}
          <Reveal
            className={`mt-4 rounded-[22px] border border-[rgba(253,186,140,.25)] bg-[radial-gradient(60%_120%_at_0%_0%,rgba(253,186,140,.12),transparent_60%),#0b0c18] p-6 text-start md:p-8 ${PROSE_LINKS}`}
          >
            <h2 className="text-2xl font-semibold tracking-[-0.02em] text-white md:text-[26px]">
              {t("licenseHeading")}
            </h2>
            <p className="mt-3 max-w-3xl leading-[1.65] text-[var(--dusk-mute)]">
              {t.rich("licenseIntro", {
                email: SUPPORT_EMAIL,
                mail: mailLink(`mailto:${SUPPORT_EMAIL}`),
              })}
            </p>
            <ul className="mt-5 grid gap-3 md:grid-cols-2">
              <li className="rounded-2xl border border-[var(--dusk-line)] bg-white/[.025] p-5 leading-[1.6] text-[#d6d8ea] [&_strong]:text-[var(--dusk-peach)]">
                {t.rich("licenseRecent", {
                  strong,
                  refund: mailLink(REFUND_MAILTO),
                })}
              </li>
              <li className="rounded-2xl border border-[var(--dusk-line)] bg-white/[.025] p-5 leading-[1.6] text-[#d6d8ea] [&_strong]:text-[var(--dusk-peach)]">
                {t.rich("licenseOlder", {
                  strong,
                  credits: mailLink(CREDITS_MAILTO),
                })}
              </li>
            </ul>
          </Reveal>

          <Reveal
            className={`mx-auto mt-16 max-w-3xl text-center ${PROSE_LINKS}`}
          >
            <h2 className="text-2xl font-semibold tracking-[-0.02em] text-white md:text-[28px]">
              {t("contributeHeading")}
            </h2>
            <p className="mt-3 leading-[1.65] text-[var(--dusk-mute)]">
              {t.rich("contributeBody", { github: externalLink(GITHUB_URL) })}
            </p>
          </Reveal>
        </div>
      </section>
    </DuskPage>
  );
}
