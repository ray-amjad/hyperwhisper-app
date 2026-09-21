export default function NotFound() {
  return (
    <html lang="en">
      <body>
        {/*
          dir is set HERE, on the heading, and not on the <html> or <body> above.
          Next renders this file's tree INSIDE the root layout's <body>, so the
          parser meets a second <html> and a second <body> in "in body" mode and
          discards both tags, keeping only their children. It merges an attribute
          off a discarded tag onto the existing element only when that element
          does not already carry it — and app/layout.tsx now emits lang AND dir on
          the root <html>, so both of this tag's attributes are dropped. Measured:
          <html lang="en" dir="ltr"> here left the rendered document at dir="rtl"
          on /ar/<unknown>, exactly as before the change. This text is English and
          reads left-to-right under every locale, so it states its own direction on
          an element that survives parsing.
        */}
        <h1 dir="ltr">404 - Not Found</h1>
      </body>
    </html>
  );
}
