/**
 * Dashboard cards placeholder (#865)
 *
 * The shape of what the account query reveals for one Account Key: the
 * Account Keys card over the Cloud Credits card, at their real heights, so the
 * swap to the real cards moves nothing below them. Same pulsing bars as
 * `PortalSkeleton`; `pulsing` stops them once the skeleton is fading out, so
 * a hidden skeleton runs no endless animation.
 */
export default function DashboardCardsSkeleton({
  pulsing,
}: {
  pulsing: boolean;
}) {
  const bar = `bg-white/10 rounded${
    pulsing ? " animate-pulse motion-reduce:animate-none" : ""
  }`;
  const card = "bg-white/5 rounded-xl border border-white/10 p-5";

  return (
    <div className="space-y-6">
      {/* Account Keys: the label over one key row and its rotate link. */}
      <div className={card}>
        <div className={`h-5 w-28 mb-3 ${bar}`} />
        <div className={`h-8 w-64 max-w-full ${bar}`} />
        <div className="pt-2">
          <div className={`h-4 w-32 ${bar}`} />
        </div>
      </div>

      {/* Cloud Credits: label, balance and minutes, then one row of buy tiles. */}
      <div className={card}>
        <div className="mb-4">
          <div className={`h-5 w-28 mb-1 ${bar}`} />
          <div className={`h-8 w-24 ${bar}`} />
          <div className={`h-5 w-40 mt-0.5 ${bar}`} />
        </div>
        <div className="grid grid-cols-3 gap-2">
          {Array.from({ length: 3 }).map((_, i) => (
            <div key={i} className={`h-[86px] rounded-lg ${bar}`} />
          ))}
        </div>
      </div>
    </div>
  );
}
