"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import {
  LicenseKeysCard,
  CloudCreditsCard,
  CreditHistoryCard,
  BillingCard,
  DashboardCardsSkeleton,
} from "@/components/customer/dashboard";
import RotateAccountKeyControl from "@/components/customer/dashboard/RotateAccountKeyControl";
import DeleteAccountKeyControl from "@/components/customer/dashboard/DeleteAccountKeyControl";
import IssueAccountKeyControl from "@/components/customer/dashboard/IssueAccountKeyControl";
import {
  NOTICE_AT,
  keyCardNoticeAt,
  postKeyCardNotice,
  type KeyCardNoticeState,
} from "@/components/customer/dashboard/key-card-notice";
import { api } from "@/lib/trpc/client";
import { ErrorReveal } from "@/components/ErrorReveal";

interface UserDashboardClientProps {
  user: {
    email: string;
    id: string;
  };
  isAdmin: boolean;
  /**
   * This session was proven by email, so it may rotate, add or delete an
   * Account Key.
   */
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
  const [skeletonGone, setSkeletonGone] = useState(!isLoading);

  // Credits are pooled per account, so use the query's account-level totals
  // rather than summing per license (which double-counts a multi-key account's
  // pooled balance).
  const totalCredits = data?.totalCredits ?? 0;
  const totalMinutesRemaining = data?.totalMinutesRemaining ?? 0;
  const activeLicenseKey = data?.licenses?.find((l) => l.status === "granted")?.key ?? null;
  // Live keys: the server refuses to delete the last one, and to add one past
  // MAX_LIVE_ACCOUNT_KEYS or to an account with none, and so do the controls.
  const liveKeyCount =
    data?.licenses?.filter((l) => l.status === "granted").length ?? 0;
  const t = useTranslations("licenseKeysCard");
  // The Account Keys card's ONE success line: the latest add, delete, rotate
  // or sent link replaces any earlier one (`key-card-notice.ts`).
  const [keyNotice, setKeyNotice] = useState<KeyCardNoticeState>(null);
  const noticeProps = (at: string) => ({
    notice: keyCardNoticeAt(keyNotice, at),
    onNotice: (text: string | null) =>
      setKeyNotice((prev) => postKeyCardNotice(prev, at, text)),
  });

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
          cell, so it is as tall as the taller of the two. When the query
          settles the skeleton fades out while its row collapses 1fr -> 0fr,
          and the cards fade in over it. Cards shorter than the skeleton (no
          keys, an error, a zero balance) shrink the cell through real
          intermediate heights; cards taller than it (several keys) take
          their height in the first frame of the cross-fade. An error renders
          no cards and the ErrorReveal above carries the gap, so mb-0 drops
          this wrapper's space-y margin, tweened with the rest. */}
      <div
        className={`grid transition-[margin] duration-[400ms] ease-in-out motion-reduce:transition-none${
          !isLoading && error ? " mb-0" : ""
        }`}
      >
        {/* It unmounts when its fade ends, so no hidden layer or pulse is
            left behind. Under reduced motion there is no transition and no
            pulse, and the collapsed skeleton just stays. */}
        {skeletonGone ? null : (
          <div
            aria-hidden
            className={`[grid-area:1/1] self-start grid pointer-events-none transition-[grid-template-rows,opacity,filter] duration-[400ms] ease-in-out motion-reduce:transition-none ${
              isLoading
                ? "grid-rows-[1fr] opacity-100 blur-[0px]"
                : "grid-rows-[0fr] opacity-0 blur-[2px]"
            }`}
            onTransitionEnd={(event) => {
              if (
                !isLoading &&
                event.target === event.currentTarget &&
                event.propertyName === "opacity"
              ) {
                setSkeletonGone(true);
              }
            }}
          >
            <div className="min-h-0 overflow-hidden">
              <DashboardCardsSkeleton />
            </div>
          </div>
        )}
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
                  // Only an account that already holds a live key may add one
                  // (never a way to a first key); the server enforces it too.
                  footer={
                    liveKeyCount > 0 ? (
                      <>
                        <p
                          aria-live="polite"
                          className="pt-2 text-xs text-emerald-300 empty:hidden"
                          role="status"
                        >
                          {/* After a delete: its row and control are gone. */}
                          {keyCardNoticeAt(keyNotice, NOTICE_AT.card)}
                        </p>
                        <IssueAccountKeyControl
                          email={user.email}
                          emailProven={canRotateKeys}
                          liveKeyCount={liveKeyCount}
                          {...noticeProps(NOTICE_AT.issue)}
                        />
                      </>
                    ) : null
                  }
                  licenses={data.licenses}
                  // Only a granted key rotates (a revoked or refunded one is already
                  // dead); the server enforces the same rule.
                  renderActions={(license, reveal) =>
                    license.status === "granted" ? (
                      // Stacked, not side by side: an open confirmation
                      // takes the row's full width.
                      <div>
                        <RotateAccountKeyControl
                          email={user.email}
                          emailProven={canRotateKeys}
                          license={license}
                          onRotated={reveal}
                          {...noticeProps(NOTICE_AT.rotate(license.id))}
                        />
                        <DeleteAccountKeyControl
                          email={user.email}
                          emailProven={canRotateKeys}
                          isLastLiveKey={liveKeyCount <= 1}
                          license={license}
                          onDeleted={({ cloudCleared }) =>
                            setKeyNotice((prev) =>
                              postKeyCardNotice(
                                prev,
                                NOTICE_AT.card,
                                cloudCleared
                                  ? t("deleted")
                                  : `${t("deleted")} ${t("cloudPurgePending")}`,
                              ),
                            )
                          }
                          {...noticeProps(NOTICE_AT.deleteKey(license.id))}
                        />
                      </div>
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
