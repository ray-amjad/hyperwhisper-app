"use client";

import {
  Dropdown,
  DropdownTrigger,
  DropdownMenu,
  DropdownItem,
} from "@heroui/dropdown";
import { Button } from "@heroui/button";
import { Globe } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { useLayoutEffect, useRef, useState, useTransition } from "react";

import { usePathname, useRouter } from "@/src/i18n/navigation";
import { localeLabels, locales } from "@/src/i18n/locales";
import { isEnglishOnlyRoute } from "@/src/i18n/english-only";

/*
  The popover's room in px around the list: the popover offset plus the
  padding of the content and menu wrappers. React Aria caps the outer popover
  at the space left in the viewport, but nothing inside honours that cap, so
  the list itself has to be held to it.
*/
const POPOVER_CHROME = 32;
const LIST_CLASS = "language-switcher-list";

/**
 * Where a pick of `newLocale` goes, as the href for next-intl's
 * `router.replace(href, { locale: newLocale })`, or null for no navigation.
 * `pathname` is next-intl's usePathname() (locale stripped), `hash` is
 * window.location.hash.
 */
export function languageSwitchHref(
  newLocale: string,
  currentLocale: string,
  pathname: string,
  hash: string,
): string | null {
  // /latency, /choosing-a-model and /blog 404 in every locale but "en", so
  // a switch from one of them goes to the new locale's homepage instead.
  // That includes English (#2939): useLocale() is always "en" there, but the
  // visitor's saved NEXT_LOCALE may be "de" (the proxy never writes the cookie
  // on these pages, #1135), and it still drives the navbar links and the bare
  // "/" redirect. /en is an ordinary page whose middleware writes
  // NEXT_LOCALE=en, so picking English there is how the visitor saves it.
  if (isEnglishOnlyRoute(pathname)) return "/";
  if (newLocale === currentLocale) return null;

  // Elsewhere the #section is kept: /en#cloud switches to /ja#cloud.
  return `${pathname}${hash}`;
}

export const LanguageSwitcher = () => {
  const locale = useLocale();
  const router = useRouter();
  const pathname = usePathname();
  const t = useTranslations("navbar.language");
  const [isPending, startTransition] = useTransition();
  const triggerRef = useRef<HTMLButtonElement>(null);
  const localeOptions = locales;

  const handleLanguageChange = (newLocale: string) => {
    const href = languageSwitchHref(
      newLocale,
      locale,
      pathname,
      window.location.hash,
    );

    if (href === null) return;

    startTransition(() => {
      router.replace(href, { locale: newLocale });
    });
  };

  /*
    In the phone menu the globe sits halfway down the screen, and a list held
    only to 70vh ran past the bottom edge, so the last languages could not be
    reached (or tapped). Hold it to the larger of the two gaps around the
    trigger, the side React Aria opens it on.

    The height goes on the open list itself, never on <html>: a style change on
    the root made WebKit re-lay out the fixed navbar 8px wider while the phone
    menu's scroll lock was on, and HeroUI closes the menu on any navbar width
    change, so the globe tap shut the menu instead of opening the list.
  */
  const [isOpen, setIsOpen] = useState(false);

  useLayoutEffect(() => {
    const trigger = triggerRef.current;
    const list = document.querySelector<HTMLElement>(`.${LIST_CLASS}`);

    if (!isOpen || !trigger || !list) return;
    const { top, bottom } = trigger.getBoundingClientRect();
    const room = Math.max(top, window.innerHeight - bottom) - POPOVER_CHROME;

    list.style.maxHeight = `min(70vh, ${Math.max(room, 96)}px)`;
  }, [isOpen]);

  return (
    <Dropdown
      shouldBlockScroll={false}
      classNames={{
        content: "bg-[#0a0c1d] border border-[var(--dusk-line)] min-w-[8rem]",
      }}
      isOpen={isOpen}
      onOpenChange={setIsOpen}
    >
      <DropdownTrigger>
        <Button
          ref={triggerRef}
          isIconOnly
          className="bg-transparent hover:bg-white/[.07] text-[var(--dusk-mute)] hover:text-white transition-colors"
          isDisabled={isPending}
          size="sm"
          title={t("switchTo")}
          variant="flat"
        >
          <Globe className="w-4 h-4" />
        </Button>
      </DropdownTrigger>
      <DropdownMenu
        aria-label={t("switchTo")}
        classNames={{
          list: `${LIST_CLASS} max-h-[70vh] overflow-y-auto overscroll-contain`,
        }}
        selectedKeys={[locale]}
        selectionMode="single"
        /*
          onAction, not onSelectionChange (#2939): React Aria toggles off an
          item that is already selected, so picking the current language gave
          an empty selection and the handler never heard "en". onAction fires
          on every press, and selectedKeys stays controlled for the check mark.
        */
        onAction={(key) => handleLanguageChange(String(key))}
      >
        {localeOptions.map((option) => (
          <DropdownItem
            key={option}
            className={
              locale === option
                ? "bg-white/[.08] text-white"
                : "text-[var(--dusk-mute)] hover:bg-white/[.06] hover:text-white"
            }
          >
            {localeLabels[option]}
          </DropdownItem>
        ))}
      </DropdownMenu>
    </Dropdown>
  );
};
