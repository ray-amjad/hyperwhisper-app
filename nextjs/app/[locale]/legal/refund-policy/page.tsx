import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Refund Policy | HyperWhisper",
  description: "HyperWhisper 14-day money back guarantee and refund policy",
};

export default function RefundPolicyPage() {
  return (
    <div className="prose prose-lg max-w-none dark:prose-invert">
      <p className="text-sm text-gray-600 dark:text-gray-400 italic mb-8">
        Last Updated: August 20, 2025
      </p>

      <h1>Refund Policy</h1>

      <p className="text-lg text-gray-700 dark:text-gray-300 bg-blue-50 dark:bg-blue-900/20 border border-blue-200 dark:border-blue-800 rounded-lg p-4 mb-8">
        <strong>14-Day Money Back Guarantee:</strong> We stand behind
        HyperWhisper with a full 14-day money back guarantee. If you're not
        completely satisfied with your purchase, we'll provide a full refund, no
        questions asked.
      </p>

      <h2>I. Our Commitment to You</h2>

      <p>
        At HyperWhisper, we're confident that our AI-powered speech-to-text
        application will transform how you work with content. However, we
        understand that software purchases are important decisions, and we want
        you to feel completely confident in your choice.
      </p>

      <p>
        That's why we offer a comprehensive 14-day money back guarantee on all
        HyperWhisper purchases.
      </p>

      <h2>II. Refund Eligibility</h2>

      <p>You are eligible for a full refund if:</p>

      <ul>
        <li>
          You request the refund within <strong>14 days</strong> of your
          original purchase date
        </li>
        <li>You purchased HyperWhisper directly from our official website</li>
        <li>You provide your original order confirmation or transaction ID</li>
      </ul>

      <p>
        <strong>No questions asked</strong>. We don't require you to provide a
        reason for your refund request, though feedback is always welcome to
        help us improve.
      </p>

      <h2>III. How to Request a Refund</h2>

      <p>Requesting a refund is simple and straightforward:</p>

      <ol>
        <li>
          <strong>Contact our support team</strong> via email at{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="mailto:support@hyperwhisper.com"
          >
            support@hyperwhisper.com
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
          <strong>We'll process your request</strong> within 1 business day
        </li>
      </ol>

      <h2>IV. Refund Processing</h2>

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
        <li>
          <strong>PayPal refunds:</strong> Immediate to your PayPal account
        </li>
        <li>
          <strong>Bank transfers:</strong> 5-7 business days depending on your
          bank
        </li>
      </ul>

      <h3>Refund Method</h3>
      <p>
        Refunds will be issued using the same payment method you used for your
        original purchase. We cannot issue refunds to different payment methods
        or accounts for security reasons.
      </p>

      <h2>V. What Happens After a Refund</h2>

      <p>Once your refund is processed:</p>

      <ul>
        <li>Your HyperWhisper license will be automatically deactivated</li>
        <li>You'll receive a confirmation email with refund details</li>
        <li>
          You may continue using the software until the deactivation takes
          effect (usually within 24 hours)
        </li>
      </ul>

      <h2>VI. Special Circumstances</h2>

      <h3>Technical Issues</h3>
      <p>
        If you're experiencing technical difficulties with HyperWhisper, we
        encourage you to contact our support team first. Many issues can be
        resolved quickly, and we're here to help you get the most out of your
        purchase.
      </p>

      <h3>Compatibility Concerns</h3>
      <p>
        Before purchasing, please review our system requirements. However, if
        HyperWhisper doesn't work on your system due to compatibility issues,
        you're fully covered by our 14-day guarantee.
      </p>

      <h3>Feature Requests</h3>
      <p>
        While we can't guarantee specific feature implementations, we actively
        consider user feedback for future updates. Your input helps shape
        HyperWhisper's development.
      </p>

      <h2>VII. Exceptions</h2>

      <p>
        Our 14-day money back guarantee applies to all standard purchases. The
        following situations may have different policies:
      </p>

      <ul>
        <li>
          <strong>Promotional or discounted purchases:</strong> Full refund
          available, but promotional pricing may not be reapplied to future
          purchases
        </li>
        <li>
          <strong>Corporate or volume licenses:</strong> May have custom refund
          terms as specified in your agreement
        </li>
      </ul>

      <h3>Cloud Credits Refunds</h3>
      <p>
        Our 14-day money back guarantee applies to your HyperWhisper{" "}
        <strong>license</strong>
        only. It does not apply to any HyperWhisper Cloud usage credits you have
        consumed.
      </p>
      <ul>
        <li>
          <strong>$5 complimentary credits:</strong> When you purchase a
          HyperWhisper license, you are automatically granted{" "}
          <strong>$5</strong>
          of HyperWhisper Cloud credits.
        </li>
        <li>
          <strong>Credits are non-refundable:</strong> Any cloud credits you
          have <em>consumed</em> (including the complimentary $5) are not
          refundable. Eg, if you purchased $5 of HyperWhisper Cloud credits and
          consumed $0.50 of credits, you will be refunded $4.50.
        </li>
      </ul>

      <h2>VIII. Beyond the 14-Day Period</h2>

      <p>
        While our standard guarantee is 14 days, we understand that exceptional
        circumstances may arise. If you have concerns about your purchase beyond
        the 14-day period, please don't hesitate to contact our support team.
        We'll work with you to find a fair solution.
      </p>

      <h2>IX. Multiple Purchases</h2>

      <p>
        If you've made multiple purchases of HyperWhisper (for different devices
        or users), each purchase is eligible for its own 14-day refund period
        from the respective purchase date.
      </p>

      <h2>X. Fraudulent Activity</h2>

      <p>
        We reserve the right to refuse refunds in cases of suspected fraudulent
        activity, including but not limited to:
      </p>

      <ul>
        <li>Repeated purchases and refund requests</li>
        <li>Attempts to obtain multiple refunds for the same license</li>
        <li>Use of stolen payment methods</li>
        <li>Violation of our Terms of Service</li>
      </ul>

      <h2>XI. Contact Information</h2>

      <p>
        For all refund requests and questions about this policy, please contact
        us:
      </p>

      <div className="bg-gray-50 dark:bg-gray-800 rounded-lg p-6 border border-gray-200 dark:border-gray-700">
        <p className="mb-2">
          <strong>Provider:</strong> Ray Amjad LTD (<a className="text-blue-600 dark:text-blue-400 hover:underline" href="https://find-and-update.company-information.service.gov.uk/company/14506459" target="_blank" rel="noopener noreferrer">Company Number 14506459</a>,
          United Kingdom)
        </p>
        <p className="mb-2">
          <strong>Email:</strong>{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="mailto:support@hyperwhisper.com"
          >
            support@hyperwhisper.com
          </a>
        </p>
        <p className="mb-2">
          <strong>Subject Line:</strong> "Refund Request - [Your Order Number]"
        </p>
        <p className="text-sm text-gray-600 dark:text-gray-400">
          We typically respond to refund requests within 1 business day.
        </p>
      </div>

      <h2>XII. Policy Updates</h2>

      <p>
        We may update this refund policy from time to time. Any changes will be
        posted on this page with an updated "Last Updated" date. Significant
        changes will be communicated via email to recent purchasers.
      </p>

      <p>
        Your purchase is governed by the refund policy in effect at the time of
        your purchase.
      </p>

      <div className="bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800 rounded-lg p-6 mt-8">
        <h3 className="text-green-800 dark:text-green-200 mt-0 mb-3">
          <a
            className="hover:underline"
            href="/"
          >
            Ready to try HyperWhisper risk-free?
          </a>
        </h3>
        <p className="text-green-700 dark:text-green-300 mb-0">
          With our 14-day money back guarantee, you can{" "}
          <a
            className="text-green-800 dark:text-green-200 underline hover:no-underline"
            href="/"
          >
            purchase HyperWhisper
          </a>{" "}
          with complete confidence. Experience the power of AI-driven
          speech-to-text transcription with zero risk.
        </p>
      </div>
    </div>
  );
}
