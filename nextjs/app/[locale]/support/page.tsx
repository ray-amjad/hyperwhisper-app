"use client";

import { ArrowRight, Clock, Mail } from "lucide-react";
import { useTranslations } from "next-intl";

import { BTN_GLASS, BTN_WHITE, WRAP } from "@/components/landing/dusk";
import { GLASS, LABEL, SECTION } from "@/components/landing/dusk-pages";
import { DuskPage, PageHero, delay } from "@/components/landing/DuskPage";
import { GmailIcon, OutlookIcon } from "@/components/landing/MailClientIcons";
import { Link } from "@/src/i18n/navigation";

const BTN_SIZE = "px-5 py-3 text-[15px]";

/** One row of the right-hand column: a title, a hint and an arrow. */
const SIDE_ITEM = `${GLASS} group relative block py-5 pe-14 ps-6 text-start backdrop-blur-[14px] transition-colors duration-200 hover:border-[rgba(196,181,253,.28)] hover:bg-white/[.06] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]`;

function SideItemBody({ title, hint }: { title: string; hint: string }) {
  return (
    <>
      <span className="block text-base font-semibold text-white">{title}</span>
      <span className="block text-sm text-[var(--dusk-mute)]">{hint}</span>
      <ArrowRight
        aria-hidden="true"
        className="absolute end-6 top-1/2 size-4 -translate-y-1/2 text-[var(--dusk-lav)] transition-transform duration-200 group-hover:translate-x-0.5 motion-reduce:transition-none rtl:-scale-x-100 rtl:group-hover:-translate-x-0.5"
      />
    </>
  );
}

export default function SupportPage() {
  const t = useTranslations("support");
  const tFooter = useTranslations("footer");
  const emailAddress = "hi@support.hyperwhisper.com";
  const subject = t("emailTemplate.subject");
  const body = t("emailTemplate.body");

  const mailtoLink = `mailto:${emailAddress}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;

  return (
    <DuskPage>
      <PageHero lead={t("subtitle")} sky="night" title={t("title")} />

      <section className={SECTION}>
        <div
          className={`${WRAP} relative z-[3] -mt-[30px] grid gap-[18px] lg:grid-cols-[1.3fr_1fr]`}
        >
          {/* The whole contact block is above the fold, so it enters with
              CSS (played from the server HTML), not a scroll Reveal that
              waits for hydration. */}
          <div
            className={`dusk-enter ${GLASS} p-6 text-start backdrop-blur-[14px] md:p-9`}
            style={delay(320)}
          >
            <span
              aria-hidden="true"
              className="grid size-[38px] place-items-center rounded-[11px] bg-[rgba(196,181,253,.12)] text-[var(--dusk-lav)]"
            >
              <Mail className="size-[19px]" />
            </span>
            <h2 className="mt-5 text-xl font-semibold text-white">
              {t("getInTouch")}
            </h2>
            <p className="mt-2 max-w-xl leading-[1.6] text-[var(--dusk-mute)]">
              {t("description")}
            </p>

            <p className={`${LABEL} mt-7`}>{t("emailUsAt")}</p>
            <p className="mt-2 text-2xl font-semibold tracking-[-0.03em] text-white [overflow-wrap:anywhere] md:text-[32px]">
              {emailAddress}
            </p>

            <p className={`${LABEL} mt-7`}>{t("openInClient")}</p>
            <div className="mt-3 flex flex-col gap-2.5 sm:flex-row sm:flex-wrap">
              {/* Default email client */}
              <a className={`${BTN_WHITE} ${BTN_SIZE}`} href={mailtoLink}>
                <Mail aria-hidden="true" />
                {t("defaultClient")}
              </a>
              {/* Gmail and Outlook: their own glyphs, straight on the glass, no tile */}
              <a
                className={`${BTN_GLASS} ${BTN_SIZE}`}
                href={`https://mail.google.com/mail/?view=cm&fs=1&to=${emailAddress}&su=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`}
                rel="noopener noreferrer"
                target="_blank"
              >
                <GmailIcon />
                {t("gmail")}
              </a>
              <a
                className={`${BTN_GLASS} ${BTN_SIZE}`}
                href={`https://outlook.live.com/mail/0/deeplink/compose?to=${emailAddress}&subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`}
                rel="noopener noreferrer"
                target="_blank"
              >
                <OutlookIcon />
                {t("outlook")}
              </a>
            </div>

            {/* Response time info */}
            <div className="mt-8 flex items-start gap-3 border-t border-[var(--dusk-line)] pt-5 text-[var(--dusk-mute)]">
              <Clock
                aria-hidden="true"
                className="mt-0.5 size-5 shrink-0"
                strokeWidth={1.5}
              />
              <p className="text-sm">{t("responseTime")}</p>
            </div>
          </div>

          <ul className="grid content-start gap-3">
            <li className="dusk-enter" style={delay(420)}>
              <a className={SIDE_ITEM} href="https://hyperwhisper.com/docs">
                <SideItemBody
                  hint={t("helpCenterHint")}
                  title={tFooter("links.helpCenter")}
                />
              </a>
            </li>
            <li className="dusk-enter" style={delay(520)}>
              {/*
                English-only page: the absolute /en/... with a native <a> and the
                same English label the navbar and footer use (#1170).
              */}
              <a className={SIDE_ITEM} href="/en/choosing-a-model">
                <SideItemBody
                  hint={t("chooseModelHint")}
                  title="Choose a model"
                />
              </a>
            </li>
            <li className="dusk-enter" style={delay(620)}>
              <Link className={SIDE_ITEM} href="/user/sign-in">
                <SideItemBody
                  hint={t("accountHint")}
                  title={tFooter("links.customerPortal")}
                />
              </Link>
            </li>
          </ul>
        </div>
      </section>
    </DuskPage>
  );
}
