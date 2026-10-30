import { ErrorReveal } from "@/components/ErrorReveal";
import { Spinner } from "@/components/ui/Spinner";

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

/** What a refused rotation, delete or issue leaves on screen. */
export interface KeyActionRefusal<M> {
  phase: RotatePhase;
  message: M;
  refetch: boolean;
}

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
export function rotateRefusal(
  code: string | undefined,
): KeyActionRefusal<RotateRefusalMessage> {
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

/** The translated message a refused delete shows (keys of `licenseKeysCard`). */
export type DeleteRefusalMessage =
  | "deleteErrorStale"
  | "deleteErrorUnavailable"
  | "deleteLastKey"
  | "manageErrorRateLimited"
  | "deleteErrorGeneric"
  | null;

/**
 * What a refused delete (`customer.deleteAccountKey`) leaves on screen:
 * - CONFLICT (the key was rotated meanwhile): closes and refetches, so the
 *   user sees the key they would be deleting before they try again.
 * - NOT_FOUND (gone, not theirs, or no longer active): `closed`, refetched;
 *   the row leaves the list.
 * - PRECONDITION_FAILED (their last live key, e.g. another tab deleted the
 *   other one): closes and refetches; the button then shows the reason.
 * - FORBIDDEN: the email step-up; TOO_MANY_REQUESTS: back to idle; anything
 *   else: the confirmation stays, so a retry is one click.
 */
export function deleteRefusal(
  code: string | undefined,
): KeyActionRefusal<DeleteRefusalMessage> {
  switch (code) {
    case "CONFLICT":
      return { phase: "idle", message: "deleteErrorStale", refetch: true };
    case "NOT_FOUND":
      return {
        phase: "closed",
        message: "deleteErrorUnavailable",
        refetch: true,
      };
    case "PRECONDITION_FAILED":
      return { phase: "idle", message: "deleteLastKey", refetch: true };
    case "FORBIDDEN":
      return { phase: "verify", message: null, refetch: false };
    case "TOO_MANY_REQUESTS":
      return {
        phase: "idle",
        message: "manageErrorRateLimited",
        refetch: false,
      };
    default:
      return {
        phase: "confirming",
        message: "deleteErrorGeneric",
        refetch: false,
      };
  }
}

/** The translated message a refused issue shows (keys of `licenseKeysCard`). */
export type IssueRefusalMessage =
  | "issueCap"
  | "issueErrorUnavailable"
  | "manageErrorRateLimited"
  | "issueErrorGeneric"
  | null;

/**
 * What a refused issue (`customer.issueAccountKey`) leaves on screen:
 * - CONFLICT (the cap of live keys, e.g. another tab added one): back to
 *   idle and refetched; the button then shows the cap.
 * - PRECONDITION_FAILED (no live key left, e.g. revoked meanwhile): idle,
 *   refetched; the control then goes away.
 * - FORBIDDEN: the email step-up; TOO_MANY_REQUESTS: back to idle; anything
 *   else: the confirmation stays.
 *
 * Never `closed`: whether issuing is possible changes with the list (a delete
 * frees a slot), and the dashboard decides that from the refetched list.
 */
export function issueRefusal(
  code: string | undefined,
): KeyActionRefusal<IssueRefusalMessage> {
  switch (code) {
    case "CONFLICT":
      return { phase: "idle", message: "issueCap", refetch: true };
    case "PRECONDITION_FAILED":
      return { phase: "idle", message: "issueErrorUnavailable", refetch: true };
    case "FORBIDDEN":
      return { phase: "verify", message: null, refetch: false };
    case "TOO_MANY_REQUESTS":
      return {
        phase: "idle",
        message: "manageErrorRateLimited",
        refetch: false,
      };
    default:
      return {
        phase: "confirming",
        message: "issueErrorGeneric",
        refetch: false,
      };
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
  /**
   * Names the panel's ids (`<idPrefix>-<licenseId>`). Each control under one
   * key needs its own. Defaults to `rotate-key`.
   */
  idPrefix?: string;
  /**
   * Why the action is not available right now (the last live key cannot be
   * deleted; the cap of keys is reached). When set and idle, the button is
   * shown disabled with this reason next to it.
   */
  disabledReason?: string | null;
  /** `danger` draws the confirmation in red (delete). Defaults to amber. */
  tone?: "warning" | "danger";
}

/**
 * The rotate control under one Account Key: a "Rotate key" button, then an
 * inline confirmation that says what rotation does to the user's devices.
 *
 * The delete and "add a new key" controls use the same view with their own
 * labels (`labels.rotate` is the action button's text), `idPrefix`, `tone`
 * and, when the action is not available, `disabledReason`.
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
  idPrefix = "rotate-key",
  disabledReason = null,
  tone = "warning",
}: RotateAccountKeyViewProps) {
  const panelId = `${idPrefix}-${licenseId}`;
  const panelTone =
    tone === "danger"
      ? "border-red-500/30 bg-red-500/10"
      : "border-amber-500/30 bg-amber-500/10";
  const actionTone =
    tone === "danger"
      ? "bg-red-600 hover:bg-red-500"
      : "bg-amber-600 hover:bg-amber-500";
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
      className={`mt-1 rounded-lg border p-3 ${panelTone}`}
      id={panelId}
      role="group"
    >
      <p className="text-sm font-medium text-white" id={`${panelId}-title`}>
        {title}
      </p>
      <p className="mt-1 text-xs text-gray-300">{body}</p>
      <div className="mt-3 flex flex-wrap gap-2">
        <button
          className={`inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-white rounded transition-colors disabled:opacity-50 ${actionTone}`}
          disabled={busy}
          type="button"
          onClick={onAction}
        >
          {busy ? <Spinner className="w-3 h-3" /> : null}
          {busy ? busyLabel : action}
        </button>
        <button
          className="px-3 py-1.5 text-xs text-gray-300 hover:text-white bg-white/5 hover:bg-white/10 rounded transition-colors disabled:opacity-50"
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
      {phase === "idle" && disabledReason ? (
        <p className="flex flex-wrap items-baseline gap-x-2 text-xs">
          <button
            disabled
            aria-describedby={`${panelId}-reason`}
            className="text-gray-600 cursor-not-allowed"
            type="button"
          >
            {labels.rotate}
          </button>
          <span className="text-gray-500" id={`${panelId}-reason`}>
            {disabledReason}
          </span>
        </p>
      ) : null}

      {phase === "idle" && !disabledReason ? (
        <button
          aria-controls={panelId}
          aria-expanded={false}
          className="text-xs text-gray-400 hover:text-white transition-colors"
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
        className="pt-1.5 text-xs text-emerald-300 empty:hidden"
        role="status"
      >
        {notice}
      </p>
      <ErrorReveal className="" open={Boolean(error)}>
        <p className="pt-1.5 text-xs text-red-400 empty:hidden" role="alert">
          {error}
        </p>
      </ErrorReveal>
    </div>
  );
}
