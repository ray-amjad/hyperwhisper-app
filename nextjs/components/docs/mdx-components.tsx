import type { ComponentPropsWithoutRef, ReactNode } from "react";

import Link from "next/link";
import {
  AlertTriangle,
  BookOpen,
  ChevronRight,
  ClipboardPaste,
  Cloud,
  Cpu,
  Gauge,
  Globe,
  Info as InfoIcon,
  KeyRound,
  Keyboard,
  Lightbulb,
  ListChecks,
  Lock,
  type LucideIcon,
  Plug,
  Scale,
  Server,
  ShieldHalf,
  SlidersHorizontal,
  Sparkles,
  StickyNote,
  Zap,
} from "lucide-react";

import { Tab, Tabs } from "./Tabs";

// The Mintlify components the docs use (tests/docs-content.test.ts fails on
// a tag in src/content/docs with no entry here), styled with the site's Dusk
// tokens (styles/globals.css, "Docs").

// Mintlify takes Font Awesome names; these are the ones the pages use.
const ICONS: Record<string, LucideIcon> = {
  bolt: Zap,
  "book-open": BookOpen,
  cloud: Cloud,
  gauge: Gauge,
  globe: Globe,
  key: KeyRound,
  keyboard: Keyboard,
  "list-check": ListChecks,
  lock: Lock,
  microchip: Cpu,
  paste: ClipboardPaste,
  plug: Plug,
  "scale-balanced": Scale,
  server: Server,
  "shield-halved": ShieldHalf,
  sliders: SlidersHorizontal,
  sparkles: Sparkles,
};

export const CARD_ICON_NAMES = Object.keys(ICONS);

type Kind = "note" | "tip" | "warning" | "info";

const CALLOUT: Record<Kind, { icon: LucideIcon; label: string }> = {
  note: { icon: StickyNote, label: "Note" },
  tip: { icon: Lightbulb, label: "Tip" },
  warning: { icon: AlertTriangle, label: "Warning" },
  info: { icon: InfoIcon, label: "Info" },
};

function Callout({ kind, children }: { kind: Kind; children?: ReactNode }) {
  const { icon: Icon, label } = CALLOUT[kind];

  return (
    <aside aria-label={label} className="docs-callout" data-kind={kind}>
      <Icon aria-hidden="true" className="docs-callout-icon" />
      <div className="docs-callout-body">{children}</div>
    </aside>
  );
}

const isInternal = (href: string) =>
  href.startsWith("/") || href.startsWith("#");

function DocLink({
  href = "",
  children,
  ...rest
}: ComponentPropsWithoutRef<"a">) {
  if (
    href.startsWith("/") &&
    !href.startsWith("//") &&
    !/\.[a-z]+($|#)/.test(href)
  ) {
    return (
      <Link href={href} {...rest}>
        {children}
      </Link>
    );
  }
  const external = !isInternal(href) && !href.startsWith("mailto:");

  return (
    <a
      href={href}
      {...rest}
      {...(external ? { rel: "noopener noreferrer", target: "_blank" } : {})}
    >
      {children}
    </a>
  );
}

function Card({
  title,
  icon,
  href,
  horizontal,
  children,
}: {
  title: string;
  icon?: string;
  href?: string;
  horizontal?: boolean;
  children?: ReactNode;
}) {
  const Icon = icon ? ICONS[icon] : undefined;
  const body = (
    <>
      {Icon ? <Icon aria-hidden="true" className="docs-card-icon" /> : null}
      <div>
        <p className="docs-card-title">
          {title}
          {href ? (
            <ChevronRight aria-hidden="true" className="docs-card-arrow" />
          ) : null}
        </p>
        {children ? <div className="docs-card-text">{children}</div> : null}
      </div>
    </>
  );
  const className = horizontal ? "docs-card docs-card-horizontal" : "docs-card";

  if (href) {
    return isInternal(href) ? (
      <Link className={`${className} docs-card-link`} href={href}>
        {body}
      </Link>
    ) : (
      <a
        className={`${className} docs-card-link`}
        href={href}
        rel="noopener noreferrer"
        target="_blank"
      >
        {body}
      </a>
    );
  }

  return <div className={className}>{body}</div>;
}

function CardGroup({
  cols = 2,
  children,
}: {
  cols?: number;
  children?: ReactNode;
}) {
  return (
    <div className="docs-card-group" data-cols={cols}>
      {children}
    </div>
  );
}

function Steps({ children }: { children?: ReactNode }) {
  return <ol className="docs-steps">{children}</ol>;
}

function Step({ title, children }: { title?: string; children?: ReactNode }) {
  return (
    <li className="docs-step">
      {title ? <p className="docs-step-title">{title}</p> : null}
      <div className="docs-step-body">{children}</div>
    </li>
  );
}

function Accordion({
  title,
  children,
}: {
  title: string;
  children?: ReactNode;
}) {
  return (
    <details className="docs-accordion">
      <summary>
        <ChevronRight aria-hidden="true" className="docs-accordion-chevron" />
        <span>{title}</span>
      </summary>
      <div className="docs-accordion-body">{children}</div>
    </details>
  );
}

function AccordionGroup({ children }: { children?: ReactNode }) {
  return <div className="docs-accordion-group">{children}</div>;
}

function Frame({
  caption,
  children,
}: {
  caption?: string;
  children?: ReactNode;
}) {
  return (
    <figure className="docs-frame">
      {children}
      {caption ? <figcaption>{caption}</figcaption> : null}
    </figure>
  );
}

function CodeGroup({ children }: { children?: ReactNode }) {
  return <div className="docs-code-group">{children}</div>;
}

export const docsMdxComponents = {
  a: DocLink,
  img: ({ alt = "", ...props }: ComponentPropsWithoutRef<"img">) => (
    // eslint-disable-next-line @next/next/no-img-element -- static docs screenshots; images.unoptimized is on
    <img alt={alt} decoding="async" loading="lazy" {...props} />
  ),
  table: (props: ComponentPropsWithoutRef<"table">) => (
    <div className="docs-table">
      <table {...props} />
    </div>
  ),
  Note: (p: { children?: ReactNode }) => <Callout kind="note" {...p} />,
  Tip: (p: { children?: ReactNode }) => <Callout kind="tip" {...p} />,
  Warning: (p: { children?: ReactNode }) => <Callout kind="warning" {...p} />,
  Info: (p: { children?: ReactNode }) => <Callout kind="info" {...p} />,
  Card,
  CardGroup,
  Steps,
  Step,
  Tabs,
  Tab,
  Accordion,
  AccordionGroup,
  Frame,
  CodeGroup,
};

/** The component names the content may use, for tests/docs-content.test.ts. */
export const DOCS_COMPONENT_NAMES = Object.keys(docsMdxComponents).filter(
  (name) => /^[A-Z]/.test(name),
);
