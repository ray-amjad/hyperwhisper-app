/**
 * Portal route fallback (#915)
 *
 * The skeleton each portal route's `loading.tsx` renders, so a sidebar click
 * paints at once instead of leaving the old page up until the new one
 * commits. The portal layout (sidebar and header) stays mounted around it.
 *
 * - `table`: the admin pages (Devices, Customers) — a heading block over the
 *   table card `DevicesClient.tsx` renders.
 * - `panel`: the Dashboard, which is also every regular user's landing page,
 *   so it never shows the admin table shape — a heading block over two plain
 *   cards.
 */
export default function PortalSkeleton({
  variant,
}: {
  variant: "table" | "panel";
}) {
  const bar = "bg-white/10 rounded animate-pulse motion-reduce:animate-none";
  const card = "bg-white/5 rounded-xl border border-white/10";

  return (
    <div aria-busy="true" className="space-y-6" role="status">
      <span className="sr-only">Loading…</span>
      <div>
        <div className={`h-8 w-56 ${bar}`} />
        <div className={`h-4 w-72 mt-2 ${bar}`} />
      </div>
      {variant === "table" ? (
        <div className={`${card} overflow-hidden`}>
          <div className="border-b border-white/10 px-6 py-4">
            <div className={`h-3 w-40 ${bar}`} />
          </div>
          <div className="divide-y divide-white/5">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="flex gap-12 px-6 py-4">
                <div className={`h-4 w-48 ${bar}`} />
                <div className={`h-4 w-28 ${bar}`} />
                <div className={`h-4 w-12 ${bar}`} />
              </div>
            ))}
          </div>
        </div>
      ) : (
        Array.from({ length: 2 }).map((_, i) => (
          <div key={i} className={`${card} p-6 space-y-3`}>
            <div className={`h-5 w-40 ${bar}`} />
            <div className={`h-4 w-64 ${bar}`} />
          </div>
        ))
      )}
    </div>
  );
}
