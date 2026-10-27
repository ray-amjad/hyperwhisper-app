"use client";

import { LicenseKeysCard, CloudCreditsCard, CreditHistoryCard, BillingCard } from "@/components/customer/dashboard";
import RotateAccountKeyControl from "@/components/customer/dashboard/RotateAccountKeyControl";
import { api } from "@/lib/trpc/client";
import { ErrorReveal } from "@/components/ErrorReveal";
import { Spinner } from "@/components/ui/Spinner";

interface UserDashboardClientProps {
  user: {
    email: string;
    id: string;
  };
  isAdmin: boolean;
  /** This session was proven by email, so it may rotate an Account Key. */
  canRotateKeys: boolean;
}

/**
 * Unified User Dashboard Client
 *
 * Displays customer features (licenses, credits, billing).
 */
export default function UserDashboardClient({
  user,
  isAdmin,
  canRotateKeys,
}: UserDashboardClientProps) {
  // Always fetch customer data (licenses, credits)
  const {
    data,
    isLoading,
    error,
  } = api.customer.licensesWithCredits.useQuery();

  // Credits are pooled per account, so use the query's account-level totals
  // rather than summing per license (which double-counts a multi-key account's
  // pooled balance).
  const totalCredits = data?.totalCredits ?? 0;
  const totalMinutesRemaining = data?.totalMinutesRemaining ?? 0;
  const activeLicenseKey = data?.licenses?.find((l) => l.status === "granted")?.key ?? null;

  return (
    <div className="space-y-6">
      {/* Header */}
      <div>
        <h1 className="text-2xl font-semibold text-white">Dashboard</h1>
        <p className="text-gray-400 text-sm mt-1">{user.email}</p>
      </div>

      {/* The load error reveals (#2286) instead of arriving at full height in
          one frame, so it lives in its own always-mounted wrapper and the
          ternary below renders nothing for it. The space-y-6 gap rides inside
          the clip as pb-6. */}
      <ErrorReveal gap="pb-6">
        {isLoading ? null : error ? (
          <div
            className="bg-white/5 rounded-xl border border-white/10 p-5"
            role="alert"
          >
            <p className="text-red-400 text-sm">{error.message}</p>
          </div>
        ) : null}
      </ErrorReveal>

      {/* License Keys Card */}
      {isLoading ? (
        <div className="bg-white/5 rounded-xl border border-white/10 p-5">
          <div className="flex items-center gap-2">
            <Spinner className="w-4 h-4 text-emerald-400" />
            <span className="text-gray-400 text-sm">Loading Account Keys...</span>
          </div>
        </div>
      ) : error ? null : data?.licenses && data.licenses.length > 0 ? (
        <>
          <LicenseKeysCard
            licenses={data.licenses}
            // Only a granted key rotates (a revoked or refunded one is already
            // dead); the server enforces the same rule.
            renderActions={(license, reveal) =>
              license.status === "granted" ? (
                <RotateAccountKeyControl
                  email={user.email}
                  emailProven={canRotateKeys}
                  license={license}
                  onRotated={reveal}
                />
              ) : null
            }
          />
          <CloudCreditsCard
            totalCredits={totalCredits}
            totalMinutesRemaining={totalMinutesRemaining}
            creditsPerMinute={data.creditsPerMinute}
            activeLicenseKey={activeLicenseKey}
          />
          <CreditHistoryCard />
        </>
      ) : (
        <div className="bg-white/5 rounded-xl border border-white/10 p-5 text-center">
          <p className="text-gray-400">No Account Keys found</p>
          <a href="/" className="text-emerald-400 hover:text-emerald-300 transition-colors text-sm mt-2 inline-block">
            Get an Account Key →
          </a>
        </div>
      )}

      {/* Billing */}
      <BillingCard />

      {/* Help */}
      <p className="text-sm text-gray-400">
        Need help?{" "}
        <a href="mailto:hi@support.hyperwhisper.com" className="text-gray-400 hover:text-white transition-colors">
          hi@support.hyperwhisper.com
        </a>
      </p>
    </div>
  );
}
