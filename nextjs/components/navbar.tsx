"use client";

import {
  Navbar as HeroUINavbar,
  NavbarContent,
  NavbarMenu,
  NavbarMenuToggle,
  NavbarBrand,
  NavbarItem,
  NavbarMenuItem,
} from "@heroui/navbar";
import { Button } from "@heroui/button";
import { link as linkStyles } from "@heroui/theme";
import clsx from "clsx";
import { Link as HeroUILink } from "@heroui/link";
import { Download, Github } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";

import { Link as LocaleLink } from "@/src/i18n/navigation";
import { useDownloadModal } from "@/contexts/DownloadModalContext";
import { LanguageSwitcher } from "@/components/language-switcher";

export const Navbar = () => {
  const { openModal } = useDownloadModal();
  const t = useTranslations("navbar");
  const locale = useLocale();
  /*
    The menu is controlled here because nothing else closes it. HeroUI closes it
    only from the toggle and from its own resize observer, so a tap on a menu
    item left the full-screen overlay up — and HeroUI's usePreventScroll keeps
    document.documentElement at overflow:hidden for as long as it is open.
  */
  const [isMenuOpen, setIsMenuOpen] = useState(false);
  const closeMenu = () => setIsMenuOpen(false);

  /*
    The latency and model-chooser pages are English-only — they 404 on every
    other locale — so their hrefs are the absolute /en/... and they are rendered
    with a native <a>, never LocaleLink (which would prefix them into
    /ja/en/latency). Labels are hardcoded for the same reason: the page each one
    opens is English either way.
  */
  const latencyItem = { label: "Latency", href: "/en/latency", raw: true };
  const chooseModelItem = {
    label: "Choose a model",
    href: "/en/choosing-a-model",
    raw: true,
  };

  const navItems = [
    { label: t("features"), href: `/${locale}#features` },
    { label: t("cloud"), href: `/${locale}#cloud` },
    chooseModelItem,
    latencyItem,
    { label: t("faq"), href: `/${locale}#faq` },
  ];

  const navMenuItems = [
    { label: t("features"), href: `/${locale}#features` },
    { label: t("cloud"), href: `/${locale}#cloud` },
    chooseModelItem,
    latencyItem,
    { label: t("faq"), href: `/${locale}#faq` },
    { label: t("support"), href: "/support" },
  ];

  return (
    <HeroUINavbar
      className="bg-black/50 backdrop-blur-xl border-b border-gray-800"
      /*
        HeroUI's navbar wrapper is `flex-nowrap` with a fixed `h-[var(--navbar-height)]`, so at a
        200% text resize nothing wraps and nothing shrinks: the bar overflowed the viewport by
        277px, the word mark collided with the first link, and the Download button was pushed off
        the screen (WCAG 2.1 SC 1.4.4). Letting the wrapper wrap and grow keeps every control on
        screen. At the normal text size the content still fits on one row, so the bar is unchanged.
      */
      classNames={{
        base: "h-auto",
        // `min-h` keeps the normal-size bar at its usual --navbar-height; `h-auto` alone
        // shrank it to its content and made the bar 16px shorter on every page.
        wrapper: "flex-wrap h-auto min-h-[var(--navbar-height)] gap-y-2 py-2",
      }}
      isMenuOpen={isMenuOpen}
      maxWidth="xl"
      position="static"
      onMenuOpenChange={setIsMenuOpen}
    >
      {/*
        HeroUI gives this content `flex-basis: 0`, so the word mark's box could end up narrower
        than the word mark itself and the text was painted over the first nav link at a large
        text size. `min-w-fit` stops the box shrinking below its own content; it changes nothing
        at the normal text size, where the box is already wider than the text.
      */}
      <NavbarContent
        className="basis-1/5 sm:basis-full min-w-fit"
        justify="start"
      >
        <NavbarBrand as="li" className="gap-3 max-w-fit">
          <LocaleLink
            className="flex justify-start items-center gap-2"
            href="/"
          >
            <img
              alt="HyperWhisper Logo"
              className="w-8 h-8 rounded-lg"
              src="/icon/32.png"
            />
            <p className="font-bold text-white">HyperWhisper</p>
          </LocaleLink>
        </NavbarBrand>
      </NavbarContent>

      {/*
        IMPORTANT: Use native <a> tags for anchor links (/#features, /#pricing, /#faq).
        The leading "/" ensures navigation to the home page first when on other pages.
        Do NOT use LocaleLink/Link from next-intl for hash navigation.
        next-intl's Link breaks anchor scrolling by using client-side routing.
      */}
      <NavbarContent className="hidden lg:flex" justify="center">
        <ul className="flex flex-wrap gap-8">
          {navItems.map((item) => (
            <NavbarItem key={item.href}>
              <a
                className={clsx(
                  linkStyles({ color: "foreground" }),
                  "text-gray-300 hover:text-white transition-colors",
                  /*
                    linkStyles() opens with `outline-solid outline-transparent` and restores the
                    ring only through `data-[focus-visible=true]:`, an attribute the HeroUI <Link>
                    component sets and a native <a> never gets. Without these 3 utilities the link
                    has no keyboard focus ring at all. `outline-focus` is HeroUI's own token, so
                    this is the same ring the Download button and the GitHub link already draw.
                  */
                  "rounded focus-visible:outline-2 focus-visible:outline-focus focus-visible:outline-offset-2",
                )}
                href={item.href}
              >
                {item.label}
              </a>
            </NavbarItem>
          ))}
        </ul>
      </NavbarContent>

      <NavbarContent
        className="hidden sm:flex basis-1/5 sm:basis-full"
        justify="end"
      >
        <NavbarItem className="hidden sm:flex">
          <LanguageSwitcher />
        </NavbarItem>
        <NavbarItem className="hidden sm:flex">
          <HeroUILink
            isExternal
            aria-label={t("githubAria")}
            className="text-gray-400 hover:text-white transition-colors"
            href="https://github.com/ray-amjad/hyperwhisper-app"
          >
            <Github className="w-5 h-5" />
          </HeroUILink>
        </NavbarItem>
        <NavbarItem className="hidden md:flex">
          <Button
            className="bg-gradient-to-r from-purple-600 to-blue-600 text-white font-semibold"
            size="sm"
            startContent={<Download className="w-4 h-4" />}
            variant="flat"
            onClick={openModal}
          >
            {t("download")}
          </Button>
        </NavbarItem>
      </NavbarContent>

      <NavbarContent className="lg:hidden pl-4 grow-0!" justify="end">
        <NavbarMenuToggle className="text-gray-400" />
      </NavbarContent>

      <NavbarMenu className="bg-black/95 backdrop-blur-xl">
        <div className="mx-4 mt-2 flex flex-col gap-2">
          {/* Use native <a> for anchor links and already-localed hrefs, LocaleLink for page routes */}
          {navMenuItems.map((item, index) => (
            <NavbarMenuItem key={`${item}-${index}`}>
              {item.href.includes("#") || "raw" in item ? (
                <a
                  className="text-gray-300 hover:text-white transition-colors text-lg"
                  href={item.href}
                  onClick={closeMenu}
                >
                  {item.label}
                </a>
              ) : (
                <LocaleLink
                  className="text-gray-300 hover:text-white transition-colors text-lg"
                  href={item.href}
                  onClick={closeMenu}
                >
                  {item.label}
                </LocaleLink>
              )}
            </NavbarMenuItem>
          ))}
          {/*
            The `hidden sm:flex` header end-cluster above and this menu stay on
            screen together from 640px up, so every control in this row would
            otherwise be drawn twice. Mirror that cluster's own breakpoints: it
            shows the language switcher from sm (640) and Download from md (768),
            so hide the switcher from sm and the whole row from md, at which point
            both of its children are duplicates.
          */}
          <NavbarMenuItem className="mt-4 flex gap-2 md:hidden">
            <span className="contents sm:hidden">
              <LanguageSwitcher />
            </span>
            <Button
              className="flex-1 bg-gradient-to-r from-purple-600 to-blue-600 text-white font-semibold"
              startContent={<Download className="w-4 h-4" />}
              variant="flat"
              onClick={openModal}
            >
              {t("download")}
            </Button>
          </NavbarMenuItem>
        </div>
      </NavbarMenu>
    </HeroUINavbar>
  );
};
