/**
 * Portal route fallback (#915)
 *
 * Next.js wraps every page under this layout in a Suspense boundary with
 * this file as the fallback, so a sidebar click paints it at once instead of
 * leaving the old page up until the new one commits. The layout (sidebar and
 * header) stays mounted around it. The shape follows the pages it stands in
 * for: a heading block over the table card `DevicesClient.tsx` renders.
 */
export default function UserPortalLoading() {
  const bar = "bg-white/10 rounded animate-pulse motion-reduce:animate-none";

  return (
    <div aria-busy="true" className="space-y-6" role="status">
      <span className="sr-only">Loading…</span>
      <div>
        <div className={`h-8 w-56 ${bar}`} />
        <div className={`h-4 w-72 mt-2 ${bar}`} />
      </div>
      <div className="bg-white/5 rounded-xl border border-white/10 overflow-hidden">
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
    </div>
  );
}
