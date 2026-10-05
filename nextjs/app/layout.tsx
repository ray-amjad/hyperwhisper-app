import { ReactNode } from "react";
import Script from "next/script";
import { headers } from "next/headers";
import { localeDirection } from "@/src/i18n/locales";

type Props = {
  children: ReactNode;
};

// Root layout required by Next.js - responsible for rendering the single <html>/<body> shell.
// We read the locale from the next-intl middleware header so the lang attribute stays accurate.
// The site is dark-only, so "dark" is in the server markup: with JavaScript off the next-themes
// script never runs, and without the class the white text lands on a light page (#1151).
export default async function RootLayout({ children }: Props) {
  const locale = (await headers()).get("x-next-intl-locale") ?? "en";

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
          src="https://www.agentstack.build/embed.js"
          data-agent-id="u0r4QjKQFA1O"
          strategy="lazyOnload"
        />
      </body>
    </html>
  );
}
