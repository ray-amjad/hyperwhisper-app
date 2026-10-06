import { Metadata } from "next";
import {
  CREDIT_FEE_RATE,
  CREDITS_PER_DOLLAR,
  MAX_CREDIT_DOLLARS,
  MIN_CREDIT_DOLLARS,
  computeCreditPurchase,
} from "@/app/api/checkout/credits/validation";

export const metadata: Metadata = {
  title: "Refund Policy | HyperWhisper",
  description:
    "HyperWhisper refund policy for HyperWhisper Cloud credits, including the 14-day money back guarantee",
};

// Every number on this page comes from the checkout's own constants, so the
// policy cannot drift from what checkout charges.
const FEE_PERCENT = Math.round(CREDIT_FEE_RATE * 100);
const formatCredits = (credits: number) => credits.toLocaleString("en-US");
const formatDollars = (cents: number) => `$${(cents / 100).toFixed(2)}`;

// Worked example: the smallest purchase, with a tenth of its credits used.
const example = computeCreditPurchase(MIN_CREDIT_DOLLARS);
const exampleUsedCredits = example.creditAmount / 10;
const exampleUsedCents = example.creditCents / 10;
const exampleRefundCents = example.creditCents - exampleUsedCents;

export default function RefundPolicyPage() {
  return (
    <div className="prose prose-lg max-w-none prose-invert">
      <p className="text-sm text-gray-400 italic mb-8">
        Last Updated: October 6, 2026
      </p>

      <h1>Refund Policy</h1>

      <p className="text-lg text-gray-300 bg-blue-900/20 border border-blue-800 rounded-lg p-4 mb-8">
        <strong>14-Day Money Back Guarantee:</strong> If you&apos;re not
        satisfied with a HyperWhisper Cloud credit purchase, we&apos;ll refund
        the credits you have not used, no questions asked. Only unused,
        unexpired purchased credits may be refunded. Consumed credits, expired
        credits and the processing fee are not refunded.
      </p>

      <h2>I. What This Policy Covers</h2>

      <p>
        HyperWhisper sells HyperWhisper Cloud credits. This policy covers those
        credit purchases. Credits are a one-time purchase, not a subscription,
        and are priced at {formatCredits(CREDITS_PER_DOLLAR)} credits per
        US$1. You can buy any whole-dollar amount from ${MIN_CREDIT_DOLLARS} to
        ${MAX_CREDIT_DOLLARS}.
      </p>

      <h2>II. What Can Be Refunded</h2>

      <ul>
        <li>
          <strong>Unused, unexpired purchased credits:</strong> Only unused,
          unexpired purchased credits may be refunded. A credit refund reverses
          the credit value only.
        </li>
        <li>
          <strong>Consumed credits:</strong> Credits you have already used are
          non-refundable.
        </li>
        <li>
          <strong>Expired credits:</strong> We reserve the right to expire
          unused credits 365 days after purchase. Expired credits are removed
          from your spendable balance and are not refundable or recoverable.
        </li>
        <li>
          <strong>Processing fee:</strong> A non-refundable processing fee of{" "}
          {FEE_PERCENT}% is added to each credit purchase as a separate line
          item. The fee is not converted into credits and is never refunded,
          including where the underlying credits are refunded.
        </li>
      </ul>

      <p>
        <strong>Example:</strong> You buy ${MIN_CREDIT_DOLLARS} of credits (
        {formatCredits(example.creditAmount)} credits) and pay{" "}
        {formatDollars(example.creditCents + example.feeCents)}, which
        includes the {formatDollars(example.feeCents)} processing fee. You use{" "}
        {formatCredits(exampleUsedCredits)} credits (
        {formatDollars(exampleUsedCents)}) and then ask for a refund. We refund{" "}
        {formatDollars(exampleRefundCents)}, the value of the credits you did
        not use. The {formatDollars(example.feeCents)} fee is not refunded.
      </p>

      <h2>III. Refund Eligibility</h2>

      <p>
        You are eligible for a refund of your unused, unexpired purchased
        credits if:
      </p>

      <ul>
        <li>
          You request the refund within <strong>14 days</strong> of your
          original purchase date
        </li>
        <li>You purchased the credits directly from our official website</li>
        <li>You provide your original order confirmation or transaction ID</li>
      </ul>

      <p>
        <strong>No questions asked</strong>. We don&apos;t require you to
        provide a reason for your refund request, though feedback is always
        welcome to help us improve.
      </p>

      <h2>IV. How to Request a Refund</h2>

      <p>Requesting a refund is simple and straightforward:</p>

      <ol>
        <li>
          <strong>Contact our support team</strong> via email at{" "}
          <a
            className="text-blue-400 hover:underline"
            href="mailto:hi@support.hyperwhisper.com"
          >
            hi@support.hyperwhisper.com
          </a>
        </li>
        <li>
          <strong>Include your order information</strong> - provide your order
          confirmation number or transaction ID
        </li>
        <li>
          <strong>Send from the email address</strong> used for your original
          purchase (for verification)
        </li>
        <li>
          <strong>We&apos;ll process your request</strong> within 1 business
          day
        </li>
      </ol>

      <h2>V. Refund Processing</h2>

      <h3>Processing Time</h3>
      <ul>
        <li>
          <strong>Refund approval:</strong> Within 1 business day of your
          request
        </li>
        <li>
          <strong>Credit card refunds:</strong> 3-5 business days to appear on
          your statement
        </li>
      </ul>

      <h3>Refund Method</h3>
      <p>
        Refunds will be issued using the same payment method you used for your
        original purchase. We cannot issue refunds to different payment methods
        or accounts for security reasons.
      </p>

      <h2>VI. What Happens After a Refund</h2>

      <p>
        Once your refund is processed, the refunded credits are removed from
        your HyperWhisper Cloud balance.
      </p>

      <h2>VII. Special Circumstances</h2>

      <h3>Technical Issues</h3>
      <p>
        If you&apos;re experiencing technical difficulties with HyperWhisper, we
        encourage you to contact our support team first. Many issues can be
        resolved quickly, and we&apos;re here to help you get the most out of
        your credits.
      </p>

      <h3>Compatibility Concerns</h3>
      <p>
        Before purchasing, please review our system requirements. However, if
        HyperWhisper Cloud doesn&apos;t work on your system due to
        compatibility issues, your unused credits are covered by our 14-day
        guarantee.
      </p>

      <h3>Feature Requests</h3>
      <p>
        While we can&apos;t guarantee specific feature implementations, we
        actively consider user feedback for future updates. Your input helps
        shape HyperWhisper&apos;s development.
      </p>

      <h2>VIII. Promotional Purchases</h2>

      <p>
        If you used a promotion code, promotional pricing may not be reapplied
        to future purchases.
      </p>

      <h2>IX. Beyond the 14-Day Period</h2>

      <p>
        While our standard guarantee is 14 days, we understand that exceptional
        circumstances may arise. If you have concerns about your purchase beyond
        the 14-day period, please don&apos;t hesitate to contact our support
        team. We&apos;ll work with you to find a fair solution.
      </p>

      <h2>X. Multiple Purchases</h2>

      <p>
        If you&apos;ve made more than one credit purchase, each purchase is
        eligible for its own 14-day refund period from the respective purchase
        date.
      </p>

      <h2>XI. Fraudulent Activity</h2>

      <p>
        We reserve the right to refuse refunds in cases of suspected fraudulent
        activity, including but not limited to:
      </p>

      <ul>
        <li>Repeated purchases and refund requests</li>
        <li>Attempts to obtain multiple refunds for the same purchase</li>
        <li>Use of stolen payment methods</li>
        <li>Violation of our Terms of Service</li>
      </ul>

      <h2>XII. Contact Information</h2>

      <p>
        For all refund requests and questions about this policy, please contact
        us:
      </p>

      <div className="bg-gray-900/50 rounded-lg p-6 border border-gray-800">
        <p className="mb-2">
          <strong>Provider:</strong> Ray Amjad LTD (<a className="text-blue-400 hover:underline" href="https://find-and-update.company-information.service.gov.uk/company/14506459" target="_blank" rel="noopener noreferrer">Company Number 14506459</a>,
          United Kingdom)
        </p>
        <p className="mb-2">
          <strong>Email:</strong>{" "}
          <a
            className="text-blue-400 hover:underline"
            href="mailto:hi@support.hyperwhisper.com"
          >
            hi@support.hyperwhisper.com
          </a>
        </p>
        <p className="mb-2">
          <strong>Subject Line:</strong> &quot;Refund Request - [Your Order
          Number]&quot;
        </p>
        <p className="text-sm text-gray-400">
          We typically respond to refund requests within 1 business day.
        </p>
      </div>

      <h2>XIII. Policy Updates</h2>

      <p>
        We may update this refund policy from time to time. Any changes will be
        posted on this page with an updated &quot;Last Updated&quot; date.
        Significant changes will be communicated via email to recent
        purchasers.
      </p>

      <p>
        Your purchase is governed by the refund policy in effect at the time of
        your purchase.
      </p>

      <div className="bg-green-900/20 border border-green-800 rounded-lg p-6 mt-8">
        <h3 className="text-green-200 mt-0 mb-3">
          <a
            className="hover:underline"
            href="/"
          >
            Ready to try HyperWhisper?
          </a>
        </h3>
        <p className="text-green-300 mb-0">
          With our 14-day money back guarantee on unused credits, you can{" "}
          <a
            className="text-green-200 underline hover:no-underline"
            href="/credits"
          >
            buy HyperWhisper Cloud credits
          </a>{" "}
          with confidence.
        </p>
      </div>
    </div>
  );
}
