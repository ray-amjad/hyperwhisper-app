"use client";

import { Fragment, useId, useMemo, useState, type CSSProperties } from "react";
import { Card, CardBody } from "@heroui/card";
import { Button } from "@heroui/button";
import { Input } from "@heroui/input";
import { Wallet, ShieldCheck, Layers, LogIn } from "lucide-react";
import { useTranslations } from "next-intl";

import {
  MIN_CREDIT_DOLLARS,
  MAX_CREDIT_DOLLARS,
  CREDITS_PER_DOLLAR,
  validateCreditPurchaseAmount,
  computeCreditPurchase,
} from "@/app/api/checkout/credits/validation";
import { showEmailError } from "@/src/lib/credits-email";
import { watchForAbandonedRedirect } from "@/src/lib/abandoned-redirect";
import { createBuyCreditsHandler } from "@/src/lib/buy-credits";
import { reportClientError } from "@/lib/shared/report-client-error";
import { ErrorReveal } from "@/components/ErrorReveal";
import { PageHero } from "@/components/landing/DuskPage";
import { WRAP } from "@/components/landing/dusk";
import {
  GLASS,
  ICON_TILE,
  LABEL,
  SECTION,
} from "@/components/landing/dusk-pages";

const PRESETS = [5, 10] as const;

/** `--d` is the entrance delay that `.dusk-enter` in globals.css reads. */
const delay = (ms: number) => ({ "--d": `${ms}ms` }) as CSSProperties;

/** Both fields: a dark well with a hairline edge that turns lavender on focus. */
const INPUT_CLASS_NAMES = {
  inputWrapper:
    "rounded-xl border bg-[#090b18] border-[#2a2e4a] shadow-none data-[hover=true]:border-[#3a3f63] group-data-[focus=true]:border-[var(--dusk-lav)]",
  input: "text-white placeholder:text-[var(--dusk-dim)]",
};

const AMOUNT_TILE =
  "cursor-pointer rounded-2xl border p-3 text-start transition-colors duration-150 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] sm:p-4 hyphens-auto [overflow-wrap:break-word]";
const AMOUNT_ON = "border-white bg-white/[.07]";
const AMOUNT_OFF =
  "border-[var(--dusk-line)] bg-white/[.02] hover:border-white/25";

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * The seam's report properties, narrowed to what `reportClientError` takes.
 * The seam builds them and keeps the email (and any licence key) out of them;
 * this only drops values that are not a string or a number, and drops the
 * seam's own `operation`, which `reportClientError` overwrites anyway.
 */
function reportContext(
  properties: Record<string, unknown>,
): Record<string, string | number> {
  const context: Record<string, string | number> = {};

  for (const [key, value] of Object.entries(properties)) {
    if (key === "operation") continue;
    if (typeof value === "string" || typeof value === "number") {
      context[key] = value;
    }
  }

  return context;
}

/**
 * #768: a summary value that pops in, one character at a time, when it
 * changes. The first render is the plain text, so the server markup and the
 * first paint are as before. Each later change remounts the characters (the
 * key), which replays their `.t-number-pop-char` animation with no reflow
 * trick. A screen reader reads the whole value from the `sr-only` copy; the
 * split characters are hidden from it.
 */
function PopValue({ text, delay = 0 }: { text: string; delay?: number }) {
  const [shown, setShown] = useState(text);
  const [changes, setChanges] = useState(0);

  // The "adjust state while rendering" pattern from the React docs: no effect,
  // so the new value never paints once without its animation.
  if (text !== shown) {
    setShown(text);
    setChanges(changes + 1);
  }
  if (changes === 0) return text;

  return (
    <Fragment key={changes}>
      <span className="sr-only">{text}</span>
      {Array.from(text).map((char, index) => (
        <span
          key={index}
          aria-hidden="true"
          className="t-number-pop-char"
          style={{ animationDelay: `${delay + index * 40}ms` }}
        >
          {char}
        </span>
      ))}
    </Fragment>
  );
}

/**
 * Guest buy-credits UI for /credits.
 *
 * Visitor enters an email, picks a preset ($5 / $10) or a custom whole-dollar
 * amount, and we POST to /api/checkout/credits (mint path — no license key)
 * through the shared seam in `src/lib/buy-credits.ts` (#949), the same one the
 * dashboard's `CloudCreditsCard` uses. Stripe collects payment, the webhook
 * mints a key and emails it to them.
 *
 * Copy lives under the `buyCredits` namespace in messages/*.json.
 */
export default function CreditsPurchase({ locale }: { locale: string }) {
  const t = useTranslations("buyCredits");
  const [email, setEmail] = useState("");
  const [emailTouched, setEmailTouched] = useState(false);
  const [amount, setAmount] = useState<number>(PRESETS[0]);
  const [customAmount, setCustomAmount] = useState("");
  const [isCustom, setIsCustom] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const effectiveAmount = isCustom ? Number(customAmount) : amount;

  // Single source of truth for bounds — same validator the API route enforces,
  // so the client preview can't accept/reject a different amount than the server.
  const amountValid = validateCreditPurchaseAmount(effectiveAmount) === null;

  const emailValid = EMAIL_RE.test(email.trim());
  const emailInvalid = showEmailError(email, emailTouched, emailValid);

  // #968: stable ids that tie the visible label, the help line and the error
  // to the email field. Never point at HeroUI's generated React Aria id.
  const emailInputId = useId();
  const emailHelpId = useId();
  const emailErrorId = useId();
  // #1147: the custom-amount field is named by the visible "Choose an amount"
  // heading and the Custom tile title, not by its placeholder.
  const amountLabelId = useId();
  const customTitleId = useId();

  const { credits, feeUsd, totalUsd } = useMemo(() => {
    if (!amountValid) return { credits: 0, feeUsd: 0, totalUsd: 0 };
    // Reuse the server's pricing math (cents) so the preview matches the charge.
    const { creditAmount, creditCents, feeCents } =
      computeCreditPurchase(effectiveAmount);

    return {
      credits: creditAmount,
      feeUsd: feeCents / 100,
      totalUsd: (creditCents + feeCents) / 100,
    };
  }, [amountValid, effectiveAmount]);

  const selectPreset = (value: number) => {
    setIsCustom(false);
    setAmount(value);
    setError(null);
  };

  // Built in the render body, as `CloudCreditsCard.tsx` does it, so a test that
  // can render this form but cannot click it still sees the wiring. Every rule
  // about when to navigate, what to show and when to clear `loading` lives in
  // the seam (#949): a non-JSON body and an empty or 5xx server `error` show
  // the translated checkout copy, and a scheduled redirect hands the release
  // of `loading` to the abandoned-redirect watcher instead of dropping it.
  const handleBuyCredits = createBuyCreditsHandler({
    email: email.trim(),
    // NOT the bare global `fetch`: passed as a value it loses its `this` and
    // throws `Illegal invocation` in the browser.
    fetchImpl: (input, init) => fetch(input, init),
    navigate: (destination) => {
      window.location.href = destination;
    },
    // Never the email address: the seam builds these properties and keeps the
    // address out of them, and only the failure goes to the report.
    // `credits.checkout` is the operation name #739 pins for this form.
    reportError: (thrown, properties) => {
      const context = reportContext(properties);

      void reportClientError("credits.checkout", thrown, context);
    },
    // The seam's busy tier is a dollar amount or "custom"; this form has one
    // button, so any tier is just "loading".
    setBusy: (tier) => setLoading(tier !== null),
    setError,
    // Back from Stripe out of the bfcache re-arms the button. See
    // `src/lib/abandoned-redirect.ts` before adding a timer here.
    onRedirectScheduled: (release) => {
      watchForAbandonedRedirect(window, release);
    },
    checkoutErrorMessage: t("errorCheckout"),
    genericErrorMessage: t("errorGeneric"),
  });

  const handleCheckout = async () => {
    // Guard against double-submit (rapid clicks create duplicate checkout
    // sessions / charges); the button is also disabled while loading.
    if (loading) return;
    setError(null);

    if (!emailValid) {
      setError(t("errorEmail"));

      return;
    }
    if (!amountValid) {
      setError(
        t("errorAmount", {
          min: MIN_CREDIT_DOLLARS,
          max: MAX_CREDIT_DOLLARS,
        }),
      );

      return;
    }

    // The seam never rejects; it sets and releases `loading` itself.
    await handleBuyCredits(effectiveAmount);
  };

  return (
    <>
      <PageHero
        eyebrow={t("eyebrow")}
        lead={t("subtitle")}
        title={t("title")}
      />

      <section className={SECTION}>
        <div
          className={`${WRAP} grid items-start gap-10 lg:grid-cols-[1.05fr_.95fr]`}
        >
          {/* Above the fold, so the entrance is CSS (`dusk-enter`), not a
              scroll reveal: it plays from the server HTML. */}
          <div className="dusk-enter" style={delay(240)}>
            {/* A HeroUI Card with a palette border colour, so the #1384
                border-width guard still covers it. */}
            <Card className="rounded-[22px] border border-violet-300/30 bg-[linear-gradient(180deg,rgba(129,90,220,.14),rgba(255,255,255,.03))] text-start shadow-none">
              <CardBody className="p-6 md:p-[34px]">
                {/* Email */}
                <label className={`block ${LABEL}`} htmlFor={emailInputId}>
                  {t("emailLabel")}
                </label>
                {/* HeroUI names a label-less Input by its placeholder, and an
                    aria-label beats <label for>, so the visible label text is
                    passed as the aria-label too. A caller's aria-describedby
                    REPLACES HeroUI's own (its error id is dropped), so the
                    error gets an id of ours and is listed here while it shows. */}
                <Input
                  aria-describedby={
                    emailInvalid
                      ? `${emailHelpId} ${emailErrorId}`
                      : emailHelpId
                  }
                  aria-label={t("emailLabel")}
                  autoComplete="email"
                  className="mt-2.5"
                  classNames={INPUT_CLASS_NAMES}
                  errorMessage={
                    <span id={emailErrorId}>{t("errorEmail")}</span>
                  }
                  id={emailInputId}
                  isInvalid={emailInvalid}
                  placeholder={t("emailPlaceholder")}
                  size="lg"
                  type="email"
                  value={email}
                  variant="bordered"
                  onBlur={() => setEmailTouched(true)}
                  onChange={(e) => setEmail(e.target.value)}
                />
                <p
                  className="mt-2 text-[13px] text-[var(--dusk-dim)]"
                  id={emailHelpId}
                >
                  {t("emailHelp")}
                </p>

                {/* Amount */}
                <div className="mt-[26px]">
                  <span className={`block ${LABEL}`} id={amountLabelId}>
                    {t("amountLabel")}
                  </span>
                  {/* A single-choice group of toggle buttons (#969): the
                      chosen tile is exposed by aria-pressed, not only its
                      border colour. Not role="radio", which would need
                      arrow-key roving focus. */}
                  <div
                    aria-labelledby={amountLabelId}
                    className="mt-2.5 grid grid-cols-2 gap-2.5 sm:grid-cols-3"
                    role="group"
                  >
                    {PRESETS.map((value) => {
                      const selected = !isCustom && amount === value;

                      return (
                        <button
                          key={value}
                          aria-pressed={selected}
                          className={`${AMOUNT_TILE} ${selected ? AMOUNT_ON : AMOUNT_OFF}`}
                          type="button"
                          onClick={() => selectPreset(value)}
                        >
                          <span className="block text-xl font-semibold text-white sm:text-2xl">
                            ${value}
                          </span>
                          <span className="mt-0.5 block text-[13px] text-[var(--dusk-mute)]">
                            {t("creditsCount", {
                              count: value * CREDITS_PER_DOLLAR,
                            })}
                          </span>
                        </button>
                      );
                    })}
                    <button
                      aria-pressed={isCustom}
                      className={`${AMOUNT_TILE} col-span-2 sm:col-span-1 ${isCustom ? AMOUNT_ON : AMOUNT_OFF}`}
                      type="button"
                      onClick={() => setIsCustom(true)}
                    >
                      <span
                        className="block text-xl font-semibold text-white sm:text-2xl"
                        id={customTitleId}
                      >
                        {t("custom")}
                      </span>
                      <span className="mt-0.5 block text-[13px] text-[var(--dusk-mute)]">
                        {t("customSub")}
                      </span>
                    </button>
                  </div>

                  {/* The field grows open from 0fr (#702) instead of arriving
                      at full height in one frame. */}
                  {isCustom && (
                    <div className="t-cell-reveal">
                      <div>
                        <div className="mt-3">
                          {/* HeroUI still sets aria-label to the placeholder,
                              but a caller's aria-labelledby reaches the
                              <input> as given and wins over aria-label in the
                              accessible name. */}
                          <Input
                            aria-labelledby={`${amountLabelId} ${customTitleId}`}
                            classNames={INPUT_CLASS_NAMES}
                            max={MAX_CREDIT_DOLLARS}
                            min={MIN_CREDIT_DOLLARS}
                            placeholder={t("customPlaceholder", {
                              min: MIN_CREDIT_DOLLARS,
                              max: MAX_CREDIT_DOLLARS,
                            })}
                            size="lg"
                            startContent={
                              <span className="text-[var(--dusk-dim)]">$</span>
                            }
                            step={1}
                            type="number"
                            value={customAmount}
                            variant="bordered"
                            onChange={(e) => {
                              setCustomAmount(e.target.value);
                              setError(null);
                            }}
                          />
                        </div>
                      </div>
                    </div>
                  )}
                </div>

                {/* Summary */}
                <div className="mt-[26px] grid gap-2.5 border-t border-[var(--dusk-line)] pt-4 text-[15px] tabular-nums">
                  <div className="flex justify-between gap-4 text-[var(--dusk-mute)]">
                    <span>{t("summaryCredits")}</span>
                    <span className="font-medium text-white">
                      <PopValue text={credits.toLocaleString()} />
                    </span>
                  </div>
                  <div className="flex justify-between gap-4 text-[var(--dusk-mute)]">
                    <span>{t("summaryFee")}</span>
                    <span className="font-medium text-white">
                      <PopValue text={`$${feeUsd.toFixed(2)}`} />
                    </span>
                  </div>
                  <div className="flex items-baseline justify-between gap-4 border-t border-[var(--dusk-line)] pt-3 text-white">
                    <span>{t("summaryTotal")}</span>
                    {/* Last in the stagger, so the eye lands on the charge. */}
                    <span className="text-[22px] font-semibold">
                      <PopValue delay={120} text={`$${totalUsd.toFixed(2)}`} />
                    </span>
                  </div>
                </div>

                {/* #749: the wrapper stays mounted and the message reveals in
                    it (.t-error-reveal), so the button below moves down over
                    280 ms instead of in one frame. The alert still mounts with
                    its text. No field shakes here: the button is disarmed
                    while the email or the amount is invalid, so the only error
                    a buyer can reach is the server refusing the checkout,
                    which no field caused. */}
                <ErrorReveal>
                  {error && (
                    <p className="mt-4 text-sm text-red-400" role="alert">
                      {error}
                    </p>
                  )}
                </ErrorReveal>

                {/* h-auto + whitespace-normal: the label wraps instead of
                    being clipped at 320px in bg, cs, el, fr, id, is, ms, nl,
                    pt, ro, th (#2871's sibling). */}
                <Button
                  className="mt-[22px] h-auto min-h-12 w-full whitespace-normal rounded-full bg-white py-3 text-center text-[15px] font-semibold text-[#0b0b14] data-[hover=true]:bg-[#ececf5] data-[hover=true]:opacity-100 data-[focus-visible=true]:outline-[var(--dusk-lav)]"
                  isDisabled={loading || !emailValid || !amountValid}
                  isLoading={loading}
                  size="lg"
                  onPress={handleCheckout}
                >
                  {loading ? t("ctaLoading") : t("ctaIdle")}
                </Button>

                <p className="mt-3.5 text-center text-[13px] text-[var(--dusk-dim)]">
                  {t("secure")}
                </p>
              </CardBody>
            </Card>
          </div>

          {/* Reassurance list */}
          <div
            className="dusk-enter grid gap-[22px] lg:pt-3"
            style={delay(320)}
          >
            <ul className="grid gap-[22px]">
              {[
                { Icon: Layers, text: t("stripProviders") },
                { Icon: Wallet, text: t("stripNoSub") },
                { Icon: ShieldCheck, text: t("stripNoTraining") },
              ].map(({ Icon, text }) => (
                <li key={text} className="flex items-center gap-4">
                  <span aria-hidden="true" className={ICON_TILE}>
                    <Icon strokeWidth={1.75} />
                  </span>
                  <span className="text-[17px] font-semibold text-white">
                    {text}
                  </span>
                </li>
              ))}
            </ul>

            {/* Existing customers: a prominent sign-in CTA so they top up
                their current wallet instead of buying a second license by
                mistake. */}
            <div
              className={`${GLASS} mt-2 flex flex-wrap items-center justify-between gap-x-4 gap-y-2 px-[22px] py-[18px]`}
            >
              <p className="text-[15px] text-[var(--dusk-ink)]">
                {t("haveKey")}
              </p>
              <Button
                className="h-auto min-h-11 min-w-0 max-w-full whitespace-normal rounded-full px-3 py-2 text-start text-[14.5px] text-[var(--dusk-lav)] data-[hover=true]:bg-white/[.06] data-[hover=true]:text-white"
                startContent={
                  <LogIn className="h-4 w-4 shrink-0" strokeWidth={1.5} />
                }
                // The label wraps instead of running past a 320px viewport in
                // the longer locales (is, ro, fr, fi): #2871.
                variant="light"
                onPress={() => {
                  window.location.href = `/${locale}/user/sign-in`;
                }}
              >
                {t("signIn")}
              </Button>
            </div>
          </div>
        </div>
      </section>
    </>
  );
}
