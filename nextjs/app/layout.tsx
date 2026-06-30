import { ReactNode } from "react";
import Script from "next/script";
import { headers } from "next/headers";

type Props = {
  children: ReactNode;
};

// Root layout required by Next.js - responsible for rendering the single <html>/<body> shell.
// We read the locale from the next-intl middleware header so the lang attribute stays accurate.
export default async function RootLayout({ children }: Props) {
  const locale = (await headers()).get("x-next-intl-locale") ?? "en";

  return (
    <html suppressHydrationWarning lang={locale}>
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
