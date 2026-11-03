"use client";

import type { KeyActionRefusal, RotatePhase } from "./RotateAccountKeyView";

import { useState } from "react";
import { useTranslations } from "next-intl";

import { authClient } from "@/src/lib/auth-client";
import { api } from "@/lib/trpc/client";

/** The notice each control posts once its sign-in link is sent. */
export type KeyActionLinkSentKey =
  | "linkSent"
  | "deleteLinkSent"
  | "issueLinkSent";

/** Translates a `licenseKeysCard` key, with optional ICU arguments. */
export type KeyActionTranslate = (
  key: string,
  values?: Record<string, string | number>,
) => string;

/** What `keyActionHandlers` reads and writes. The hook supplies the real ones. */
export interface KeyActionDeps {
  /** The account's address, where the sign-in link is sent. */
  email: string;
  /** Whether this session was proven by email (else Start opens the step-up). */
  emailProven: boolean;
  linkSentKey: KeyActionLinkSentKey;
  onNotice: (text: string | null) => void;
  setPhase: (phase: RotatePhase) => void;
  setError: (error: string | null) => void;
  translate: KeyActionTranslate;
  /** Refetches the dashboard's `licensesWithCredits` list. */
  refetchKeys: () => void;
  /** Sends the magic link to `email`, back to this page. */
  sendMagicLink: (email: string) => Promise<{ error: unknown }>;
}

/** The four callbacks `RotateAccountKeyView` takes for the step-up flow. */
export interface KeyActionViewHandlers {
  onStart: () => void;
  onCancel: () => void;
  onConfirm: () => void;
  onSendLink: () => void;
}

/**
 * The email step-up state machine shared by the rotate, delete and issue
 * controls, with every collaborator passed in. Pure (no React), so the
 * transitions are tested directly; `useKeyAction` wires it to state.
 */
export function keyActionHandlers(deps: KeyActionDeps) {
  const {
    email,
    emailProven,
    linkSentKey,
    onNotice,
    setPhase,
    setError,
    translate,
    refetchKeys,
    sendMagicLink,
  } = deps;

  /** What a refused mutation leaves on screen (the control's own mapper). */
  function onRefusal(
    refusal: KeyActionRefusal<string | null>,
    tArgs?: Record<string, string | number>,
  ) {
    if (refusal.refetch) refetchKeys();
    setPhase(refusal.phase);
    setError(refusal.message ? translate(refusal.message, tArgs) : null);
  }

  async function sendLink() {
    setError(null);
    setPhase("sending");
    const { error: linkError } = await sendMagicLink(email);

    if (linkError) {
      setPhase("verify");
      setError(translate("linkFailed"));

      return;
    }

    setPhase("idle");
    onNotice(translate(linkSentKey));
  }

  /**
   * The view's callbacks. `confirm` is the control's own `mutate` call;
   * `isPending` is its mutation's flag, so a double click never sends twice.
   */
  function viewHandlers(
    confirm: () => void,
    isPending: boolean,
  ): KeyActionViewHandlers {
    return {
      onStart: () => {
        onNotice(null);
        setError(null);
        setPhase(emailProven ? "confirming" : "verify");
      },
      onCancel: () => {
        setError(null);
        setPhase("idle");
      },
      onConfirm: () => {
        if (isPending) return;
        setError(null);
        setPhase("rotating");
        confirm();
      },
      onSendLink: () => {
        void sendLink();
      },
    };
  }

  return { onRefusal, sendLink, viewHandlers };
}

interface UseKeyActionOptions {
  email: string;
  emailProven: boolean;
  onNotice: (text: string | null) => void;
  linkSentKey: KeyActionLinkSentKey;
}

/**
 * The `phase`/`error` state and the step-up flow of one Account Key control.
 * Each control keeps its own mutation, cache patch, labels and refusal mapper,
 * and passes the mapped refusal to `onRefusal`.
 */
export function useKeyAction({
  email,
  emailProven,
  onNotice,
  linkSentKey,
}: UseKeyActionOptions) {
  const t = useTranslations("licenseKeysCard");
  const utils = api.useUtils();
  const [phase, setPhase] = useState<RotatePhase>("idle");
  const [error, setError] = useState<string | null>(null);

  const handlers = keyActionHandlers({
    email,
    emailProven,
    linkSentKey,
    onNotice,
    setPhase,
    setError,
    translate: (key, values) => t(key, values),
    refetchKeys: () => {
      void utils.customer.licensesWithCredits.invalidate();
    },
    // Back to this page once the link is followed, with an email session.
    sendMagicLink: (to) =>
      authClient.signIn.magicLink({
        email: to,
        callbackURL: window.location.pathname,
      }),
  });

  return {
    phase,
    setPhase,
    error,
    setError,
    onRefusal: handlers.onRefusal,
    viewHandlers: handlers.viewHandlers,
  };
}
