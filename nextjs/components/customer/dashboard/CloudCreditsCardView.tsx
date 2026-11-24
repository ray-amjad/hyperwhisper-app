"use client";

import { ErrorReveal } from "@/components/ErrorReveal";
import { Spinner } from "@/components/ui/Spinner";
import {
  BTN_PRIMARY,
  CARD_ACCENT,
  FOCUS_RING,
  INPUT,
  LABEL,
} from "@/components/user/panel-styles";

/** One amount tile, like the /credits page's amount picker. */
const TILE =
  `flex flex-col items-center justify-center gap-0.5 px-3 py-3.5 rounded-2xl border text-white font-medium disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer transition-[background-color,border-color,scale] duration-150 ease-out active:not-disabled:scale-[.97] ${FOCUS_RING} motion-reduce:transition-none`;
const TILE_OFF =
  "border-[var(--dusk-line)] bg-white/[.03] hover:not-disabled:border-white/25 hover:not-disabled:bg-white/[.06]";
const TILE_ON = "border-white bg-white/[.08]";

/**
 * The tier a checkout is in flight for: the dollar amount of a preset, or the
 * `"custom"` sentinel for the free-entry box. `null` is "nothing in flight".
 *
 * Re-declared here rather than imported from `@/src/lib/buy-credits` so this
 * file stays a leaf: a test can call it with no module mocks at all.
 */
export type CloudCreditsTier = number | "custom";

/** One preset button, with every string already translated by the wrapper. */
export interface CloudCreditsTierView {
  /** The dollar amount. It is both the button's label and the click argument. */
  amount: number;
  /** Translated `cloudCreditsCard.creditsCount` for this tier. */
  creditsLabel: string;
  /** Translated `cloudCreditsCard.minutes`, or `null` when unknown. */
  minutesLabel: string | null;
}

/**
 * The flat strings the card paints outside the tier grid. A bag rather than
 * eight separate props so a test can spread one literal and so a new string
 * does not change this component's arity.
 */
export interface CloudCreditsCardLabels {
  /** `cloudCreditsCard.title` */
  title: string;
  /**
   * `cloudCreditsCard.minutesRemaining`, already interpolated, or `null` when
   * there are no minutes to announce.
   *
   * NULLABLE, and that is the point (#947 review round 1, finding 2). Whether
   * this line is shown is ONE decision and the wrapper makes it, the same way
   * it already made `CloudCreditsTierView.minutesLabel`. When the string was
   * non-nullable this component re-decided it from `totalMinutesRemaining > 0`
   * — two owners for one rule, and a caller that disagreed painted
   * `~0 minutes remaining`.
   */
  minutesRemaining: string | null;
  /** `cloudCreditsCard.custom` */
  custom: string;
  /** `cloudCreditsCard.customSub` */
  customSub: string;
  /** `cloudCreditsCard.topUp` */
  topUp: string;
}

export interface CloudCreditsCardViewProps {
  totalCredits: number;
  /** Null hides the whole buy block — and with it the error region. */
  activeLicenseKey: string | null;
  tiers: readonly CloudCreditsTierView[];
  labels: CloudCreditsCardLabels;
  /** Which tier is spinning, or `null`. Any non-null value disarms them all. */
  loadingTier: CloudCreditsTier | null;
  /** The message from a refused checkout, or `null` when there is none. */
  error: string | null;
  /** Whether the custom-amount input is showing. */
  showCustom: boolean;
  onToggleCustom: () => void;
  customAmount: string;
  onCustomAmountChange: (value: string) => void;
  /** Whether the typed custom amount passes `validateCreditPurchaseAmount`. */
  customValid: boolean;
  /** `MIN_CREDIT_DOLLARS` / `MAX_CREDIT_DOLLARS`, for the input attributes. */
  minAmount: number;
  maxAmount: number;
  onBuy: (amount: number, tier: CloudCreditsTier) => void;
}

/**
 * The presentational half of `CloudCreditsCard` (#737).
 *
 * It owns every byte the card renders and it holds NO state and NO hooks, so a
 * test can call it as a plain function and walk the element tree it returns.
 * That is the only way a button's `onClick` is reachable in this repo:
 * `renderToStaticMarkup` is the whole rendering surface here and React does not
 * serialise event handlers, so a markup assertion can never see the wiring.
 * The same split, and the same reason, as `components/user/UserHeaderView.tsx`.
 *
 * Being hook-free also makes the busy and failed states reachable. They are
 * props here, not `useState` values an initial render never reaches, so a test
 * can render this in those states and assert POSITIVELY on the markup — which
 * is what puts #737's "Amount too large" literally on screen in a test.
 *
 * DEVIATION from `UserHeaderView`, which hard-codes its English: this card's
 * copy is translated, and `useTranslations` is a HOOK, so it cannot be called
 * here at all. Every string therefore arrives pre-formatted as a prop,
 * including the parameterised per-tier ones (`creditsCount`, `minutes`), which
 * the wrapper interpolates before handing them over. That is why `tiers` is a
 * view model and not the raw `CREDIT_TIERS` constant.
 *
 * The stateful half — the four `useState`s, `loadPostHog`, the two
 * `useTranslations` calls and the `createBuyCreditsHandler` call — stays in
 * `CloudCreditsCard.tsx`.
 */
export default function CloudCreditsCardView({
  totalCredits,
  activeLicenseKey,
  tiers,
  labels,
  loadingTier,
  error,
  showCustom,
  onToggleCustom,
  customAmount,
  onCustomAmountChange,
  customValid,
  minAmount,
  maxAmount,
  onBuy,
}: CloudCreditsCardViewProps) {
  return (
    <div className={`${CARD_ACCENT} p-5 sm:p-6`}>
      <div className="mb-5">
        <p className={`${LABEL} mb-2`}>{labels.title}</p>
        <p className="text-[34px] sm:text-[40px] font-semibold leading-none tracking-[-0.035em] text-white tabular-nums">
          {totalCredits.toLocaleString()}
        </p>
        {/* No `> 0` test here. The wrapper already made that call and a null
            string IS the answer — same shape as `tier.minutesLabel` below. */}
        {labels.minutesRemaining && (
          <p className="mt-2 text-sm text-[var(--dusk-mute)]">
            {labels.minutesRemaining}
          </p>
        )}
      </div>

      {activeLicenseKey && (
        <>
          <div className="grid grid-cols-3 gap-2.5">
            {tiers.map((tier) => {
              const isLoading = loadingTier === tier.amount;
              const isDisabled = loadingTier !== null;

              return (
                <button
                  key={tier.amount}
                  className={`${TILE} ${TILE_OFF}`}
                  disabled={isDisabled}
                  onClick={() => onBuy(tier.amount, tier.amount)}
                >
                  {isLoading ? (
                    <Spinner className="w-5 h-5 text-white" />
                  ) : (
                    <>
                      <span className="text-[22px] font-semibold leading-tight tracking-[-0.02em]">
                        ${tier.amount}
                      </span>
                      <span className="text-xs text-[var(--dusk-mute)]">
                        {tier.creditsLabel}
                      </span>
                      {tier.minutesLabel && (
                        <span className="text-xs text-[var(--dusk-mute)]">
                          {tier.minutesLabel}
                        </span>
                      )}
                    </>
                  )}
                </button>
              );
            })}

            {/* Custom amount: toggles an inline input below the tier grid. */}
            <button
              aria-pressed={showCustom}
              className={`${TILE} ${showCustom ? TILE_ON : TILE_OFF}`}
              disabled={loadingTier !== null}
              onClick={onToggleCustom}
            >
              <span className="text-[22px] font-semibold leading-tight tracking-[-0.02em]">{labels.custom}</span>
              <span className="text-xs text-[var(--dusk-mute)]">{labels.customSub}</span>
            </button>
          </div>

          {/* The row grows open from 0fr (#702) instead of arriving at full
              height in one frame. */}
          {showCustom && (
            <div className="t-cell-reveal">
              <div>
                <div className="mt-2.5 flex items-center gap-2">
                  <div className="relative flex-1">
                    <span className="pointer-events-none absolute left-3.5 top-1/2 -translate-y-1/2 text-[var(--dusk-dim)]">
                      $
                    </span>
                    <input
                      className={`w-full py-2.5 pl-8 pr-3 tabular-nums disabled:opacity-50 ${INPUT}`}
                      disabled={loadingTier !== null}
                      inputMode="numeric"
                      max={maxAmount}
                      min={minAmount}
                      placeholder={`${minAmount}–${maxAmount}`}
                      step={1}
                      type="number"
                      value={customAmount}
                      onChange={(e) => onCustomAmountChange(e.target.value)}
                    />
                  </div>
                  <button
                    className={`${BTN_PRIMARY} self-stretch`}
                    disabled={!customValid || loadingTier !== null}
                    onClick={() => onBuy(Number(customAmount), "custom")}
                  >
                    {loadingTier === "custom" ? (
                      <Spinner className="w-5 h-5" />
                    ) : (
                      labels.topUp
                    )}
                  </button>
                </div>
              </div>
            </div>
          )}

          {/* #737: the whole point. A refused checkout used to leave the
              customer with a spinner that stopped and nothing else. The region
              sits UNDER the tier buttons, inside the `activeLicenseKey` guard
              — there is no buy button to fail without a key. #2286: it
              reveals instead of arriving in one frame; the mt-2 stays inside
              the clip, so a closed wrapper takes no space. */}
          <ErrorReveal>
            {error && (
              <div className="mt-2">
                <span className="text-rose-300 text-sm" role="alert">
                  {error}
                </span>
              </div>
            )}
          </ErrorReveal>
        </>
      )}
    </div>
  );
}
