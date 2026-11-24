"use client";

import { useTranslations } from "next-intl";

import RotateAccountKeyView, { issueRefusal } from "./RotateAccountKeyView";
import { useKeyAction } from "./use-key-action";

import { MAX_LIVE_ACCOUNT_KEYS } from "@/lib/account-key-limits";
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
  /**
   * The card's one success line, if this control posted it (see
   * `key-card-notice.ts`), and how to post or clear it.
   */
  notice: string | null;
  onNotice: (text: string | null) => void;
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
  notice,
  onNotice,
}: IssueAccountKeyControlProps) {
  const t = useTranslations("licenseKeysCard");
  const utils = api.useUtils();
  const { phase, setPhase, error, onRefusal, viewHandlers } = useKeyAction({
    email,
    emailProven,
    onNotice,
    linkSentKey: "issueLinkSent",
  });

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
      onNotice(t("issued"));
    },
    onError: (err) => {
      onRefusal(issueRefusal(err.data?.code), { max: MAX_LIVE_ACCOUNT_KEYS });
    },
  });

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
      {...viewHandlers(() => issue.mutate(), issue.isPending)}
    />
  );
}
