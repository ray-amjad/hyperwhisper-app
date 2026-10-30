"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";

import RotateAccountKeyView, {
  rotateRefusal,
  type RotatePhase,
} from "./RotateAccountKeyView";

import { authClient } from "@/src/lib/auth-client";
import { api } from "@/lib/trpc/client";

interface RotateAccountKeyControlProps {
  license: { id: string; key: string };
  /**
   * Whether this browser's session was proven by email (magic link). A session
   * opened with the key itself cannot rotate it, so the control offers an
   * emailed sign-in link instead of the confirmation.
   */
  emailProven: boolean;
  /** The account's address, where that link is sent. */
  email: string;
  /** Called after a rotation lands, so the card can show the new key. */
  onRotated: () => void;
}

/**
 * The stateful half of the rotate control: the confirmation step, the
 * `customer.rotateAccountKey` mutation, the email step-up, and the translated
 * copy. The markup is `RotateAccountKeyView`, which holds no hooks.
 *
 * After a rotation the dashboard's `licensesWithCredits` cache is patched with
 * the new key at once (so the card shows it with no flash of the dead one) and
 * then refetched.
 *
 * What each refusal leaves on screen is `rotateRefusal` in the view.
 */
export default function RotateAccountKeyControl({
  license,
  emailProven,
  email,
  onRotated,
}: RotateAccountKeyControlProps) {
  const t = useTranslations("licenseKeysCard");
  const utils = api.useUtils();
  const [phase, setPhase] = useState<RotatePhase>("idle");
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const rotate = api.customer.rotateAccountKey.useMutation({
    onSuccess: (data) => {
      utils.customer.licensesWithCredits.setData(undefined, (prev) =>
        prev
          ? {
              ...prev,
              licenses: prev.licenses.map((l) =>
                l.id === data.id ? { ...l, key: data.key } : l,
              ),
            }
          : prev,
      );
      void utils.customer.licensesWithCredits.invalidate();
      setPhase("idle");
      // Not every cloud app confirmed the purge: the old key may still
      // transcribe there until its cached verdict expires.
      setNotice(
        data.cloudCleared
          ? t("rotated")
          : `${t("rotated")} ${t("cloudPurgePending")}`,
      );
      onRotated();
    },
    onError: (err) => {
      const refusal = rotateRefusal(err.data?.code);

      if (refusal.refetch) void utils.customer.licensesWithCredits.invalidate();
      setPhase(refusal.phase);
      setError(refusal.message ? t(refusal.message) : null);
    },
  });

  async function sendLink() {
    setError(null);
    setPhase("sending");
    // Back to this page once the link is followed, with an email session.
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
    setNotice(t("linkSent"));
  }

  return (
    <RotateAccountKeyView
      error={error}
      labels={{
        rotate: t("rotate"),
        confirmTitle: t("confirmTitle"),
        confirmBody: t("confirmBody"),
        confirm: t("confirm"),
        cancel: t("cancel"),
        rotating: t("rotating"),
        verifyTitle: t("verifyTitle"),
        verifyBody: t("verifyBody"),
        sendLink: t("sendLink"),
        sendingLink: t("sendingLink"),
      }}
      licenseId={license.id}
      notice={notice}
      phase={phase}
      onCancel={() => {
        setError(null);
        setPhase("idle");
      }}
      onConfirm={() => {
        if (rotate.isPending) return;
        setError(null);
        setPhase("rotating");
        // The key the user is looking at: if it changed meanwhile, the server
        // answers CONFLICT instead of rotating a key they never saw.
        rotate.mutate({ id: license.id, currentKey: license.key });
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
