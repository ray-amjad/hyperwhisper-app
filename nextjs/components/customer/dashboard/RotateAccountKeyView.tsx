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
}

export type RotatePhase = "idle" | "confirming" | "rotating";

export interface RotateAccountKeyViewProps {
  /** The key row's id: names the confirmation panel for `aria-controls`. */
  licenseId: string;
  phase: RotatePhase;
  labels: RotateAccountKeyLabels;
  /** The success line after a rotation, or null. */
  notice: string | null;
  /** The refusal line, or null. */
  error: string | null;
  onStart: () => void;
  onConfirm: () => void;
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
  onCancel,
}: RotateAccountKeyViewProps) {
  const panelId = `rotate-key-${licenseId}`;
  const open = phase !== "idle";
  const busy = phase === "rotating";

  return (
    <div className="pt-2">
      {open ? null : (
        <button
          aria-controls={panelId}
          aria-expanded={false}
          className="text-xs text-gray-400 hover:text-white transition-colors"
          type="button"
          onClick={onStart}
        >
          {labels.rotate}
        </button>
      )}

      {open ? (
        <div
          aria-labelledby={`${panelId}-title`}
          className="mt-1 rounded-lg border border-amber-500/30 bg-amber-500/10 p-3"
          id={panelId}
          role="group"
        >
          <p className="text-sm font-medium text-white" id={`${panelId}-title`}>
            {labels.confirmTitle}
          </p>
          <p className="mt-1 text-xs text-gray-300">{labels.confirmBody}</p>
          <div className="mt-3 flex flex-wrap gap-2">
            <button
              className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-white bg-amber-600 hover:bg-amber-500 rounded transition-colors disabled:opacity-50"
              disabled={busy}
              type="button"
              onClick={onConfirm}
            >
              {busy ? <Spinner className="w-3 h-3" /> : null}
              {busy ? labels.rotating : labels.confirm}
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
      ) : null}

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
