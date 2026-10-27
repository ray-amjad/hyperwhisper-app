"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";

import RotateAccountKeyView, { type RotatePhase } from "./RotateAccountKeyView";

import { api } from "@/lib/trpc/client";

interface RotateAccountKeyControlProps {
  license: { id: string; key: string };
  /** Called after a rotation lands, so the card can show the new key. */
  onRotated: () => void;
}

/**
 * The stateful half of the rotate control: the confirmation step, the
 * `customer.rotateAccountKey` mutation, and the translated copy. The markup
 * is `RotateAccountKeyView`, which holds no hooks.
 *
 * After a rotation the dashboard's `licensesWithCredits` cache is patched with
 * the new key at once (so the card shows it with no flash of the dead one) and
 * then refetched. A CONFLICT means another rotation got there first: the
 * refetch brings the current key.
 */
export default function RotateAccountKeyControl({
  license,
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
      setNotice(t("rotated"));
      onRotated();
    },
    onError: (err) => {
      const code = err.data?.code;

      if (code === "CONFLICT")
        void utils.customer.licensesWithCredits.invalidate();
      setPhase("confirming");
      setError(
        code === "TOO_MANY_REQUESTS"
          ? t("errorRateLimited")
          : code === "CONFLICT"
            ? t("errorStale")
            : t("errorGeneric"),
      );
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
        rotate.mutate({ id: license.id, currentKey: license.key });
      }}
      onStart={() => {
        setNotice(null);
        setError(null);
        setPhase("confirming");
      }}
    />
  );
}
