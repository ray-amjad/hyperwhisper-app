export default function NotFound() {
  return (
    <>
      {/*
        No <html> or <body> here. Next renders this file INSIDE the root layout
        (app/layout.tsx), which already supplies the document's only <html> and
        <body>. A nested <html> made React 19 compare its props with the real
        document and log a hydration mismatch on every 404 (#1224).

        dir is set on the heading because the root <html> carries the locale's
        dir, so on /ar/<unknown> the document is dir="rtl". This text is English
        and reads left-to-right under every locale, so it states its own
        direction.
      */}
      <h1 dir="ltr">404 - Not Found</h1>
    </>
  );
}
