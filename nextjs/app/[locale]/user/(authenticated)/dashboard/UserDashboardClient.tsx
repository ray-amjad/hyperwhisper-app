"use client";

import { useState } from "react";
import {
  LicenseKeysCard,
  CloudCreditsCard,
  CreditHistoryCard,
  BillingCard,
  DashboardCardsSkeleton,
} from "@/components/customer/dashboard";
import RotateAccountKeyControl from "@/components/customer/dashboard/RotateAccountKeyControl";
import { api } from "@/lib/trpc/client";
import { ErrorReveal } from "@/components/ErrorReveal";

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
  // Whether the first render had to wait for the query, so the skeleton was
  // on screen and the cards should fade in over it (#865).
  const [revealCards] = useState(isLoading);

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

      {/* Account cards (#865). The skeleton and the cards share one grid
          cell. When the query settles the skeleton fades out while its row
          collapses 1fr -> 0fr, and the cards fade in over it, so the cell's
          height goes from the skeleton's to the cards' through real
          intermediate heights, whichever is taller (no keys, an error and a
          zero balance are all shorter). An error renders no cards and the
          ErrorReveal above carries the gap, so mb-0 drops this wrapper's
          space-y margin, tweened with the rest. */}
      <div
        className={`grid transition-[margin] duration-[400ms] ease-in-out motion-reduce:transition-none${
          !isLoading && error ? " mb-0" : ""
        }`}
      >
        <div
          aria-hidden
          className={`[grid-area:1/1] self-start grid pointer-events-none transition-[grid-template-rows,opacity,filter] duration-[400ms] ease-in-out motion-reduce:transition-none ${
            isLoading
              ? "grid-rows-[1fr] opacity-100 blur-[0px]"
              : "grid-rows-[0fr] opacity-0 blur-[2px]"
          }`}
        >
          <div className="min-h-0 overflow-hidden">
            <DashboardCardsSkeleton pulsing={isLoading} />
          </div>
        </div>
        {/* The skeleton is aria-hidden, so this status says what it shows. */}
        <p className="sr-only" role="status">
          {isLoading ? "Loading Account Keys..." : ""}
        </p>
        {/* Later in the tree and positioned, so the cards paint over the
            fading skeleton. They fade in only when a skeleton was on screen:
            a remount with cached data shows them at once. Fill mode
            backwards, not both: a filter left on after the fade would make
            this box the containing block of any position: fixed dialog
            inside the cards. */}
        {isLoading || error ? null : (
          <div
            className={`t-skel-content [grid-area:1/1] self-start relative${
              revealCards
                ? " animate-[skel-reveal_400ms_ease-in-out_backwards] motion-reduce:animate-none"
                : ""
            }`}
          >
            {data?.licenses && data.licenses.length > 0 ? (
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
                <div className="mt-6">
                  <CloudCreditsCard
                    totalCredits={totalCredits}
                    totalMinutesRemaining={totalMinutesRemaining}
                    creditsPerMinute={data.creditsPerMinute}
                    activeLicenseKey={activeLicenseKey}
                  />
                </div>
                {/* Its own query settles later: it grows its slot open
                    instead of pushing Billing down in one frame. */}
                <CreditHistoryCard gap="pt-6" />
              </>
            ) : (
              <div className="bg-white/5 rounded-xl border border-white/10 p-5 text-center">
                <p className="text-gray-400">No Account Keys found</p>
                <a
                  href="/"
                  className="text-emerald-400 hover:text-emerald-300 transition-colors text-sm mt-2 inline-block"
                >
                  Get an Account Key →
                </a>
              </div>
            )}
          </div>
        )}
      </div>

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
