import { CARD, SKEL } from "@/components/user/panel-styles";

/**
 * Dashboard cards placeholder (#865)
 *
 * The shape of what the account query reveals for one Account Key: the
 * Account Keys card over the Cloud Credits card, at their real heights, so the
 * swap to the real cards moves nothing below them. Same pulsing bars as
 * `PortalSkeleton`.
 */
export default function DashboardCardsSkeleton() {
  const bar = SKEL;
  const card = `${CARD} p-5 sm:p-6`;

  return (
    <div className="space-y-6">
      {/* Account Keys: the label over one key row and its rotate link. The
          heights are the real card's (measured at 390, 768 and 1280px): a
          17.25px label, a 34px key row whose chip wraps to its own 24px line
          on a phone, and the 32px rotate row. */}
      <div className={card}>
        <div className={`h-[17.25px] w-28 mb-3.5 ${bar}`} />
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className={`h-[34px] w-64 max-w-full ${bar}`} />
          <div className="flex h-6 items-center">
            <div className={`h-[21px] w-14 rounded-full ${bar}`} />
          </div>
        </div>
        <div className="flex h-8 items-center pt-2">
          <div className={`h-4 w-20 ${bar}`} />
        </div>
      </div>

      {/* Cloud Credits: label, balance (34px, 40px from sm) and the minutes
          line, then one row of buy tiles. A tile is 93.5px, and 109.5px where
          its caption wraps (tiles under 110px wide, so viewports under 422px). */}
      <div className={card}>
        <div className="mb-5">
          <div className={`h-[17.25px] w-28 ${bar}`} />
          <div className={`mt-2 h-[34px] sm:h-10 w-28 ${bar}`} />
          <div className={`mt-2 h-5 w-48 ${bar}`} />
        </div>
        <div className="grid grid-cols-3 gap-2.5">
          {Array.from({ length: 3 }).map((_, i) => (
            <div
              key={i}
              className={`h-[109.5px] min-[422px]:h-[93.5px] rounded-2xl ${bar}`}
            />
          ))}
        </div>
      </div>
    </div>
  );
}
