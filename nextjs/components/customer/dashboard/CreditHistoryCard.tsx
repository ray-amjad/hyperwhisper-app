"use client";

import { useState } from "react";
import { api } from "@/lib/trpc/client";
import { formatDate } from "@/lib/format-date";

/**
 * Credit History Card
 *
 * Lists the user's paid credit-pack purchases (top-ups and mints), newest-first,
 * with the amount, purchase date, expiry, and remaining balance per grant.
 * Paid packs only — free/included bundles are not shown here.
 *
 * Expired grants sort after the live ones, and only the first
 * `COLLAPSED_GRANTS` show until "Show all" is pressed, so a long history does
 * not push Billing off the page (#1664).
 *
 * Its query settles after the dashboard's own, so the card grows its slot open
 * (`.t-cell-reveal`, #865) instead of pushing the cards below it down in one
 * frame, unless its data was already cached. `gap` is the space it has above
 * it: it sits inside the clip, so it grows with the tween and a hidden card
 * takes no space.
 */
/** Purchases shown before "Show all" is pressed (#1664). */
export const COLLAPSED_GRANTS = 5;

export default function CreditHistoryCard({ gap }: { gap?: string }) {
  const { data, isLoading, error } = api.customer.creditHistory.useQuery();
  // Grow open only when the card arrives after a wait; a remount with cached
  // data shows it at once.
  const [revealOnArrival] = useState(isLoading);
  const [showAll, setShowAll] = useState(false);

  // Hide the card entirely when there's nothing to show (no purchases yet).
  if (isLoading || error) return null;
  if (!data || data.grants.length === 0) return null;

  // The server sends newest-first; a stable sort keeps that order within the
  // live and the expired groups.
  const grants = [...data.grants].sort(
    (a, b) => Number(a.expired) - Number(b.expired),
  );
  const hidden = grants.length - COLLAPSED_GRANTS;
  const shown =
    showAll || hidden <= 0 ? grants : grants.slice(0, COLLAPSED_GRANTS);

  return (
    <div className={revealOnArrival ? "t-cell-reveal" : undefined}>
      <div>
        <div className={gap}>
          <div className="bg-white/5 rounded-xl border border-white/10 p-5">
            <p className="text-sm text-gray-400 mb-4">Credit Purchases</p>

            <div className="space-y-3">
              {shown.map((grant) => (
                <div
                  key={grant.id}
                  className="flex items-center justify-between gap-4 border-b border-white/5 pb-3 last:border-0 last:pb-0"
                >
                  <div className="min-w-0">
                    {/* <bdi> keeps the English number-and-word order on an RTL page (#1659). */}
                    <p className="text-white font-medium">
                      <bdi>{grant.originalAmount.toLocaleString()} credits</bdi>
                    </p>
                    <p className="text-xs text-gray-400 mt-0.5">
                      <bdi>
                        {formatDate(grant.createdAt)}
                        {grant.expiresAt && (
                          <>
                            {" · "}
                            {grant.expired ? (
                              <span className="text-red-400">
                                expired {formatDate(grant.expiresAt)}
                              </span>
                            ) : (
                              <span>expires {formatDate(grant.expiresAt)}</span>
                            )}
                          </>
                        )}
                      </bdi>
                    </p>
                  </div>
                  <div className="text-right shrink-0">
                    <p className="text-sm text-gray-300">
                      <bdi>
                        {grant.expired ? 0 : grant.remainingAmount.toLocaleString()}{" "}
                        left
                      </bdi>
                    </p>
                  </div>
                </div>
              ))}
            </div>
            {hidden > 0 ? (
              <button
                aria-expanded={showAll}
                className="mt-3 text-sm text-emerald-400 hover:text-emerald-300 transition-colors"
                type="button"
                onClick={() => setShowAll((prev) => !prev)}
              >
                {showAll ? "Show fewer" : `Show all ${grants.length}`}
              </button>
            ) : null}
          </div>
        </div>
      </div>
    </div>
  );
}
