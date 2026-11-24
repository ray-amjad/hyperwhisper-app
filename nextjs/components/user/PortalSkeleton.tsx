import DashboardCardsSkeleton from "@/components/customer/dashboard/DashboardCardsSkeleton";
import { CARD, SKEL } from "@/components/user/panel-styles";

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
 *   so it never shows the admin table shape — a heading block over the
 *   dashboard's own cards skeleton.
 */
export default function PortalSkeleton({
  variant,
}: {
  variant: "table" | "panel";
}) {
  const bar = SKEL;
  const card = CARD;

  return (
    <div aria-busy="true" className="space-y-6" role="status">
      <span className="sr-only">Loading…</span>
      {/* The heading block at PAGE_TITLE's and PAGE_SUB's real line boxes:
          26px (30px from md) at leading 1.15, then mt-1.5 and one 20px line. */}
      <div>
        <div className="flex h-[1.15em] items-center text-[26px] md:text-[30px]">
          <div className={`h-[0.9em] w-56 max-w-full ${bar}`} />
        </div>
        <div className="mt-1.5 flex h-5 items-center">
          <div className={`h-4 w-72 max-w-full ${bar}`} />
        </div>
      </div>
      {variant === "table" ? (
        <div className={`${card} overflow-hidden`}>
          <div className="border-b border-[var(--dusk-line)] px-6 py-4">
            <div className={`h-3 w-40 ${bar}`} />
          </div>
          <div className="divide-y divide-[var(--dusk-line)]">
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
        // The dashboard's first paint is this heading over its own cards
        // skeleton (the account query is still loading), so the panel shape
        // is that skeleton and the swap from loading.tsx moves nothing.
        <DashboardCardsSkeleton />
      )}
    </div>
  );
}
