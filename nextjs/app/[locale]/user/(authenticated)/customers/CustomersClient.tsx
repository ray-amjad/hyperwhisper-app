"use client";

import {
  useState,
  useEffect,
  useLayoutEffect,
  useRef,
  type CSSProperties,
  type ReactNode,
} from "react";
import { AnimatePresence, m } from "framer-motion";
import { keepPreviousData } from "@tanstack/react-query";
import { api } from "@/lib/trpc/client";
import { errorSentence } from "@/lib/trpc/input-error";
import { formatDate } from "@/lib/format-date";
import { ICON_SWAP } from "@/lib/icon-swap";
import { CREDITS_PER_MINUTE } from "@/lib/credits-per-minute";
import { AnimatedDialog } from "@/components/AnimatedDialog";
import { ErrorReveal } from "@/components/ErrorReveal";
import { Spinner } from "@/components/ui/Spinner";
import {
  ALERT_ERROR,
  ALERT_OK,
  BTN_DANGER,
  BTN_PRIMARY,
  BTN_PRIMARY_XS,
  BTN_SECONDARY,
  BTN_SECONDARY_XS,
  CARD,
  CHIP,
  CHIP_LAV,
  CHIP_MUTE,
  CHIP_PEACH,
  CHIP_ROSE,
  ICON_BTN,
  INPUT,
  LABEL,
  PAGE_SUB,
  PAGE_TITLE,
  SKEL,
  TBODY,
  TH,
  TR_HOVER,
} from "@/components/user/panel-styles";

/** A small inline row action (Refund, Add credits), as a pill chip. */
const ROW_ACTION =
  "inline-flex items-center rounded-full px-2.5 py-0.5 text-[11.5px] font-medium ring-1 ring-inset transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] disabled:opacity-40 disabled:cursor-not-allowed";

/**
 * Windowed list of page numbers to render in the pager: always page 1,
 * the last page, the current page, and current±1, with "ellipsis" markers
 * standing in for any gaps. Keeps the control a fixed, scannable width even
 * when totalPages is large, instead of rendering every page number.
 */
function getPageNumbers(
  currentPage: number,
  totalPages: number
): Array<number | "ellipsis"> {
  const pages = new Set<number>();
  pages.add(1);
  pages.add(totalPages);
  for (let p = currentPage - 1; p <= currentPage + 1; p++) {
    if (p >= 1 && p <= totalPages) pages.add(p);
  }

  const sorted = Array.from(pages).sort((a, b) => a - b);
  const result: Array<number | "ellipsis"> = [];
  let previous: number | null = null;
  for (const p of sorted) {
    if (previous !== null && p - previous > 1) {
      result.push("ellipsis");
    }
    result.push(p);
    previous = p;
  }
  return result;
}

// #1155: shown in place of a zod input failure's serialized issue array.
const INVALID_EMAIL = "Enter a valid email address.";
const INVALID_AMOUNT =
  "Enter a credit amount above 0 and no more than 1,000,000.";
const INVALID_INPUT =
  "That request was not valid. Refresh the page and try again.";

function formatCredits(credits: number) {
  return credits.toLocaleString("en-US", {
    minimumFractionDigits: 0,
    maximumFractionDigits: 2,
  });
}

/**
 * #966: a customer's pooled balance pops in, one character at a time, when it
 * changes (Add credits, or any refetch that changes it), the way the
 * /credits summary values do (#768, `PopValue` in CreditsPurchase.tsx). The
 * first render is the plain text, so the first load, a page change or a search
 * animates nothing: a row is keyed by its customer, so only the row whose
 * value changed pops. Each later change remounts the characters (the key),
 * which replays their `.t-number-pop-char` animation; under reduced motion
 * that rule shows them at once. A screen reader reads the whole value from the
 * `sr-only` copy; the split characters are hidden from it.
 */
function PopCredits({ text }: { text: string }) {
  const [shown, setShown] = useState(text);
  const [changes, setChanges] = useState(0);

  // The "adjust state while rendering" pattern from the React docs: no effect,
  // so the new value never paints once without its animation.
  if (text !== shown) {
    setShown(text);
    setChanges(changes + 1);
  }

  if (changes === 0) return text;

  // nowrap: the split characters are inline-blocks, and the line may not
  // break between them where the plain text could not break.
  return (
    <span key={changes} className="whitespace-nowrap">
      <span className="sr-only">{text}</span>
      {Array.from(text).map((char, index) => (
        <span
          key={index}
          aria-hidden="true"
          className="t-number-pop-char"
          style={{ animationDelay: `${index * 40}ms` }}
        >
          {char}
        </span>
      ))}
    </span>
  );
}

// #1663: account_keys.status is "granted" or "revoked"; show a word, not the enum.
function licenseStatusLabel(status: string) {
  if (status === "granted") return "Active";
  if (status === "revoked") return "Revoked";

  return status.charAt(0).toUpperCase() + status.slice(1);
}

const currencyFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
});

function formatCurrency(cents: number) {
  return currencyFormatter.format(cents / 100);
}

// The height tween: 300 ms on --motion-ease-smooth-out, the `01-card-resize`
// timing #916 asked for.
const HEIGHT_TWEEN_MS = 300;
const HEIGHT_TRANSITION = `height ${HEIGHT_TWEEN_MS}ms var(--motion-ease-smooth-out)`;

/**
 * #916: a box whose content changes height in one commit (a search narrows
 * 100 rows to 2, or clearing it brings them back) tweens to the new height
 * instead of stepping. A ResizeObserver keeps the box's last settled height;
 * when `trigger` changes, the layout effect pins the box at that height, then
 * sets the new one, and the height transition tweens between the two. The pin
 * comes off when the tween ends, so at rest the box is `height: auto` and an
 * inline editor or an error reveal inside it sizes it at once, as before. The
 * same pin-then-release as SignInClient (#752) and the /download panel (#767).
 * Under reduced motion nothing is pinned: the box takes its new height in the
 * same frame as the new content, as before.
 *
 * Only a change from one value of `trigger` to another tweens: the first
 * value after `undefined` (the first load's rows replacing the skeleton)
 * lands at once, as before.
 *
 * `transition` is any other transition the box keeps (the table's in-flight
 * dim); it is listed beside height, never as `all` (#668).
 */
function HeightTween({
  trigger,
  className,
  transition,
  children,
}: {
  trigger: unknown;
  className?: string;
  transition?: string;
  children: ReactNode;
}) {
  const box = useRef<HTMLDivElement>(null);
  const content = useRef<HTMLDivElement>(null);
  const settledHeight = useRef<number | null>(null);
  const previousTrigger = useRef(trigger);
  // The tween in flight: `stop` ends it (a second change mid-tween starts
  // from where the box is now), `retarget` moves its end height.
  const tween = useRef<{ stop: () => void; retarget: () => void } | null>(null);

  useEffect(() => {
    const element = box.current;
    const inner = content.current;

    if (!element || !inner) return;

    // Reports after layout and before paint, so it always holds the height
    // the last painted frame showed. Mid-tween the box is pinned, so a change
    // of the content's own size (an editor opening) moves the pin instead:
    // nothing is clipped, and nothing jumps when the pin comes off.
    const observer = new ResizeObserver(() => {
      if (tween.current) tween.current.retarget();
      else settledHeight.current = element.offsetHeight;
    });

    observer.observe(element);
    observer.observe(inner);

    return () => {
      observer.disconnect();
      tween.current?.stop();
    };
  }, []);

  useLayoutEffect(() => {
    const element = box.current;
    const inner = content.current;
    const previous = previousTrigger.current;

    previousTrigger.current = trigger;
    if (!element || !inner || previous === undefined) return;

    let from = settledHeight.current;

    if (tween.current) {
      // The computed height mid-transition is where the box is drawn now
      // (offsetHeight would read the pinned target).
      from = parseFloat(getComputedStyle(element).height);
      tween.current.stop();
    }

    // The two-frame start below would hold the old height for those frames,
    // and the global guard would then end the transition at once: a late step
    // instead of an on-time one.
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

    const to = element.offsetHeight;

    if (from === null || Math.abs(from - to) < 1) return;

    // Pin the box where it was drawn; the new content must not spill out of
    // it while it is still shorter.
    element.style.height = `${from}px`;
    element.style.overflow = "clip";

    const target = () =>
      inner.offsetHeight + element.offsetHeight - element.clientHeight;
    let started = false;
    let timer = 0;
    let frame = 0;

    const unpin = () => {
      window.cancelAnimationFrame(frame);
      window.clearTimeout(timer);
      element.removeEventListener("transitionend", onEnd);
      element.style.height = "";
      element.style.overflow = "";
      tween.current = null;
      settledHeight.current = element.offsetHeight;
    };
    // Children's transitions bubble here too; only the box's own counts.
    const onEnd = (event: TransitionEvent) => {
      if (event.target === element && event.propertyName === "height") unpin();
    };
    // A transition that never runs fires no transitionend.
    const bound = () => {
      window.clearTimeout(timer);
      timer = window.setTimeout(unpin, HEIGHT_TWEEN_MS + 400);
    };

    // The tween starts two frames later, after the frame that draws the new
    // content: a 100-row page takes one frame of ~250 ms to lay out and
    // paint, and a transition started inside it is ~99% done by the first
    // frame anyone sees (verify on #916). The first callback runs before that
    // frame is drawn, the second in the frame after it.
    frame = window.requestAnimationFrame(() => {
      frame = window.requestAnimationFrame(() => {
        started = true;
        element.style.height = `${target()}px`;
        bound();
      });
    });

    element.addEventListener("transitionend", onEnd);
    tween.current = {
      stop: unpin,
      retarget: () => {
        // Before the start the box stays pinned: the content is still being
        // drawn, and the start reads its height then.
        if (!started) return;
        const height = target();

        // Its own frames report too; a pixel of rounding is not a change.
        if (Math.abs(parseFloat(element.style.height) - height) < 2) return;
        element.style.height = `${height}px`;
        bound();
      },
    };
  }, [trigger]);

  const style: CSSProperties = {
    transition: transition
      ? `${transition}, ${HEIGHT_TRANSITION}`
      : HEIGHT_TRANSITION,
  };

  // flow-root: the content's own margins count in its measured height.
  return (
    <div ref={box} className={className} style={style}>
      <div ref={content} className="flow-root">
        {children}
      </div>
    </div>
  );
}

export default function CustomersClient() {
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");
  const [page, setPage] = useState(1);

  // A new search always starts back at page 1 — otherwise a search narrow
  // enough to have fewer pages than the current page number would request a
  // page that no longer exists. Reset page in the SAME batch as the
  // debouncedSearch update (rather than in a separate effect keyed off
  // debouncedSearch) so there's never a render where the query sees the new
  // search term paired with the stale page — that extra render was firing a
  // wasted request (including the Stripe spend fan-out server-side).
  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearch(search);
      setPage(1);
    }, 300);
    return () => clearTimeout(timer);
  }, [search]);

  // Timers that fire after a delay and call setState/refetch — tracked so they
  // can be cleared on unmount to avoid set-after-unmount warnings and wasted
  // authenticated refetches.
  const refundTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const copyTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    return () => {
      if (refundTimerRef.current) clearTimeout(refundTimerRef.current);
      if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    };
  }, []);

  const {
    data,
    isLoading: loading,
    isFetching,
    error: queryError,
    refetch,
  } = api.admin.customers.list.useQuery(
    { search: debouncedSearch || undefined, page },
    // Keep showing the previous page's rows while a new page (or search)
    // loads, instead of dropping to the loading skeleton on every page
    // change — that flash is exactly what the pager is meant to avoid.
    { placeholderData: keepPreviousData }
  );

  // True during any background refetch (page change, search, manual
  // refresh) — including the keepPreviousData window where `loading` stays
  // false and the table still shows the previous page's rows. Used to give a
  // subtle in-flight signal so a page-button click doesn't look like it did
  // nothing.
  const isTransitioning = isFetching && !loading;

  // The server clamps an out-of-range page to the last valid one and echoes
  // the clamped value back as data.page. Sync local page state to that once
  // a fetch completes, so the pager (highlighted page, Prev/Next disabled
  // state) never disagrees with what was actually fetched — e.g. an admin
  // sitting on the last page when the result set shrinks out from under
  // them. Guarded by the equality check, so this settles rather than loops.
  //
  // Gated on !isFetching: while a new page/search request is in flight,
  // keepPreviousData keeps `data` pointing at the PREVIOUS page's response,
  // so data.page briefly disagrees with the just-clicked `page` even though
  // nothing needs correcting — that's expected transient staleness, not a
  // server clamp. Without this gate, clicking a new page would fire this
  // effect on that intermediate render and immediately revert `page` back
  // before the real response ever arrived. Only sync once the fetch settles
  // and `data` is confirmed fresh for the current request.
  useEffect(() => {
    if (data && !isFetching && data.page !== page) {
      setPage(data.page);
    }
  }, [data, isFetching, page]);

  const [showGrant, setShowGrant] = useState(false);
  const [grantEmail, setGrantEmail] = useState("");
  const [grantResult, setGrantResult] = useState<{ email: string; licenseKey: string } | null>(null);

  const grantMutation = api.admin.customers.grant.useMutation({
    onSuccess: (data) => {
      setGrantResult(data);
      setGrantEmail("");
      // A newly granted customer sorts first (newest createdAt wins page 1),
      // so jump back to page 1 — otherwise an admin on page 3+ sees no
      // visible change after granting.
      setPage(1);
      void refetch();
    },
  });

  // Refund modal state
  const [refundTarget, setRefundTarget] = useState<{
    id: string;
    email: string;
    key: string;
  } | null>(null);

  const refundMutation = api.admin.customers.refund.useMutation({
    onSuccess: () => {
      refundTimerRef.current = setTimeout(() => {
        setRefundTarget(null);
        refundMutation.reset();
        void refetch();
      }, 1500);
    },
  });

  // Add-credits modal state. Credits are pooled per account, so the target is
  // the customer: `id` is any one of their license ids, which the server maps
  // to the owning user's single wallet.
  const [creditTarget, setCreditTarget] = useState<{
    id: string;
    email: string;
    credits: number;
  } | null>(null);
  const [creditAmount, setCreditAmount] = useState("");

  const addCreditsMutation = api.admin.customers.addCredits.useMutation({
    onSuccess: () => {
      setCreditTarget(null);
      setCreditAmount("");
      void refetch();
    },
  });

  function openAddCredits(target: {
    id: string;
    email: string;
    credits: number;
  }) {
    setCreditTarget(target);
    setCreditAmount("");
    addCreditsMutation.reset();
  }

  function handleAddCredits(e: React.FormEvent) {
    e.preventDefault();
    if (!creditTarget) return;
    const amount = parseFloat(creditAmount);
    if (isNaN(amount) || amount <= 0) return;
    addCreditsMutation.mutate({ licenseKeyId: creditTarget.id, amount });
  }

  // Inline email-edit state
  const [editingUserId, setEditingUserId] = useState<string | null>(null);
  const [editEmail, setEditEmail] = useState("");
  // Session-only marker for customers whose email was edited in this view.
  const [editedUserIds, setEditedUserIds] = useState<Set<string>>(new Set());

  const updateEmailMutation = api.admin.customers.updateEmail.useMutation({
    onSuccess: (_data, variables) => {
      setEditingUserId(null);
      setEditedUserIds((prev) => {
        const next = new Set(prev);
        next.add(variables.userId);
        return next;
      });
      void refetch();
    },
  });

  function startEdit(userId: string, email: string) {
    setEditingUserId(userId);
    setEditEmail(email);
    updateEmailMutation.reset();
  }

  function cancelEdit() {
    setEditingUserId(null);
    updateEmailMutation.reset();
  }

  const [copiedKey, setCopiedKey] = useState<string | null>(null);

  async function copyKey(key: string) {
    try {
      await navigator.clipboard.writeText(key);
      setCopiedKey(key);
      if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
      copyTimerRef.current = setTimeout(() => setCopiedKey(null), 2000);
    } catch {
      // Clipboard API unavailable (e.g. insecure context) — ignore.
    }
  }

  const customers = data?.customers ?? [];
  const error = queryError?.message ?? null;

  return (
    <div className="space-y-6">
      {/* Page Header. Wraps on a phone, so the buttons never push the page
          wider than the viewport (#1330). */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className={PAGE_TITLE}>Customers</h1>
          <p className={PAGE_SUB}>
            All customers with their license keys and credit balances
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <button
            onClick={() => { setShowGrant(!showGrant); setGrantResult(null); grantMutation.reset(); }}
            className={BTN_PRIMARY}
          >
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M12 4v16m8-8H4" />
            </svg>
            Grant Account Key
          </button>
          <button
            onClick={() => refetch()}
            disabled={isFetching}
            className={BTN_SECONDARY}
          >
            {isFetching ? (
              <Spinner className="w-4 h-4" />
            ) : (
              <svg
                className="w-4 h-4"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={1.5}
                  d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"
                />
              </svg>
            )}
            Refresh
          </button>
        </div>
      </div>

      {/* Search */}
      <div>
        <input
          type="text"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search by email..."
          className={`w-full max-w-md px-4 py-2.5 text-sm ${INPUT}`}
        />
      </div>

      {/* Grant License Form. It grows open from 0fr (#2285, as #702 did for
          the other disclosure panels) instead of arriving at full height in
          one frame, so the rows under it slide down. The parent's space-y-6
          gap is dropped from the wrapper (mb-0) and carried as padding inside
          the clip, so the gap grows with the tween instead of landing in the
          first frame. */}
      {showGrant && (
        <div className="t-cell-reveal mb-0">
          <div>
            <div className="pb-6">
              <div className={`${CARD} p-5 space-y-3`}>
                <h3 className={LABEL}>Grant Account Key by Email</h3>
                <form
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (grantEmail.trim()) {
                      setGrantResult(null);
                      grantMutation.mutate({ email: grantEmail.trim() });
                    }
                  }}
                  className="flex flex-wrap items-center gap-3"
                >
                  <input
                    type="email"
                    value={grantEmail}
                    onChange={(e) => setGrantEmail(e.target.value)}
                    placeholder="user@example.com"
                    required
                    className={`min-w-0 flex-1 basis-56 px-3.5 py-2 text-sm ${INPUT}`}
                  />
                  <button
                    type="submit"
                    disabled={grantMutation.isPending}
                    className={BTN_PRIMARY}
                  >
                    {grantMutation.isPending ? "Granting..." : "Grant"}
                  </button>
                </form>
                {/* #2286: the error reveals. The wrapper is always mounted, so
                    the form above is never the card's last child and keeps
                    its space-y-3 margin: -mt-3 cancels that while closed, and
                    pt-3 inside the clip carries the gap when open. */}
                <ErrorReveal className="-mt-3" gap="pt-3">
                  {grantMutation.error && (
                    <p className="text-rose-300 text-sm" role="alert">
                      {errorSentence(grantMutation.error, INVALID_EMAIL)}
                    </p>
                  )}
                </ErrorReveal>
                {/* #965: the granted key rises out of a small blur
                    (.t-panel-content) while the box grows to it
                    (HeightTween), so the panel grows instead of stepping
                    ~80 px in one frame. A closed box takes no space: -mt-3
                    cancels the space-y-3 gap and pt-3 carries it inside. */}
                <HeightTween className="-mt-3" trigger={grantResult}>
                  {grantResult && (
                    <div className="pt-3">
                      <div
                        className={`t-panel-content p-3.5 space-y-1 ${ALERT_OK}`}
                        data-enter=""
                      >
                        <p className="text-[var(--dusk-lav)] text-sm">Account Key granted to <span className="font-medium">{grantResult.email}</span></p>
                        <p className="text-white font-mono text-sm select-all break-all">{grantResult.licenseKey}</p>
                      </div>
                    </div>
                  )}
                </HeightTween>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Error Message. It reveals (#2286) instead of arriving at full height
          in one frame; the space-y-6 gap rides inside the clip as pb-6. */}
      <ErrorReveal gap="pb-6">
        {error && (
          <div className={`p-4 ${ALERT_ERROR}`} role="alert">
            <p className="text-rose-300">{error}</p>
          </div>
        )}
      </ErrorReveal>

      {/* Customers Table. A search that narrows 100 rows to 2, or clearing
          it, tweens the card's height (#916) instead of stepping ~5,800 px in
          one frame; the clearing path comes from the query cache, so the dim
          below never fires there and the tween is its only feedback. */}
      <HeightTween
        className={`${CARD} overflow-hidden ${
          isTransitioning ? "opacity-50" : "opacity-100"
        }`}
        // Tailwind's transition-opacity, kept beside the height tween.
        transition="opacity 150ms cubic-bezier(0.4, 0, 0.2, 1)"
        trigger={data}
      >
        <div className="overflow-x-auto">
          {/* min-w (#1330): the email wraps anywhere, so without a floor a
              phone-width column squeezes it to a letter or two per line. This
              keeps the columns readable and lets the wrapper scroll instead. */}
          <table className="w-full min-w-[48rem]">
            <thead>
              <tr className="border-b border-[var(--dusk-line)] bg-white/[.02]">
                {/* Fixed width (#914): the inline email editor fits inside it, so
                    opening it never widens the column or re-wraps other rows. */}
                <th className={`w-[24rem] ${TH}`}>
                  Email
                </th>
                <th className={TH}>
                  Account Keys
                </th>
                <th className={TH}>
                  Total
                </th>
                <th className={TH}>
                  Total Spend
                </th>
                <th className={TH}>
                  Created
                </th>
              </tr>
            </thead>
            <tbody className={TBODY}>
              {loading ? (
                Array.from({ length: 5 }).map((_, i) => (
                  <tr key={i}>
                    {Array.from({ length: 5 }).map((_, j) => (
                      <td key={j} className="px-6 py-4">
                        <div className={`h-4 w-24 ${SKEL}`} />
                      </td>
                    ))}
                  </tr>
                ))
              ) : customers.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-6 py-14 text-center text-[var(--dusk-mute)]">
                    No customers found.
                  </td>
                </tr>
              ) : (
                customers.map((customer) => {
                  const isEditing = editingUserId === customer.userId;
                  return (
                    <tr
                      key={customer.userId}
                      className={`${TR_HOVER} align-top`}
                    >
                      {/* Email + inline edit */}
                      <td className="px-6 py-4">
                        {isEditing ? (
                          // w-0 + min-w-full: the editor adds no intrinsic width to the
                          // column, it fills the fixed one (#914). t-cell-reveal tweens it in.
                          // The input is w-0 flex-1: its default size=20 width (~228px) would
                          // otherwise set the form's min-content and overflow the cell.
                          <div className="t-cell-reveal w-0 min-w-full">
                            <form
                              onSubmit={(e) => {
                                e.preventDefault();
                                const value = editEmail.trim();
                                if (value) {
                                  updateEmailMutation.mutate({
                                    userId: customer.userId,
                                    newEmail: value,
                                  });
                                }
                              }}
                              className="flex flex-col gap-2"
                            >
                              <div className="flex items-center gap-2">
                                <input
                                  type="email"
                                  value={editEmail}
                                  onChange={(e) => setEditEmail(e.target.value)}
                                  // Opening the editor is the admin's own click on its
                                  // pencil, so focus follows it there (#914).
                                  // eslint-disable-next-line jsx-a11y/no-autofocus
                                  autoFocus
                                  required
                                  className={`px-2.5 py-1 rounded-lg text-sm w-0 min-w-0 flex-1 ${INPUT}`}
                                />
                                <button
                                  type="submit"
                                  disabled={updateEmailMutation.isPending}
                                  className={BTN_PRIMARY_XS}
                                >
                                  {updateEmailMutation.isPending ? "Saving..." : "Save"}
                                </button>
                                <button
                                  type="button"
                                  onClick={cancelEdit}
                                  disabled={updateEmailMutation.isPending}
                                  className={BTN_SECONDARY_XS}
                                >
                                  Cancel
                                </button>
                              </div>
                              <p className="text-[var(--dusk-peach)]/85 text-xs">
                                Updates this customer&apos;s account email and moves
                                {" "}
                                {customer.licenseCount === 1
                                  ? "their Account Key"
                                  : `all ${customer.licenseCount} Account Keys`}
                                {" "}
                                to the new address.
                              </p>
                              {/* #2286: the error reveals. A closed wrapper
                                  still takes the form's gap-2, so -mt-2 cancels
                                  it and pt-2 inside the clip carries it open. */}
                              <ErrorReveal className="-mt-2" gap="pt-2">
                                {updateEmailMutation.error && (
                                  <p
                                    className="text-rose-300 text-xs"
                                    role="alert"
                                  >
                                    {errorSentence(updateEmailMutation.error, INVALID_EMAIL)}
                                  </p>
                                )}
                              </ErrorReveal>
                            </form>
                          </div>
                        ) : (
                          <div className="flex flex-col items-start gap-1.5">
                            {/* #1663: the pencil rides in the email's own text run,
                                glued to its last character, so a wrap never leaves
                                it alone on a line under the address. */}
                            <p className="text-[var(--dusk-ink)] wrap-anywhere">
                              {customer.email.slice(0, -1)}
                              <span className="whitespace-nowrap">
                                {customer.email.slice(-1)}
                                <button
                                  type="button"
                                  onClick={() =>
                                    startEdit(customer.userId, customer.email)
                                  }
                                  disabled={isTransitioning}
                                  title="Edit email"
                                  aria-label="Edit email"
                                  className="ml-1.5 inline-flex align-[-2px] rounded text-[var(--dusk-dim)] hover:text-[var(--dusk-lav)] transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:text-[var(--dusk-dim)]"
                                >
                                  <svg
                                    className="h-3.5 w-3.5"
                                    fill="none"
                                    viewBox="0 0 24 24"
                                    strokeWidth={1.5}
                                    stroke="currentColor"
                                  >
                                    <path
                                      strokeLinecap="round"
                                      strokeLinejoin="round"
                                      d="M16.862 4.487l1.687-1.688a1.875 1.875 0 112.652 2.652L10.582 16.07a4.5 4.5 0 01-1.897 1.13L6 18l.8-2.685a4.5 4.5 0 011.13-1.897l8.932-8.931zm0 0L19.5 7.125"
                                    />
                                  </svg>
                                </button>
                              </span>
                            </p>
                            {(customer.licenseCount > 1 ||
                              editedUserIds.has(customer.userId)) && (
                              <div className="flex items-center gap-2 flex-wrap">
                                {customer.licenseCount > 1 && (
                                  <span className={`${CHIP} ${CHIP_LAV}`}>
                                    {customer.licenseCount} licenses
                                  </span>
                                )}
                                {editedUserIds.has(customer.userId) && (
                                  <span className={`${CHIP} ${CHIP_MUTE}`}>
                                    Edited
                                  </span>
                                )}
                              </div>
                            )}
                          </div>
                        )}
                      </td>

                      {/* License keys (one per license, each copyable + refundable).
                          #1663: one shared grid, each license a `contents` row, so
                          key / status / Refund line up in columns whether or not
                          a row has a Refund. The balance is pooled per account,
                          so it and Add credits show once, in Total, not per key. */}
                      <td className="px-6 py-4">
                        <div className="grid grid-cols-[repeat(3,max-content)] items-center gap-x-2 gap-y-2">
                          {customer.licenses.map((license) => (
                            <div key={license.id} className="contents">
                              <button
                                type="button"
                                onClick={() => copyKey(license.key)}
                                title="Click to copy Account Key"
                                aria-label={
                                  copiedKey === license.key
                                    ? "Account Key copied"
                                    : "Copy Account Key"
                                }
                                className="group relative inline-flex items-center gap-1.5 rounded-lg border border-[#2a2e4a] bg-[#090b18] px-2 py-1 font-mono text-xs text-[var(--dusk-ink)] transition-colors hover:border-[rgba(196,181,253,.45)] hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
                              >
                                <code>{license.key.slice(0, 8)}...</code>
                                <AnimatePresence
                                  initial={false}
                                  mode="popLayout"
                                >
                                  <m.span
                                    key={
                                      copiedKey === license.key
                                        ? "copied"
                                        : "copy"
                                    }
                                    className="inline-flex"
                                    {...ICON_SWAP}
                                  >
                                    {copiedKey === license.key ? (
                                      <svg
                                        className="h-3.5 w-3.5 text-[var(--dusk-lav)]"
                                        fill="none"
                                        viewBox="0 0 24 24"
                                        strokeWidth={1.5}
                                        stroke="currentColor"
                                      >
                                        <path
                                          strokeLinecap="round"
                                          strokeLinejoin="round"
                                          d="M4.5 12.75l6 6 9-13.5"
                                        />
                                      </svg>
                                    ) : (
                                      <svg
                                        className="h-3.5 w-3.5 text-[var(--dusk-dim)] transition-colors group-hover:text-[var(--dusk-lav)]"
                                        fill="none"
                                        viewBox="0 0 24 24"
                                        strokeWidth={1.5}
                                        stroke="currentColor"
                                      >
                                        <path
                                          strokeLinecap="round"
                                          strokeLinejoin="round"
                                          d="M15.75 17.25v3.375c0 .621-.504 1.125-1.125 1.125h-9.75a1.125 1.125 0 01-1.125-1.125V7.875c0-.621.504-1.125 1.125-1.125H6.75a9.06 9.06 0 011.5.124m7.5 10.376h3.375c.621 0 1.125-.504 1.125-1.125V11.25c0-4.46-3.243-8.161-7.5-8.876a9.06 9.06 0 00-1.5-.124H9.375c-.621 0-1.125.504-1.125 1.125v3.5m7.5 10.375H9.375a1.125 1.125 0 01-1.125-1.125v-9.25m11.25 5.5h-1.875a1.125 1.125 0 01-1.125-1.125v-1.875M18 14.25v-3.75"
                                        />
                                      </svg>
                                    )}
                                  </m.span>
                                </AnimatePresence>
                              </button>
                              <span
                                className={`${CHIP} justify-self-start ${
                                  license.status === "revoked"
                                    ? CHIP_ROSE
                                    : CHIP_LAV
                                }`}
                              >
                                {licenseStatusLabel(license.status)}
                              </span>
                              {license.stripeSessionId &&
                              license.status !== "revoked" ? (
                                <button
                                  onClick={() =>
                                    setRefundTarget({
                                      id: license.id,
                                      email: customer.email,
                                      key: license.key,
                                    })
                                  }
                                  disabled={isTransitioning}
                                  className={`${ROW_ACTION} bg-rose-500/[.12] text-rose-300 ring-rose-400/25 hover:bg-rose-500/20 disabled:hover:bg-rose-500/[.12]`}
                                >
                                  Refund
                                </button>
                              ) : (
                                // Holds the Refund cell so the next license starts a new grid row.
                                <span aria-hidden="true" />
                              )}
                            </div>
                          ))}
                        </div>
                      </td>

                      {/* Total credits. #1663: one Add credits per account, beside
                          the pooled balance it changes; any license id reaches
                          the same wallet on the server. */}
                      <td className="px-6 py-4">
                        <div className="flex flex-col items-start gap-1.5">
                          <span className="inline-flex flex-wrap items-baseline gap-x-1 text-[var(--dusk-ink)] text-sm font-medium tabular-nums">
                            <PopCredits
                              text={formatCredits(customer.totalCredits)}
                            />
                            <span className="text-[var(--dusk-dim)] text-xs">
                              ({Math.floor(customer.totalCredits / CREDITS_PER_MINUTE)} min)
                            </span>
                          </span>
                          {customer.licenses[0] && (
                            <button
                              onClick={() =>
                                openAddCredits({
                                  id: customer.licenses[0].id,
                                  email: customer.email,
                                  credits: customer.totalCredits,
                                })
                              }
                              disabled={isTransitioning}
                              className={`${ROW_ACTION} bg-[rgba(196,181,253,.12)] text-[var(--dusk-lav)] ring-[rgba(196,181,253,.24)] hover:bg-[rgba(196,181,253,.2)] disabled:hover:bg-[rgba(196,181,253,.12)]`}
                            >
                              Add credits
                            </button>
                          )}
                        </div>
                      </td>

                      {/* Total Spend (net lifetime Stripe spend across all of this
                          customer's Stripe customer IDs). Two distinct "no number"
                          states, rendered differently so a real Stripe error isn't
                          silently mistaken for "$0 spent":
                          - No Stripe customer ID on any license at all -> dash,
                            no Stripe call was ever made for this customer.
                          - Has Stripe customer ID(s) but totalSpentCents is null ->
                            a fetch failed server-side (logged there); show a
                            distinct "error" indicator rather than a dash or $0.00.
                          - Otherwise -> the real formatted currency amount. */}
                      <td className="px-6 py-4">
                        {!customer.licenses.some(
                          (license) => license.stripeCustomerId
                        ) ? (
                          <span
                            className="text-[var(--dusk-dim)] text-sm font-medium"
                            title="No Stripe purchase on file (may have paid via another method)"
                          >
                            —
                          </span>
                        ) : customer.totalSpentCents === null ? (
                          <span
                            className="inline-flex items-center gap-1 text-[var(--dusk-peach)] text-sm font-medium"
                            title="Failed to fetch Stripe spend for this customer"
                          >
                            <svg
                              className="w-3.5 h-3.5"
                              fill="none"
                              stroke="currentColor"
                              viewBox="0 0 24 24"
                            >
                              <path
                                d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z"
                                strokeLinecap="round"
                                strokeLinejoin="round"
                                strokeWidth={2}
                              />
                            </svg>
                            error
                          </span>
                        ) : (
                          <span className="text-[var(--dusk-ink)] text-sm font-medium tabular-nums">
                            {formatCurrency(customer.totalSpentCents)}
                          </span>
                        )}
                      </td>

                      {/* Created */}
                      <td className="px-6 py-4 text-[var(--dusk-mute)] text-sm tabular-nums">
                        {formatDate(customer.created, "en-US")}
                      </td>
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </HeightTween>

      {/* Customer Count + Pagination */}
      {data && data.totalCustomers > 0 && (
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-[var(--dusk-mute)] text-sm flex items-center gap-2 tabular-nums">
            <span>
              Showing {(data.page - 1) * data.pageSize + 1}
              &ndash;
              {Math.min(data.page * data.pageSize, data.totalCustomers)} of{" "}
              {data.totalCustomers} customer{data.totalCustomers !== 1 ? "s" : ""}
            </span>
            {isTransitioning && (
              <Spinner className="w-3.5 h-3.5 text-[var(--dusk-mute)]" label="Loading" />
            )}
          </p>

          {data.totalPages > 1 && (
            <nav className="flex flex-wrap items-center gap-1.5" aria-label="Pagination">
              <button
                type="button"
                onClick={() => setPage((p) => p - 1)}
                disabled={page <= 1}
                className={BTN_SECONDARY_XS}
              >
                Prev
              </button>
              {getPageNumbers(page, data.totalPages).map((entry, i) =>
                entry === "ellipsis" ? (
                  <span key={`ellipsis-${i}`} className="text-[var(--dusk-dim)] px-1">
                    …
                  </span>
                ) : (
                  <button
                    key={entry}
                    type="button"
                    onClick={() => setPage(entry)}
                    aria-current={entry === page ? "page" : undefined}
                    className={`w-9 h-9 flex items-center justify-center rounded-full text-sm font-medium tabular-nums transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] ${
                      entry === page
                        ? "bg-white text-[#0b0b14]"
                        : "bg-white/[.04] hover:bg-white/[.09] text-[var(--dusk-mute)] hover:text-white border border-[var(--dusk-line)]"
                    }`}
                  >
                    {entry}
                  </button>
                )
              )}
              <button
                type="button"
                onClick={() => setPage((p) => p + 1)}
                disabled={page >= data.totalPages}
                className={BTN_SECONDARY_XS}
              >
                Next
              </button>
            </nav>
          )}
        </div>
      )}

      {/* Refund Modal */}
      <AnimatePresence>
        {refundTarget && (
          <AnimatedDialog
            key="refund-modal"
            onBackdropClick={() => { if (!refundMutation.isPending) { setRefundTarget(null); refundMutation.reset(); } }}
          >
            <div className="flex items-center justify-between">
              <h2 className="text-lg font-semibold tracking-[-0.02em] text-white">Refund Account Key</h2>
              <button
                onClick={() => { setRefundTarget(null); refundMutation.reset(); }}
                className={`p-1 -m-1 ${ICON_BTN}`}
              >
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>

            <div className="space-y-2 text-sm">
              <p className="text-[var(--dusk-mute)]">Email: <span className="text-white [overflow-wrap:anywhere]">{refundTarget.email}</span></p>
              <p className="text-[var(--dusk-mute)]">Key: <code className="text-white font-mono text-xs break-all">{refundTarget.key}</code></p>
            </div>

            {/* #965: the success banner replaces the two buttons. The box
                tweens between their heights (HeightTween) and the banner
                rises out of a small blur (.t-panel-content), instead of both
                landing in one frame. No spin: it is a text box, not an icon. */}
            <HeightTween trigger={refundMutation.isSuccess}>
              {refundMutation.isSuccess ? (
                <div
                  className={`t-panel-content p-3.5 ${ALERT_OK}`}
                  data-enter=""
                >
                  <p className="text-[var(--dusk-lav)] text-sm">
                    Refund processed successfully{refundMutation.data?.revoked ? " and Account Key revoked" : ""}.
                  </p>
                </div>
              ) : (
                <>
                  {/* #2286: the error reveals; its pb-5 is the gap to the
                      buttons (the dialog's space-y-5 stops at the HeightTween
                      box, one level up). */}
                  <ErrorReveal gap="pb-5">
                    {refundMutation.error && (
                      <div
                        className={`p-3.5 ${ALERT_ERROR}`}
                        role="alert"
                      >
                        <p className="text-rose-300 text-sm">{errorSentence(refundMutation.error, INVALID_INPUT)}</p>
                      </div>
                    )}
                  </ErrorReveal>
                  <div className="flex gap-3 pt-2">
                    <button
                      onClick={() =>
                        refundMutation.mutate({
                          licenseKeyId: refundTarget.id,
                          revokeLicense: true,
                        })
                      }
                      disabled={refundMutation.isPending}
                      className={`flex-1 ${BTN_DANGER}`}
                    >
                      {refundMutation.isPending ? "Processing..." : "Refund & Revoke"}
                    </button>
                    <button
                      onClick={() =>
                        refundMutation.mutate({
                          licenseKeyId: refundTarget.id,
                          revokeLicense: false,
                        })
                      }
                      disabled={refundMutation.isPending}
                      className={`flex-1 ${BTN_SECONDARY}`}
                    >
                      {refundMutation.isPending ? "Processing..." : "Refund Only"}
                    </button>
                  </div>
                </>
              )}
            </HeightTween>
          </AnimatedDialog>
        )}
      </AnimatePresence>

      {/* Add Credits Modal (targets a single license key directly) */}
      <AnimatePresence>
        {creditTarget && (
          <AnimatedDialog
            key="credit-modal"
            onBackdropClick={() => { if (!addCreditsMutation.isPending) { setCreditTarget(null); addCreditsMutation.reset(); } }}
          >
            {/* Header */}
            <div className="flex items-center justify-between">
              <h2 className="text-lg font-semibold tracking-[-0.02em] text-white">Add Credits</h2>
              <button
                onClick={() => { setCreditTarget(null); addCreditsMutation.reset(); }}
                className={`p-1 -m-1 ${ICON_BTN}`}
              >
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>

            {/* Account Info (the pooled balance) */}
            <div className="p-3.5 rounded-2xl border border-[var(--dusk-line)] bg-white/[.03] space-y-1">
              <p className="text-white font-medium [overflow-wrap:anywhere]">{creditTarget.email}</p>
              <p className="text-[var(--dusk-mute)] text-sm tabular-nums">
                Current balance: {formatCredits(creditTarget.credits)} credits
                ({Math.floor(creditTarget.credits / CREDITS_PER_MINUTE)} minutes)
              </p>
            </div>

            {/* Form */}
            <form onSubmit={handleAddCredits} className="space-y-4">
              {/* Credit Amount */}
              <div>
                <label className={`block mb-2 ${LABEL}`} htmlFor="add-credits-amount">
                  Credits to Add
                </label>
                <input
                  id="add-credits-amount"
                  type="number"
                  value={creditAmount}
                  onChange={(e) => setCreditAmount(e.target.value)}
                  placeholder="e.g. 5000"
                  min="1"
                  step="1"
                  required
                  className={`w-full px-3.5 py-2.5 tabular-nums ${INPUT}`}
                />
                {creditAmount && parseFloat(creditAmount) > 0 && (
                  <p className="text-[var(--dusk-mute)] text-xs mt-1.5">
                    = {Math.floor(parseFloat(creditAmount) / CREDITS_PER_MINUTE)} minutes of transcription
                  </p>
                )}
              </div>

              {/* Quick amounts */}
              <div className="flex gap-2">
                {[1000, 5000, 10000, 20000].map((amount) => (
                  <button
                    key={amount}
                    type="button"
                    onClick={() => setCreditAmount(amount.toString())}
                    className="flex-1 px-2 py-1.5 rounded-full border border-[var(--dusk-line)] bg-white/[.03] text-[var(--dusk-mute)] text-sm tabular-nums transition-colors hover:border-white/25 hover:bg-white/[.07] hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
                  >
                    {amount.toLocaleString()}
                  </button>
                ))}
              </div>

              {/* Error. It reveals (#2286); the space-y-4 gap rides inside
                  the clip as pb-4. */}
              <ErrorReveal gap="pb-4">
                {addCreditsMutation.error && (
                  <p className="text-rose-300 text-sm" role="alert">
                    {errorSentence(addCreditsMutation.error, INVALID_AMOUNT)}
                  </p>
                )}
              </ErrorReveal>

              {/* Actions */}
              <div className="flex gap-3 pt-2">
                <button
                  type="button"
                  onClick={() => { setCreditTarget(null); addCreditsMutation.reset(); }}
                  className={`flex-1 ${BTN_SECONDARY}`}
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={addCreditsMutation.isPending || !creditAmount || parseFloat(creditAmount) <= 0}
                  className={`flex-1 ${BTN_PRIMARY}`}
                >
                  {addCreditsMutation.isPending ? "Adding..." : "Add Credits"}
                </button>
              </div>
            </form>
          </AnimatedDialog>
        )}
      </AnimatePresence>
    </div>
  );
}
