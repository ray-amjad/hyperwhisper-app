"use client";

import { api } from "@/lib/trpc/client";

/**
 * Credit History Card
 *
 * Lists the user's paid credit-pack purchases (top-ups and mints), newest-first,
 * with the amount, purchase date, expiry, and remaining balance per grant.
 * Paid packs only — free/included bundles are not shown here.
 */
export default function CreditHistoryCard() {
  const { data, isLoading, error } = api.customer.creditHistory.useQuery();

  // Hide the card entirely when there's nothing to show (no purchases yet).
  if (isLoading || error) return null;
  if (!data || data.grants.length === 0) return null;

  const formatDate = (iso: string) =>
    new Date(iso).toLocaleDateString(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric",
    });

  return (
    <div className="bg-white/5 rounded-xl border border-white/10 p-5">
      <p className="text-sm text-gray-400 mb-4">Credit Purchases</p>

      <div className="space-y-3">
        {data.grants.map((grant) => (
          <div
            key={grant.id}
            className="flex items-center justify-between gap-4 border-b border-white/5 pb-3 last:border-0 last:pb-0"
          >
            <div className="min-w-0">
              <p className="text-white font-medium">
                {grant.originalAmount.toLocaleString()} credits
              </p>
              <p className="text-xs text-gray-500 mt-0.5">
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
              </p>
            </div>
            <div className="text-right shrink-0">
              <p className="text-sm text-gray-300">
                {grant.expired ? 0 : grant.remainingAmount.toLocaleString()} left
              </p>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
