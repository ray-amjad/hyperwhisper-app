export default function LegalLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <div className="container mx-auto px-6 py-12 max-w-5xl">
        <div className="bg-white dark:bg-gray-800 rounded-lg shadow-sm p-10 lg:p-12">
          {/*
            `overflow-wrap:anywhere` keeps a long unbreakable token — the support address, a URL —
            inside the column. Without it the 263px `hi@support.hyperwhisper.com` ran off the card
            and off a 320px screen, so all 3 legal routes needed sideways scrolling (WCAG 2.1 SC
            1.4.10). `anywhere` breaks only when a line would otherwise overflow, so ordinary
            sentences are untouched; `break-all` would break every word and is the wrong tool.
          */}
          <article className="prose prose-lg max-w-none dark:prose-invert [overflow-wrap:anywhere]">
            {children}
          </article>
        </div>
      </div>
    </div>
  );
}
