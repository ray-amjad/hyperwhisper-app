export default function LegalLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <div className="container mx-auto px-6 py-12 max-w-5xl">
        <div className="bg-white dark:bg-gray-800 rounded-lg shadow-sm p-10 lg:p-12">
          <article className="prose prose-lg max-w-none dark:prose-invert">
            {children}
          </article>
        </div>
      </div>
    </div>
  );
}
