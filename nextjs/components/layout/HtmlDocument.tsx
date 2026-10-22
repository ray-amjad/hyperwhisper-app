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
export default function HtmlDocument({ locale, children }: Props) {
  return (
    <html
      suppressHydrationWarning
      className="dark"
      dir={localeDirection(locale)}
      lang={locale}
    >
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
