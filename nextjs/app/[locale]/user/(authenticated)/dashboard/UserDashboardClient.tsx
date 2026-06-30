"use client";

import { LicenseKeysCard, CloudCreditsCard, CreditHistoryCard, BillingCard } from "@/components/customer/dashboard";
import { api } from "@/lib/trpc/client";

interface UserDashboardClientProps {
  user: {
    email: string;
    id: string;
  };
  isAdmin: boolean;
}

/**
 * Unified User Dashboard Client
 *
 * Displays customer features (licenses, credits, billing).
 */
export default function UserDashboardClient({
  user,
  isAdmin,
}: UserDashboardClientProps) {
  // Always fetch customer data (licenses, credits)
  const {
    data,
    isLoading,
    error,
  } = api.customer.licensesWithCredits.useQuery();

  // Calculate totals across all licenses
  const totalCredits = data?.licenses?.reduce((sum, l) => sum + l.credits, 0) ?? 0;
  const totalMinutesRemaining = data?.licenses?.reduce((sum, l) => sum + l.minutesRemaining, 0) ?? 0;
  const activeLicenseKey = data?.licenses?.find((l) => l.status === "granted")?.key ?? null;

  return (
    <div className="space-y-6">
      {/* Header */}
      <div>
        <h1 className="text-2xl font-semibold text-white">Dashboard</h1>
        <p className="text-gray-400 text-sm mt-1">{user.email}</p>
      </div>

      {/* License Keys Card */}
      {isLoading ? (
        <div className="bg-white/5 rounded-xl border border-white/10 p-5">
          <div className="flex items-center gap-2">
            <div className="w-4 h-4 border-2 border-emerald-400 border-t-transparent rounded-full animate-spin" />
            <span className="text-gray-400 text-sm">Loading licenses...</span>
          </div>
        </div>
      ) : error ? (
        <div className="bg-white/5 rounded-xl border border-white/10 p-5">
          <p className="text-red-400 text-sm">{error.message}</p>
        </div>
      ) : data?.licenses && data.licenses.length > 0 ? (
        <>
          <LicenseKeysCard licenses={data.licenses} />
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
          <p className="text-gray-400">No licenses found</p>
          <a href="/" className="text-emerald-400 hover:text-emerald-300 text-sm mt-2 inline-block">
            Purchase a license →
          </a>
        </div>
      )}

      {/* Billing */}
      <BillingCard />

      {/* Help */}
      <p className="text-sm text-gray-500">
        Need help?{" "}
        <a href="mailto:support@hyperwhisper.com" className="text-gray-400 hover:text-white">
          support@hyperwhisper.com
        </a>
      </p>
    </div>
  );
}
