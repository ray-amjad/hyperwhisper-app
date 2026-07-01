"use client";

import { Link as HeroUILink } from "@heroui/link";
import { Divider } from "@heroui/divider";
import { env } from "@env/client.mjs";
import { Github, Linkedin, Twitter } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";

import { Link } from "@/src/i18n/navigation";
import { useDownloadModal } from "@/contexts/DownloadModalContext";

export default function FooterSection() {
  const { openModal } = useDownloadModal();
  const t = useTranslations("footer");
  const locale = useLocale();

  const footerLinks = {
    [t("product")]: [
      { label: t("links.features"), href: "/#features" },
      { label: t("links.pricing"), href: "/#cloud" },
      { label: t("links.download"), href: "#" },
      { label: t("links.roadmap"), href: "https://hyperwhisper.userjot.com/" },
    ],
    [t("resources")]: [
      { label: t("links.helpCenter"), href: "https://hyperwhisper.com/docs" },
      {
        label: t("links.customerPortal"),
        href: "/user",
      },
      { label: t("links.olderVersions"), href: "/older-versions" },
      { label: t("links.blog"), href: "/blog" },
    ],
    [t("company")]: [
      { label: t("links.about"), href: "/" },
      { label: t("links.support"), href: "/support" },
    ],
    [t("legal")]: [
      { label: t("links.privacyPolicy"), href: "/legal/privacy-policy" },
      { label: t("links.termsOfService"), href: "/legal/terms-of-service" },
      { label: t("links.refundPolicy"), href: "/legal/refund-policy" },
      { label: t("links.dataTraining"), href: "https://hyperwhisper.com/docs/data-privacy" },
    ],
  };

  return (
    <footer className="bg-gray-900/50 backdrop-blur-xl border-t border-gray-800">
      <div className="max-w-6xl mx-auto px-6 py-12">
        {/* Main footer content */}
        <div className="grid grid-cols-2 md:grid-cols-5 gap-8 mb-8">
          {/* Brand column */}
          <div className="col-span-2 md:col-span-1">
            <div className="flex items-center gap-2 mb-4">
              <img
                alt="HyperWhisper Logo"
                className="w-10 h-10 rounded-xl"
                src="/icon/64.png"
              />
              <span className="text-xl font-bold text-white">HyperWhisper</span>
            </div>
            <p className="text-sm text-gray-400">{t("tagline")}</p>
          </div>

          {/* Links columns */}
          {Object.entries(footerLinks).map(([category, links]) => (
            <div key={category}>
              <h3 className="font-semibold text-white mb-3">{category}</h3>
              <ul className="space-y-2">
                {/*
                  IMPORTANT: Use native <a> for anchor links (/#features, /#pricing).
                  Do NOT use next-intl Link for hash navigation - it breaks anchor scrolling.
                */}
                {links.map((link) => (
                  <li key={link.label}>
                    {link.label === t("links.download") ? (
                      <button
                        className="text-sm text-gray-400 hover:text-white transition-colors text-left"
                        onClick={openModal}
                      >
                        {link.label}
                      </button>
                    ) : link.href.includes("#") ? (
                      <a
                        className="text-sm text-gray-400 hover:text-white transition-colors"
                        href={link.href.startsWith("/#")
                          ? `/${locale}${link.href.slice(1)}`
                          : link.href}
                      >
                        {link.label}
                      </a>
                    ) : link.href.startsWith("http") ? (
                      <a
                        className="text-sm text-gray-400 hover:text-white transition-colors"
                        href={link.href}
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        {link.label}
                      </a>
                    ) : (
                      <Link
                        className="text-sm text-gray-400 hover:text-white transition-colors"
                        href={link.href}
                      >
                        {link.label}
                      </Link>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>

        <Divider className="bg-gray-800" />

        {/* Bottom footer */}
        <div className="flex flex-col md:flex-row justify-between items-center pt-8 gap-4">
          <p className="text-sm text-gray-400">{t("copyright")}</p>
          <div className="flex gap-6 items-center">
            <HeroUILink
              isExternal
              aria-label={t("linkedinAria")}
              className="text-gray-400 hover:text-white transition-colors"
              href="https://www.linkedin.com/company/hyperwhisper/"
            >
              <Linkedin className="w-5 h-5" />
            </HeroUILink>
            <HeroUILink
              isExternal
              aria-label="Follow HyperWhisper on X (Twitter)"
              className="text-gray-400 hover:text-white transition-colors"
              href="https://x.com/HyperWhisperApp"
            >
              <Twitter className="w-5 h-5" />
            </HeroUILink>
            <HeroUILink
              isExternal
              aria-label="HyperWhisper on GitHub"
              className="text-gray-400 hover:text-white transition-colors"
              href="https://github.com/ray-amjad/hyperwhisper-app"
            >
              <Github className="w-5 h-5" />
            </HeroUILink>
          </div>
        </div>
      </div>
    </footer>
  );
}
