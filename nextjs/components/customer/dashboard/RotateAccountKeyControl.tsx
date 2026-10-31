"use client";

import { useTranslations } from "next-intl";

import RotateAccountKeyView, { rotateRefusal } from "./RotateAccountKeyView";
import { useKeyAction } from "./use-key-action";

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
  /**
   * The card's one success line, if this control posted it (see
   * `key-card-notice.ts`), and how to post or clear it.
   */
  notice: string | null;
  onNotice: (text: string | null) => void;
}

/**
 * The stateful half of the rotate control: the confirmation step, the
 * `customer.rotateAccountKey` mutation, the email step-up, and the translated
 * copy. The markup is `RotateAccountKeyView`, which holds no hooks; the
 * step-up state is `useKeyAction`.
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
  notice,
  onNotice,
}: RotateAccountKeyControlProps) {
  const t = useTranslations("licenseKeysCard");
  const utils = api.useUtils();
  const { phase, setPhase, error, onRefusal, viewHandlers } = useKeyAction({
    email,
    emailProven,
    onNotice,
    linkSentKey: "linkSent",
  });

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
      onNotice(
        data.cloudCleared
          ? t("rotated")
          : `${t("rotated")} ${t("cloudPurgePending")}`,
      );
      onRotated();
    },
    onError: (err) => {
      onRefusal(rotateRefusal(err.data?.code));
    },
  });

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
      {...viewHandlers(
        // The key the user is looking at: if it changed meanwhile, the server
        // answers CONFLICT instead of rotating a key they never saw.
        () => rotate.mutate({ id: license.id, currentKey: license.key }),
        rotate.isPending,
      )}
    />
  );
}
