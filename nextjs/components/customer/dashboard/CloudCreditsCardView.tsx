"use client";

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
 * The stateful half — the four `useState`s, `usePostHog`, the two
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
    <div className="bg-white/5 rounded-xl border border-white/10 p-5">
      <div className="mb-4">
        <p className="text-sm text-gray-400 mb-1">{labels.title}</p>
        <p className="text-2xl font-semibold text-white">
          {totalCredits.toLocaleString()}
        </p>
        {/* No `> 0` test here. The wrapper already made that call and a null
            string IS the answer — same shape as `tier.minutesLabel` below. */}
        {labels.minutesRemaining && (
          <p className="text-sm text-gray-400 mt-0.5">
            {labels.minutesRemaining}
          </p>
        )}
      </div>

      {activeLicenseKey && (
        <>
          <div className="grid grid-cols-3 gap-2">
            {tiers.map((tier) => {
              const isLoading = loadingTier === tier.amount;
              const isDisabled = loadingTier !== null;

              return (
                <button
                  key={tier.amount}
                  className="flex flex-col items-center justify-center px-3 py-3 bg-white/5 border border-white/10 text-white font-medium rounded-lg hover:bg-white/10 hover:border-white/20 disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer transition-colors"
                  disabled={isDisabled}
                  onClick={() => onBuy(tier.amount, tier.amount)}
                >
                  {isLoading ? (
                    <div className="w-5 h-5 border-2 border-white border-t-transparent rounded-full animate-spin" />
                  ) : (
                    <>
                      <span className="text-lg font-semibold">
                        ${tier.amount}
                      </span>
                      <span className="text-xs text-gray-400">
                        {tier.creditsLabel}
                      </span>
                      {tier.minutesLabel && (
                        <span className="text-xs text-gray-500">
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
              className={`flex flex-col items-center justify-center px-3 py-3 border text-white font-medium rounded-lg disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer transition-colors ${
                showCustom
                  ? "bg-white/10 border-white/30"
                  : "bg-white/5 border-white/10 hover:bg-white/10 hover:border-white/20"
              }`}
              disabled={loadingTier !== null}
              onClick={onToggleCustom}
            >
              <span className="text-lg font-semibold">{labels.custom}</span>
              <span className="text-xs text-gray-400">{labels.customSub}</span>
            </button>
          </div>

          {showCustom && (
            <div className="mt-2 flex items-center gap-2">
              <div className="relative flex-1">
                <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-gray-400">
                  $
                </span>
                <input
                  className="w-full rounded-lg border border-white/10 bg-white/5 py-2 pl-7 pr-3 text-white placeholder:text-gray-500 focus:border-white/30 focus:outline-none disabled:opacity-50"
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
                className="flex items-center justify-center rounded-lg bg-white/10 border border-white/20 px-4 py-2 font-medium text-white hover:bg-white/20 disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer transition-colors"
                disabled={!customValid || loadingTier !== null}
                onClick={() => onBuy(Number(customAmount), "custom")}
              >
                {loadingTier === "custom" ? (
                  <div className="w-5 h-5 border-2 border-white border-t-transparent rounded-full animate-spin" />
                ) : (
                  labels.topUp
                )}
              </button>
            </div>
          )}

          {/* #737: the whole point. A refused checkout used to leave the
              customer with a spinner that stopped and nothing else. The region
              sits UNDER the tier buttons, inside the `activeLicenseKey` guard
              — there is no buy button to fail without a key. */}
          {error && (
            <div className="mt-2">
              <span className="text-red-300 text-sm" role="alert">
                {error}
              </span>
            </div>
          )}
        </>
      )}
    </div>
  );
}
