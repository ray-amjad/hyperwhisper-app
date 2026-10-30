import { ErrorReveal } from "@/components/ErrorReveal";
import { Spinner } from "@/components/ui/Spinner";
import { BTN_SECONDARY_XS, BTN_WARN_XS } from "@/components/user/panel-styles";

/** Every string the view shows, already translated by the container. */
export interface RotateAccountKeyLabels {
  rotate: string;
  confirmTitle: string;
  confirmBody: string;
  confirm: string;
  cancel: string;
  rotating: string;
  verifyTitle: string;
  verifyBody: string;
  sendLink: string;
  sendingLink: string;
}

/**
 * - `idle`: the Rotate button.
 * - `confirming` / `rotating`: the confirmation, then the mutation in flight.
 * - `verify` / `sending`: this session was opened with the key itself, which
 *   cannot rotate it; offer an emailed sign-in link instead.
 * - `closed`: rotation can never work for this key (gone, or no longer
 *   active). No button, only the reason: a retry would only spend the limit.
 */
export type RotatePhase =
  | "idle"
  | "confirming"
  | "rotating"
  | "verify"
  | "sending"
  | "closed";

/** The translated message a refusal shows (keys of `licenseKeysCard`). */
export type RotateRefusalMessage =
  | "errorStale"
  | "errorUnavailable"
  | "errorRateLimited"
  | "errorGeneric"
  | null;

/**
 * What a refused rotation leaves on screen, by tRPC error code. Pure, so the
 * rules are tested directly:
 * - CONFLICT (rotated elsewhere): the panel CLOSES and the key is refetched,
 *   so a second Confirm can never rotate the key the user just installed.
 * - NOT_FOUND / PRECONDITION_FAILED: rotation can never work for this key;
 *   `closed`, the reason, and no button (a retry would only spend the limit).
 * - FORBIDDEN: this session cannot rotate; the email step-up opens.
 * - TOO_MANY_REQUESTS: back to idle with the reason; start again later.
 * - anything else (network, server): the confirmation stays; retry is real.
 */
export function rotateRefusal(code: string | undefined): {
  phase: RotatePhase;
  message: RotateRefusalMessage;
  refetch: boolean;
} {
  switch (code) {
    case "CONFLICT":
      return { phase: "idle", message: "errorStale", refetch: true };
    case "NOT_FOUND":
    case "PRECONDITION_FAILED":
      return { phase: "closed", message: "errorUnavailable", refetch: true };
    case "FORBIDDEN":
      return { phase: "verify", message: null, refetch: false };
    case "TOO_MANY_REQUESTS":
      return { phase: "idle", message: "errorRateLimited", refetch: false };
    default:
      return { phase: "confirming", message: "errorGeneric", refetch: false };
  }
}

export interface RotateAccountKeyViewProps {
  /** The key row's id: names the panel for `aria-controls`. */
  licenseId: string;
  phase: RotatePhase;
  labels: RotateAccountKeyLabels;
  /** The success line after a rotation or a sent link, or null. */
  notice: string | null;
  /** The refusal line, or null. */
  error: string | null;
  onStart: () => void;
  onConfirm: () => void;
  onSendLink: () => void;
  onCancel: () => void;
}

/**
 * The rotate control under one Account Key: a "Rotate key" button, then an
 * inline confirmation that says what rotation does to the user's devices.
 *
 * HOLDS NO HOOKS (the `CloudCreditsCardView` split, #737): a test calls it as a
 * function and reaches every `onClick` and every state. `RotateAccountKeyControl`
 * owns the state, the mutation and the translations.
 */
export default function RotateAccountKeyView({
  licenseId,
  phase,
  labels,
  notice,
  error,
  onStart,
  onConfirm,
  onSendLink,
  onCancel,
}: RotateAccountKeyViewProps) {
  const panelId = `rotate-key-${licenseId}`;
  const confirming = phase === "confirming" || phase === "rotating";
  const verifying = phase === "verify" || phase === "sending";
  const busy = phase === "rotating" || phase === "sending";

  const panel = (
    title: string,
    body: string,
    action: string,
    busyLabel: string,
    onAction: () => void,
  ) => (
    <div
      aria-labelledby={`${panelId}-title`}
      className="mt-1.5 rounded-2xl border border-[rgba(253,186,140,.26)] bg-[linear-gradient(180deg,rgba(253,186,140,.09),rgba(255,255,255,.02))] p-4"
      id={panelId}
      role="group"
    >
      <p className="text-sm font-medium text-white" id={`${panelId}-title`}>
        {title}
      </p>
      <p className="mt-1 text-[13px] leading-[1.5] text-[var(--dusk-mute)]">{body}</p>
      <div className="mt-3 flex flex-wrap gap-2">
        <button
          className={BTN_WARN_XS}
          disabled={busy}
          type="button"
          onClick={onAction}
        >
          {busy ? <Spinner className="w-3.5 h-3.5" /> : null}
          {busy ? busyLabel : action}
        </button>
        <button
          className={BTN_SECONDARY_XS}
          disabled={busy}
          type="button"
          onClick={onCancel}
        >
          {labels.cancel}
        </button>
      </div>
    </div>
  );

  return (
    <div className="pt-2">
      {phase === "idle" ? (
        <button
          aria-controls={panelId}
          aria-expanded={false}
          className="rounded text-xs text-[var(--dusk-mute)] underline decoration-white/20 underline-offset-4 transition-colors hover:text-white hover:decoration-white/50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
          type="button"
          onClick={onStart}
        >
          {labels.rotate}
        </button>
      ) : null}

      {confirming
        ? panel(
            labels.confirmTitle,
            labels.confirmBody,
            labels.confirm,
            labels.rotating,
            onConfirm,
          )
        : null}

      {verifying
        ? panel(
            labels.verifyTitle,
            labels.verifyBody,
            labels.sendLink,
            labels.sendingLink,
            onSendLink,
          )
        : null}

      <p
        aria-live="polite"
        className="pt-1.5 text-xs text-[var(--dusk-lav)] empty:hidden"
        role="status"
      >
        {notice}
      </p>
      <ErrorReveal className="" open={Boolean(error)}>
        <p className="pt-1.5 text-xs text-rose-300 empty:hidden" role="alert">
          {error}
        </p>
      </ErrorReveal>
    </div>
  );
}
