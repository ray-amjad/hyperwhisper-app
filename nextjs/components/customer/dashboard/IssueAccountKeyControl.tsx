"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";

import RotateAccountKeyView, {
  issueRefusal,
  type RotatePhase,
} from "./RotateAccountKeyView";

import { MAX_LIVE_ACCOUNT_KEYS } from "@/lib/account-key-limits";
import { authClient } from "@/src/lib/auth-client";
import { api } from "@/lib/trpc/client";

interface IssueAccountKeyControlProps {
  /**
   * Whether this browser's session was proven by email (magic link). A session
   * opened with a key cannot add one, so the control offers an emailed
   * sign-in link instead of the confirmation.
   */
  emailProven: boolean;
  /** The account's address, where that link is sent. */
  email: string;
  /**
   * How many live (`granted`) keys the account holds now. The dashboard only
   * renders this control when it is at least 1; at the cap the button is
   * disabled with the reason.
   */
  liveKeyCount: number;
  /** Called after a key was added (the dashboard clears its own notice). */
  onIssued?: () => void;
}

/**
 * The "Add a new key" control at the foot of the Account Keys card: a
 * confirmation, the `customer.issueAccountKey` mutation, the email step-up
 * and the translated copy. The markup is `RotateAccountKeyView`.
 *
 * After an issue the new row is added to the `licensesWithCredits` cache at
 * once (it shares the account balance of the rows already there), then the
 * list is refetched.
 *
 * What each refusal leaves on screen is `issueRefusal` in the view.
 */
export default function IssueAccountKeyControl({
  emailProven,
  email,
  liveKeyCount,
  onIssued,
}: IssueAccountKeyControlProps) {
  const t = useTranslations("licenseKeysCard");
  const utils = api.useUtils();
  const [phase, setPhase] = useState<RotatePhase>("idle");
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const issue = api.customer.issueAccountKey.useMutation({
    onSuccess: (data) => {
      utils.customer.licensesWithCredits.setData(undefined, (prev) => {
        if (!prev || prev.licenses.length === 0) return prev;
        const [sibling] = prev.licenses;

        return {
          ...prev,
          licenses: [
            {
              ...sibling,
              id: data.id,
              key: data.key,
              status: data.status,
              createdAt: data.createdAt,
            },
            ...prev.licenses,
          ],
        };
      });
      void utils.customer.licensesWithCredits.invalidate();
      setPhase("idle");
      setNotice(t("issued"));
      onIssued?.();
    },
    onError: (err) => {
      const refusal = issueRefusal(err.data?.code);

      if (refusal.refetch) void utils.customer.licensesWithCredits.invalidate();
      setPhase(refusal.phase);
      setError(
        refusal.message
          ? t(refusal.message, { max: MAX_LIVE_ACCOUNT_KEYS })
          : null,
      );
    },
  });

  async function sendLink() {
    setError(null);
    setPhase("sending");
    const { error: linkError } = await authClient.signIn.magicLink({
      email,
      callbackURL: window.location.pathname,
    });

    if (linkError) {
      setPhase("verify");
      setError(t("linkFailed"));

      return;
    }

    setPhase("idle");
    setNotice(t("issueLinkSent"));
  }

  const disabledReason =
    liveKeyCount >= MAX_LIVE_ACCOUNT_KEYS
      ? t("issueCap", { max: MAX_LIVE_ACCOUNT_KEYS })
      : null;

  return (
    <RotateAccountKeyView
      disabledReason={disabledReason}
      // The reason already says it once the list is refetched.
      error={disabledReason && phase === "idle" ? null : error}
      idPrefix="issue-key"
      labels={{
        rotate: t("issue"),
        confirmTitle: t("issueConfirmTitle"),
        confirmBody: t("issueConfirmBody"),
        confirm: t("issueConfirm"),
        cancel: t("cancel"),
        rotating: t("issuing"),
        verifyTitle: t("verifyTitle"),
        verifyBody: t("issueVerifyBody"),
        sendLink: t("sendLink"),
        sendingLink: t("sendingLink"),
      }}
      licenseId="account"
      notice={notice}
      phase={phase}
      onCancel={() => {
        setError(null);
        setPhase("idle");
      }}
      onConfirm={() => {
        if (issue.isPending) return;
        setError(null);
        setPhase("rotating");
        issue.mutate();
      }}
      onSendLink={() => {
        void sendLink();
      }}
      onStart={() => {
        setNotice(null);
        setError(null);
        setPhase(emailProven ? "confirming" : "verify");
      }}
    />
  );
}
