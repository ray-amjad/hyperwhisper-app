import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Terms of Service | HyperWhisper",
  description: "HyperWhisper Terms of Service and User Agreement",
};

export default function TermsOfServicePage() {
  return (
    <div className="prose prose-lg max-w-none dark:prose-invert">
      <p className="text-sm text-gray-600 dark:text-gray-400 italic mb-8">
        Last Updated: February 17, 2026
      </p>
      <h1>Terms of Service</h1>

      <p>
        These Terms are between you and Ray Amjad LTD (<a className="text-blue-600 dark:text-blue-400 hover:underline" href="https://find-and-update.company-information.service.gov.uk/company/14506459" target="_blank" rel="noopener noreferrer">Company Number 14506459</a>,
        incorporated in the United Kingdom). By installing or using
        HyperWhisper, you agree to these Terms. If you do not agree, do not use
        the app.
      </p>

      <h2>License</h2>
      <ul>
        <li>
          <strong>Grant</strong>: We grant you a personal, limited,
          non-transferable, revocable license to use the app on devices you own
          or control.
        </li>
        <li>
          <strong>Restrictions</strong>: You may not resell, sublicense, rent,
          lease, reverse engineer, or circumvent license/usage limits.
        </li>
        <li>
          <strong>Ownership</strong>: The app is licensed, not sold. We retain
          all rights not expressly granted.
        </li>
      </ul>

      <h2>Payments and Refunds</h2>
      <ul>
        <li>
          <strong>Payments</strong>: Purchases are handled by{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://stripe.com"
            target="_blank"
            rel="noopener noreferrer"
          >
            Stripe
          </a>
          . Taxes may apply. Purchases made before December 2025 were processed
          by{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://polar.sh"
            target="_blank"
            rel="noopener noreferrer"
          >
            Polar
          </a>
          .
        </li>
        <li>
          <strong>Refunds</strong>: Our 14-day money-back guarantee applies as
          described in our{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="/legal/refund-policy"
          >
            Refund Policy
          </a>
          .
        </li>
        <li>
          <strong>License after refund</strong>: Refunded licenses may be
          deactivated.
        </li>
      </ul>

      <h2>Privacy and Data</h2>
      <ul>
        <li>
          <strong>Local-first</strong>: Recordings, transcripts, settings, and
          vocabulary are stored on your device. We do not collect them.
        </li>
        <li>
          <strong>Local transcription</strong>: When using local models,
          no audio data ever leaves your device.
        </li>
        <li>
          <strong>Cloud transcription (BYOK)</strong>: When you provide your own
          API key for a third-party provider (e.g., OpenAI, Deepgram, Groq),
          your device connects directly to that provider. We do not proxy or
          store your audio or transcripts.
        </li>
        <li>
          <strong>HyperWhisper Cloud</strong>: When using HyperWhisper Cloud,
          your audio is routed through our edge servers to a third-party
          transcription provider (Deepgram, ElevenLabs, or Groq). We do not
          store your audio or transcripts — we only track credit usage.
        </li>
        <li>
          <strong>What we keep</strong>: We keep only your email, order details
          from our payment provider, and license-related information necessary
          to activate/validate your license.
        </li>
        <li>
          See our{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="/legal/privacy-policy"
          >
            Privacy Policy
          </a>{" "}
          for a full list of providers and links to their privacy policies.
        </li>
      </ul>

      <h2>HyperWhisper Cloud Credits</h2>
      <ul>
        <li>
          <strong>Trial credits</strong>: New devices receive a limited number
          of free credits for trying HyperWhisper Cloud. Trial credits do not
          expire.
        </li>
        <li>
          <strong>Purchased credits</strong>: Licensed users receive
          complimentary credits with their license purchase and may buy
          additional credits. Purchased credits do not expire and are tied to
          your license.
        </li>
        <li>
          <strong>Usage</strong>: Credits are deducted after each successful
          transcription based on the actual cost of the providers used.
        </li>
        <li>
          <strong>Refunds</strong>: Consumed credits are non-refundable. Only
          unused purchased credits may be refunded as described in our{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="/legal/refund-policy"
          >
            Refund Policy
          </a>
          .
        </li>
      </ul>

      <h2>License Activation and Validation</h2>
      <ul>
        <li>
          The app may contact our licensing service to activate, validate, or
          deactivate a license. Internet access may be required periodically.
        </li>
        <li>
          We may deny or revoke licenses obtained fraudulently or used in
          violation of these Terms.
        </li>
      </ul>

      <h2>Fair Usage Policy</h2>
      <ul>
        <li>
          <strong>Device Usage</strong>: You may use your license on as many
          devices as you like, provided such use is reasonable and for your
          personal or business purposes.
        </li>
        <li>
          <strong>Monitoring</strong>: We track device usage to ensure
          compliance with this policy. If we detect usage patterns that appear
          excessive or inconsistent with typical individual use (e.g., use
          across an unreasonable number of devices simultaneously), we may
          contact you to discuss your usage.
        </li>
        <li>
          <strong>Enforcement</strong>: If after discussion we determine that
          usage is unfair or violates these Terms (such as sharing licenses
          commercially or across a large organization), we reserve the right to
          suspend or terminate your license.
        </li>
        <li>
          <strong>Good Faith</strong>: This policy is designed to be flexible
          for legitimate users while preventing abuse. We will always reach out
          before taking any action.
        </li>
      </ul>

      <h2>Acceptable Use</h2>
      <ul>
        <li>
          Do not use the app to violate laws, infringe rights, or distribute
          malicious content.
        </li>
        <li>
          Do not attempt to bypass technical protections or share license keys
          publicly.
        </li>
      </ul>

      <h2>Third‑Party Services</h2>
      <p>
        HyperWhisper integrates with the following third-party services. Your
        use of these services may be subject to their own terms and policies.
      </p>
      <p>
        <strong>Transcription providers</strong> (bring your own API key):{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://openai.com"
          target="_blank"
          rel="noopener noreferrer"
        >
          OpenAI
        </a>
        ,{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://deepgram.com"
          target="_blank"
          rel="noopener noreferrer"
        >
          Deepgram
        </a>
        ,{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://elevenlabs.io"
          target="_blank"
          rel="noopener noreferrer"
        >
          ElevenLabs
        </a>
        ,{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://groq.com"
          target="_blank"
          rel="noopener noreferrer"
        >
          Groq
        </a>
        ,{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://www.assemblyai.com"
          target="_blank"
          rel="noopener noreferrer"
        >
          AssemblyAI
        </a>
        ,{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://mistral.ai"
          target="_blank"
          rel="noopener noreferrer"
        >
          Mistral
        </a>
      </p>
      <p>
        <strong>Post-processing providers</strong> (bring your own API key):{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://openai.com"
          target="_blank"
          rel="noopener noreferrer"
        >
          OpenAI
        </a>
        ,{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://www.anthropic.com"
          target="_blank"
          rel="noopener noreferrer"
        >
          Anthropic
        </a>
        ,{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://ai.google"
          target="_blank"
          rel="noopener noreferrer"
        >
          Google
        </a>
        ,{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://groq.com"
          target="_blank"
          rel="noopener noreferrer"
        >
          Groq
        </a>
        ,{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://cerebras.ai"
          target="_blank"
          rel="noopener noreferrer"
        >
          Cerebras
        </a>
      </p>
      <p>
        <strong>Billing</strong>:{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://stripe.com"
          target="_blank"
          rel="noopener noreferrer"
        >
          Stripe
        </a>
        {" · "}
        <strong>Analytics</strong>:{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://posthog.com"
          target="_blank"
          rel="noopener noreferrer"
        >
          PostHog
        </a>
      </p>
      <ul>
        <li>
          See our{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="/legal/privacy-policy"
          >
            Privacy Policy
          </a>{" "}
          for links to each provider&apos;s privacy policy.
        </li>
        <li>
          We are not responsible for third‑party services and do not control
          their availability or performance.
        </li>
      </ul>

      <h2>Updates</h2>
      <ul>
        <li>
          We may update the app automatically. Updates are part of the service
          and subject to these Terms.
        </li>
        <li>
          We may update these Terms. Continued use after changes means you
          accept the updated Terms.
        </li>
      </ul>

      <h2>Disclaimers</h2>
      <p>
        The app is provided “as is” and “as available” without warranties of any
        kind. To the maximum extent permitted by law, we disclaim all implied
        warranties, including merchantability, fitness for a particular purpose,
        and non‑infringement. We do not warrant that transcription will be
        error‑free or uninterrupted.
      </p>

      <h2>Limitation of Liability</h2>
      <p>
        To the maximum extent permitted by law, we will not be liable for
        indirect, incidental, special, consequential, or punitive damages, or
        any loss of data, use, goodwill, or profits. Our aggregate liability for
        all claims relating to the app will not exceed the amount you paid for
        the app in the 12 months before the event giving rise to the claim.
      </p>

      <h2>Termination</h2>
      <p>
        We may suspend or terminate access to the app (and deactivate licenses)
        if you materially breach these Terms. You may stop using the app at any
        time. Sections that by their nature should survive termination will
        survive.
      </p>

      <h2>Governing Law and Dispute Resolution</h2>
      <p>
        These Terms are governed by the laws of England and Wales, without
        regard to conflict of law principles, unless a different law is required
        by your local consumer protection rules. The courts of England and Wales
        shall have exclusive jurisdiction, except where applicable law provides
        you with non-waivable consumer protections in your place of residence.
      </p>

      <h2>Contact</h2>
      <p>
        Ray Amjad LTD (<a className="text-blue-600 dark:text-blue-400 hover:underline" href="https://find-and-update.company-information.service.gov.uk/company/14506459" target="_blank" rel="noopener noreferrer">Company Number 14506459</a>, United Kingdom). Questions about
        these Terms:{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="mailto:support@hyperwhisper.com"
        >
          support@hyperwhisper.com
        </a>
      </p>
    </div>
  );
}
