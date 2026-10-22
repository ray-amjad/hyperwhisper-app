import { ReactNode } from "react";
import Script from "next/script";

import { localeDirection } from "@/src/i18n/locales";

type Props = {
  locale: string;
  children: ReactNode;
};

// The single <html>/<body> shell, rendered by app/[locale]/layout.tsx with the
// locale from its params. It used to live in app/layout.tsx and read the locale
// from the proxy's x-next-intl-locale header, and that headers() call made every
// route in the app dynamic (#843).
// The site is dark-only, so "dark" is in the server markup: with JavaScript off the next-themes
// script never runs, and without the class the white text lands on a light page (#1151).
//
// The open-source banner's dismissal lives in localStorage, which the server cannot read. This
// blocking script marks <html> before first paint, and globals.css hides the banner from that
// mark, so a visitor who dismissed it never sees it flash in and the header never jumps 48px
// (#849). It reads no cookie or header, so every route stays static (#843).
const BANNER_DISMISSED_SCRIPT = `try{if(localStorage.getItem("os-banner-dismissed")==="1")document.documentElement.dataset.osBanner="dismissed"}catch(e){}`;

export default function HtmlDocument({ locale, children }: Props) {
  return (
    <html
      suppressHydrationWarning
      className="dark"
      dir={localeDirection(locale)}
      lang={locale}
    >
      <head>
        <script dangerouslySetInnerHTML={{ __html: BANNER_DISMISSED_SCRIPT }} />
      </head>
      <body>
        {children}
        <Script id="agentstack-init" strategy="lazyOnload">
          {`window.agentstack=new Proxy({_q:[]},{get(t,p){if(p==='_q')return t._q;return(...a)=>t._q.push([p,...a]);}});`}
        </Script>
        <Script
          data-agent-id="u0r4QjKQFA1O"
          src="https://www.agentstack.build/embed.js"
          strategy="lazyOnload"
        />
      </body>
    </html>
  );
}
