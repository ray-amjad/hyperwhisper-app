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
import { useTransition } from "react";

import { usePathname, useRouter } from "@/src/i18n/navigation";
import { localeLabels, locales } from "@/src/i18n/locales";

export const LanguageSwitcher = () => {
  const locale = useLocale();
  const router = useRouter();
  const pathname = usePathname();
  const t = useTranslations("navbar.language");
  const [isPending, startTransition] = useTransition();
  const localeOptions = locales;

  const handleLanguageChange = (newLocale: string) => {
    if (newLocale === locale) return;
    startTransition(() => {
      router.replace(pathname, { locale: newLocale });
    });
  };

  return (
    <Dropdown
      shouldBlockScroll={false}
      classNames={{
        content: "bg-[#0a0c1d] border border-[var(--dusk-line)] min-w-[8rem]",
      }}
    >
      <DropdownTrigger>
        <Button
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
          list: "max-h-[70vh] overflow-y-auto overscroll-contain",
        }}
        selectedKeys={[locale]}
        selectionMode="single"
        onSelectionChange={(keys) => {
          const selected = Array.from(keys)[0];
          if (typeof selected === "string") handleLanguageChange(selected);
        }}
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
