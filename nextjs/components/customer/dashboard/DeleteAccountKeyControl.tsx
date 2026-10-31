"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";

import RotateAccountKeyView, {
  deleteRefusal,
  type RotatePhase,
} from "./RotateAccountKeyView";

import { authClient } from "@/src/lib/auth-client";
import { api } from "@/lib/trpc/client";

interface DeleteAccountKeyControlProps {
  license: { id: string; key: string };
  /**
   * Whether this browser's session was proven by email (magic link). A session
   * opened with a key cannot delete one, so the control offers an emailed
   * sign-in link instead of the confirmation.
   */
  emailProven: boolean;
  /** The account's address, where that link is sent. */
  email: string;
  /**
   * This is the account's only live key. The server refuses to delete it, so
   * the button is disabled with the reason instead.
   */
  isLastLiveKey: boolean;
  /** Called after the delete lands (the row is already gone from the list). */
  /** `cloudCleared` false: every cloud app did not confirm the purge. */
  onDeleted: (result: { cloudCleared: boolean }) => void;
  /**
   * The card's one success line, if this control posted it (see
   * `key-card-notice.ts`), and how to post or clear it.
   */
  notice: string | null;
  onNotice: (text: string | null) => void;
}

/**
 * The stateful half of the delete control under one Account Key: the
 * confirmation (it warns that devices using the key stop working), the
 * `customer.deleteAccountKey` mutation, the email step-up and the translated
 * copy. The markup is `RotateAccountKeyView`, in red.
 *
 * After a delete the row is dropped from the `licensesWithCredits` cache at
 * once, then the list is refetched. This control unmounts with its row, so
 * the success line is the dashboard's (`onDeleted`).
 *
 * What each refusal leaves on screen is `deleteRefusal` in the view.
 */
export default function DeleteAccountKeyControl({
  license,
  emailProven,
  email,
  isLastLiveKey,
  onDeleted,
  notice,
  onNotice,
}: DeleteAccountKeyControlProps) {
  const t = useTranslations("licenseKeysCard");
  const utils = api.useUtils();
  const [phase, setPhase] = useState<RotatePhase>("idle");
  const [error, setError] = useState<string | null>(null);

  const remove = api.customer.deleteAccountKey.useMutation({
    onSuccess: (data) => {
      utils.customer.licensesWithCredits.setData(undefined, (prev) =>
        prev
          ? { ...prev, licenses: prev.licenses.filter((l) => l.id !== data.id) }
          : prev,
      );
      void utils.customer.licensesWithCredits.invalidate();
      setPhase("idle");
      onDeleted({ cloudCleared: data.cloudCleared });
    },
    onError: (err) => {
      const refusal = deleteRefusal(err.data?.code);

      if (refusal.refetch) void utils.customer.licensesWithCredits.invalidate();
      setPhase(refusal.phase);
      setError(refusal.message ? t(refusal.message) : null);
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
    onNotice(t("deleteLinkSent"));
  }

  const disabledReason = isLastLiveKey ? t("deleteLastKey") : null;

  return (
    <RotateAccountKeyView
      disabledReason={disabledReason}
      // The view drops the error only when it repeats the reason (a refused
      // last key, once the list is refetched); a CONFLICT or a rate limit
      // still shows beside it.
      error={error}
      idPrefix="delete-key"
      labels={{
        rotate: t("delete"),
        confirmTitle: t("deleteConfirmTitle"),
        confirmBody: t("deleteConfirmBody"),
        confirm: t("deleteConfirm"),
        cancel: t("cancel"),
        rotating: t("deleting"),
        verifyTitle: t("verifyTitle"),
        verifyBody: t("deleteVerifyBody"),
        sendLink: t("sendLink"),
        sendingLink: t("sendingLink"),
      }}
      licenseId={license.id}
      notice={notice}
      phase={phase}
      tone="danger"
      onCancel={() => {
        setError(null);
        setPhase("idle");
      }}
      onConfirm={() => {
        if (remove.isPending) return;
        setError(null);
        setPhase("rotating");
        // The key the user is looking at: if it was rotated meanwhile, the
        // server answers CONFLICT instead of deleting a key they never saw.
        remove.mutate({ id: license.id, currentKey: license.key });
      }}
      onSendLink={() => {
        void sendLink();
      }}
      onStart={() => {
        onNotice(null);
        setError(null);
        setPhase(emailProven ? "confirming" : "verify");
      }}
    />
  );
}
