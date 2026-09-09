export type PaidCreditGrant = {
  id: string;
  createdAt: Date;
  expiresAt: Date | null;
  originalAmount: number;
  remainingAmount: number;
  status: string;
};

export type CreditHistoryClock = {
  now: () => number;
};

/** Builds the customer credit-history response around an injectable clock. */
export function createCreditHistoryPresenter(
  clock: CreditHistoryClock = { now: Date.now },
) {
  return function presentCreditHistory(grants: readonly PaidCreditGrant[]) {
    const now = clock.now();

    return {
      grants: grants.map((grant) => ({
        id: grant.id,
        createdAt: grant.createdAt.toISOString(),
        expiresAt: grant.expiresAt ? grant.expiresAt.toISOString() : null,
        originalAmount: grant.originalAmount,
        remainingAmount: grant.remainingAmount,
        // A grant expires at the boundary, not only after it.
        expired: grant.expiresAt ? grant.expiresAt.getTime() <= now : false,
        status: grant.status,
      })),
    };
  };
}
