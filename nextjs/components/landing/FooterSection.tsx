"use client";

import { Github, Linkedin, Twitter } from "lucide-react";
import { useTranslations } from "next-intl";

import { WRAP } from "./dusk";

import { Link } from "@/src/i18n/navigation";
import { useDownloadModal } from "@/contexts/DownloadModalContext";
import { useSiteHref } from "@/src/i18n/use-site-href";

type FooterLink = {
  label: string;
  href: string;
  /** An absolute path that must never get a locale prefix (English-only pages). */
  raw?: boolean;
  /** Opens the download dialog instead of navigating. */
  download?: boolean;
};

const LINK =
  "rounded text-sm text-[var(--dusk-dim)] transition-colors hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";
const ICON_LINK =
  "rounded text-[var(--dusk-dim)] transition-colors hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";

export default function FooterSection() {
  const { openModal } = useDownloadModal();
  const t = useTranslations("footer");
  /*
    #1170: on an English-only page (/en/latency, /en/choosing-a-model,
    /en/blog) a locale link would point at /en/..., an ordinary page that
    writes NEXT_LOCALE=en. There, after hydration, every site link is a native
    <a> that follows the saved NEXT_LOCALE, and stays /en with none
    (see siteHref).
  */
  const { nativeLinks, siteHref } = useSiteHref();

  const columns: { title: string; links: FooterLink[] }[] = [
    {
      title: t("product"),
      links: [
        { label: t("links.features"), href: "/#features" },
        { label: t("links.pricing"), href: "/#cloud" },
        { label: t("links.download"), href: "#", download: true },
        {
          label: t("links.roadmap"),
          href: "https://hyperwhisper.userjot.com/",
        },
      ],
    },
    {
      title: t("resources"),
      links: [
        { label: t("links.helpCenter"), href: "https://hyperwhisper.com/docs" },
        /*
          English-only pages: /<locale>/... 404s on every other locale, so the
          href is the absolute /en/... with a native <a>, and the label is the
          same hardcoded English the navbar uses. The blog keeps its translated
          label (its page is English either way).
        */
        { label: "Choose a model", href: "/en/choosing-a-model", raw: true },
        { label: "Latency", href: "/en/latency", raw: true },
        { label: t("links.blog"), href: "/en/blog", raw: true },
        { label: t("links.customerPortal"), href: "/user" },
        { label: t("links.olderVersions"), href: "/older-versions" },
        { label: t("links.openSource"), href: "/open-source" },
      ],
    },
    {
      title: t("company"),
      links: [
        { label: t("links.about"), href: "/" },
        { label: t("links.support"), href: "/support" },
        {
          label: "GitHub",
          href: "https://github.com/ray-amjad/hyperwhisper-app",
        },
      ],
    },
    {
      title: t("legal"),
      links: [
        { label: t("links.privacyPolicy"), href: "/legal/privacy-policy" },
        { label: t("links.termsOfService"), href: "/legal/terms-of-service" },
        { label: t("links.refundPolicy"), href: "/legal/refund-policy" },
        {
          label: t("links.dataTraining"),
          href: "https://hyperwhisper.com/docs/data-privacy",
        },
      ],
    },
  ];

  /*
    IMPORTANT: native <a> for anchor links (/#features, /#cloud). next-intl's
    Link uses client-side routing, which breaks anchor scrolling.
  */
  const renderLink = (link: FooterLink) => {
    if (link.download) {
      return (
        <button
          className={`${LINK} text-start`}
          type="button"
          onClick={openModal}
        >
          {link.label}
        </button>
      );
    }
    if (link.href.startsWith("http")) {
      return (
        <a
          className={LINK}
          href={link.href}
          rel="noopener noreferrer"
          target="_blank"
        >
          {link.label}
        </a>
      );
    }
    if (link.raw) {
      return (
        <a className={LINK} href={link.href}>
          {link.label}
        </a>
      );
    }
    if (link.href.includes("#") || nativeLinks) {
      return (
        <a className={LINK} href={siteHref(link.href)}>
          {link.label}
        </a>
      );
    }

    return (
      <Link className={LINK} href={link.href}>
        {link.label}
      </Link>
    );
  };

  return (
    <footer className="border-t border-[var(--dusk-line)] bg-[#04050b] pb-10 pt-16 text-start">
      <div className={WRAP}>
        <div className="grid grid-cols-2 gap-x-6 gap-y-10 md:grid-cols-[1.6fr_repeat(4,1fr)] md:gap-8">
          <div className="col-span-2 md:col-span-1">
            <div className="flex items-center gap-2.5 text-[17px] font-semibold text-white">
              <img
                alt=""
                className="size-[30px] rounded-lg"
                height={30}
                src="/icon/128.png"
                width={30}
              />
              HyperWhisper
            </div>
            <p className="mt-3.5 max-w-[260px] text-sm leading-normal text-[var(--dusk-dim)]">
              {t("tagline")}
            </p>
          </div>

          {columns.map((column) => (
            <div key={column.title} className="min-w-0">
              <h3 className="mb-3.5 text-[13px] font-semibold text-white">
                {column.title}
              </h3>
              <ul className="grid gap-2.5 break-words hyphens-auto">
                {column.links.map((link) => (
                  <li key={link.href + link.label}>{renderLink(link)}</li>
                ))}
              </ul>
            </div>
          ))}
        </div>

        <div className="mt-12 flex flex-col gap-4 border-t border-[var(--dusk-line)] pt-6 text-[13px] text-[var(--dusk-dim)] md:flex-row md:items-center md:justify-between">
          <p>{t("copyright")}</p>
          <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
            <p>{t("madeBy")}</p>
            <div className="flex items-center gap-5">
              <a
                aria-label={t("linkedinAria")}
                className={ICON_LINK}
                href="https://www.linkedin.com/company/hyperwhisper/"
                rel="noopener noreferrer"
                target="_blank"
              >
                <Linkedin aria-hidden="true" className="size-[18px]" />
              </a>
              <a
                aria-label={t("xAria")}
                className={ICON_LINK}
                href="https://x.com/HyperWhisperApp"
                rel="noopener noreferrer"
                target="_blank"
              >
                <Twitter aria-hidden="true" className="size-[18px]" />
              </a>
              <a
                aria-label={t("githubAria")}
                className={ICON_LINK}
                href="https://github.com/ray-amjad/hyperwhisper-app"
                rel="noopener noreferrer"
                target="_blank"
              >
                <Github aria-hidden="true" className="size-[18px]" />
              </a>
            </div>
          </div>
        </div>
      </div>
    </footer>
  );
}
