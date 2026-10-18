import type { ReactNode } from "react";

import { useTranslations } from "next-intl";

import { PROSE_TEXT_LINK } from "@/components/text-link";

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

export default function OpenSourceContent() {
  const t = useTranslations("openSource");

  return (
    <article className="mx-auto w-full max-w-4xl pt-16 pb-24 md:pt-24">
      {/* Header */}
      <div className="space-y-5 text-center">
        <p className="text-sm uppercase tracking-[0.3em] text-purple-300">
          {t("eyebrow")}
        </p>
        <h1 className="mx-auto max-w-3xl text-4xl font-semibold leading-tight text-white md:text-5xl">
          {t("heading")}
        </h1>
        <p className="mx-auto max-w-2xl text-lg leading-8 text-gray-300">
          {t("lead")}
        </p>
      </div>

      {/* Body */}
      <div
        className={`prose prose-invert mx-auto mt-12 max-w-3xl prose-headings:text-white prose-strong:text-white ${PROSE_TEXT_LINK}`}
      >
        <p>
          {t.rich("intro", {
            github: externalLink(GITHUB_URL),
            license: externalLink(LICENSE_URL),
          })}
        </p>

        <h2>{t("whyHeading")}</h2>
        <p>{t.rich("whyVerify", { strong })}</p>
        <p>{t.rich("whyNoLockIn", { strong })}</p>
        <p>{t.rich("whyFork", { strong })}</p>
        <p>{t.rich("whyOpen", { strong })}</p>

        <h2>{t("licenseHeading")}</h2>
        <p>
          {t.rich("licenseIntro", {
            email: SUPPORT_EMAIL,
            mail: mailLink(`mailto:${SUPPORT_EMAIL}`),
          })}
        </p>
        <ul>
          <li>
            {t.rich("licenseRecent", {
              strong,
              refund: mailLink(REFUND_MAILTO),
            })}
          </li>
          <li>
            {t.rich("licenseOlder", {
              strong,
              credits: mailLink(CREDITS_MAILTO),
            })}
          </li>
        </ul>

        <h2>{t("contributeHeading")}</h2>
        <p>{t.rich("contributeBody", { github: externalLink(GITHUB_URL) })}</p>
      </div>
    </article>
  );
}
