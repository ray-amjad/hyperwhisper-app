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

import { Link as LocaleLink } from "@/src/i18n/navigation";
import { useDownloadModal } from "@/contexts/DownloadModalContext";
import { LanguageSwitcher } from "@/components/language-switcher";

export const Navbar = () => {
  const { openModal } = useDownloadModal();
  const t = useTranslations("navbar");
  const locale = useLocale();

  const navItems = [
    { label: t("features"), href: `/${locale}#features` },
    { label: t("cloud"), href: `/${locale}#cloud` },
    { label: t("faq"), href: `/${locale}#faq` },
  ];

  const navMenuItems = [
    { label: t("features"), href: `/${locale}#features` },
    { label: t("cloud"), href: `/${locale}#cloud` },
    { label: t("faq"), href: `/${locale}#faq` },
    { label: t("support"), href: "/support" },
  ];

  return (
    <HeroUINavbar
      className="bg-black/50 backdrop-blur-xl border-b border-gray-800"
      maxWidth="xl"
      position="static"
    >
      <NavbarContent className="basis-1/5 sm:basis-full" justify="start">
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
        <ul className="flex gap-8">
          {navItems.map((item) => (
            <NavbarItem key={item.href}>
              <a
                className={clsx(
                  linkStyles({ color: "foreground" }),
                  "text-gray-300 hover:text-white transition-colors",
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

      <NavbarContent className="sm:hidden basis-1 pl-4" justify="end">
        <NavbarMenuToggle className="text-gray-400" />
      </NavbarContent>

      <NavbarMenu className="bg-black/95 backdrop-blur-xl">
        <div className="mx-4 mt-2 flex flex-col gap-2">
          {/* Use native <a> for anchor links, LocaleLink for page routes */}
          {navMenuItems.map((item, index) => (
            <NavbarMenuItem key={`${item}-${index}`}>
              {item.href.includes("#") ? (
                <a
                  className="text-gray-300 hover:text-white transition-colors text-lg"
                  href={item.href}
                >
                  {item.label}
                </a>
              ) : (
                <LocaleLink
                  className="text-gray-300 hover:text-white transition-colors text-lg"
                  href={item.href}
                >
                  {item.label}
                </LocaleLink>
              )}
            </NavbarMenuItem>
          ))}
          <NavbarMenuItem className="mt-4 flex gap-2">
            <LanguageSwitcher />
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
