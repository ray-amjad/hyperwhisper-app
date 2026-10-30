import {
  Children,
  isValidElement,
  type ReactElement,
  type ReactNode,
} from "react";
import { useTranslations } from "next-intl";

import LegalToc, { type TocItem } from "./LegalToc";

import { PageHero } from "@/components/landing/DuskPage";
import { WRAP } from "@/components/landing/dusk";
import { SECTION } from "@/components/landing/dusk-pages";

/** The plain text of a heading's children (strings and numbers only). */
function textOf(node: ReactNode): string {
  if (typeof node === "string" || typeof node === "number") return `${node}`;
  if (Array.isArray(node)) return node.map(textOf).join("");
  if (isValidElement<{ children?: ReactNode }>(node))
    return textOf(node.props.children);

  return "";
}

/**
 * Every `<h2 id>` in the document, in order. The table of contents is built
 * from these, so its labels are always the headings' own text and a renamed
 * heading cannot leave a stale entry behind.
 */
function collectHeadings(node: ReactNode, out: TocItem[] = []): TocItem[] {
  Children.forEach(node, (child) => {
    if (!isValidElement<{ id?: string; children?: ReactNode }>(child)) return;
    const element = child as ReactElement<{
      id?: string;
      children?: ReactNode;
    }>;

    if (element.type === "h2" && element.props.id) {
      out.push({ id: element.props.id, label: textOf(element.props.children) });

      return;
    }
    collectHeadings(element.props.children, out);
  });

  return out;
}

/*
  Dusk prose. The typography plugin's colours are overridden through its own
  --tw-prose-* variables (utilities sit in a later layer than .prose, so they
  win). Body links are lavender; a link inside a heading (the refund page's
  closing "HyperWhisper" heading) inherits its heading's colour instead, and
  that rule's extra element selector outranks the blanket link colour.
*/
const PROSE = [
  "prose prose-invert max-w-none",
  "[--tw-prose-body:var(--dusk-mute)] [--tw-prose-headings:var(--dusk-ink)] [--tw-prose-bold:#fff]",
  "[--tw-prose-bullets:var(--dusk-dim)] [--tw-prose-counters:var(--dusk-dim)] [--tw-prose-hr:var(--dusk-line)]",
  "[--tw-prose-links:var(--dusk-lav)] [&_a]:text-[var(--dusk-lav)] [&_a]:decoration-[rgba(196,181,253,.45)] [&_a]:underline-offset-4 [&_a:hover]:text-white [&_:is(h2,h3,h4)_a]:text-inherit",
  "[&_a]:rounded-sm [&_a:focus-visible]:outline-2 [&_a:focus-visible]:outline-offset-2 [&_a:focus-visible]:outline-[var(--dusk-lav)]",
  "[&_h2]:mb-3.5 [&_h2]:mt-11 [&_h2]:text-[24px] md:[&_h2]:text-[28px] [&_h2]:font-semibold [&_h2]:leading-tight [&_h2]:tracking-[-0.8px]",
  "[&_h3]:text-[var(--dusk-ink)] [&_strong]:font-semibold",
  // Keeps a long unbreakable token (the support address, a URL) inside the
  // column on a 320px screen (WCAG 2.1 SC 1.4.10); see the legal layout.
  "[overflow-wrap:anywhere]",
].join(" ");

/**
 * A legal page: the sky band with the title and the "Last Updated" line, then
 * a sticky table of contents beside the document from lg up (a collapsed
 * list above it on smaller screens). Presentation only: every word of the
 * document is the page's own children. The chrome around it is translated:
 * the eyebrow reuses the footer's "Legal" column heading.
 */
export default function LegalDocument({
  title,
  updated,
  children,
}: {
  title: string;
  updated: string;
  children: ReactNode;
}) {
  const headings = collectHeadings(children);
  const t = useTranslations("legal");
  const tFooter = useTranslations("footer");

  return (
    <>
      <PageHero eyebrow={tFooter("legal")} lead={updated} title={title} />
      <section className={SECTION}>
        <div
          className={`${WRAP} lg:grid lg:grid-cols-[260px_minmax(0,1fr)] lg:items-start lg:gap-14`}
        >
          <LegalToc items={headings} label={t("onThisPage")} />
          <article className={`min-w-0 max-w-[720px] ${PROSE}`}>
            {children}
          </article>
        </div>
      </section>
    </>
  );
}
