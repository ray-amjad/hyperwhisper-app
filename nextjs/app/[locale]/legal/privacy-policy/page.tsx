import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Privacy Policy | HyperWhisper",
  description:
    "HyperWhisper Privacy Policy - Local-first, no cloud storage of your audio or transcripts.",
};

export default function PrivacyPolicyPage() {
  return (
    <div className="prose prose-lg max-w-none dark:prose-invert">
      <p className="text-sm text-gray-600 dark:text-gray-400 italic mb-8">
        Last Updated: June 30, 2026
      </p>

      <h1>Privacy Policy</h1>

      <p>
        HyperWhisper is built to keep your data on your device. You can
        transcribe entirely offline using local AI models, or choose from a
        range of cloud providers — including HyperWhisper Cloud, our built-in
        cloud service. Whichever option you choose, we never store your audio
        recordings or transcripts.
      </p>
      <p>
        Ray Amjad LTD (
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://find-and-update.company-information.service.gov.uk/company/14506459"
          target="_blank"
          rel="noopener noreferrer"
        >
          Company Number 14506459
        </a>
        , incorporated in the United Kingdom) is the provider of HyperWhisper
        and acts as the data controller for the limited personal data we handle.
      </p>

      <h2>What We Collect</h2>
      <ul>
        <li>
          <strong>Email</strong>: Used to deliver receipts, licenses, and
          support. We keep a record of the transactional emails we send you —
          the recipient address, email type, subject, send timestamp, and
          delivery status — retained for support, audit, and deliverability
          troubleshooting. We do not store the message body.
        </li>
        <li>
          <strong>Order and billing info</strong>: Processed by our payment
          provider (
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://stripe.com"
            target="_blank"
            rel="noopener noreferrer"
          >
            Stripe
          </a>
          ) for purchases, refunds, and fraud prevention.
        </li>
        <li>
          <strong>License information</strong>: The app contacts our licensing
          service to activate and validate your license. During this process, we
          receive your Account Key, a SHA-256 hash of your device&apos;s
          hardware identifier (we never receive the raw identifier), and your
          device&apos;s hostname. This information is used to enforce our fair
          usage policy. No audio or transcripts are transmitted.
        </li>
      </ul>

      <p className="font-semibold">
        We do not store your audio recordings or transcripts.
      </p>

      <h2>What Stays On Your Device</h2>
      <ul>
        <li>
          <strong>Recordings</strong>: Your audio stays on your device unless
          you export or share it.
        </li>
        <li>
          <strong>Transcripts</strong>: Stored locally on your device; you
          control them.
        </li>
        <li>
          <strong>Settings and vocabulary</strong>: App preferences and optional
          custom vocabulary are stored locally.
        </li>
      </ul>

      <h2>Transcription Processing</h2>
      <p>
        HyperWhisper supports both local (on-device) and cloud-based
        transcription. When using local models, no audio data ever leaves your
        device.
      </p>
      <p>
        When using cloud transcription, your audio is sent directly from your
        device to the cloud provider you choose. HyperWhisper does not proxy,
        store, or retain your audio or transcripts on our servers. The cloud
        provider you select may store or process your data according to their
        own privacy policies. You are responsible for reviewing those policies
        before use.
      </p>
      <p>
        <strong>Third-party transcription providers</strong> (bring your own API
        key):
      </p>
      <ul>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://openai.com/policies/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            OpenAI
          </a>
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://deepgram.com/privacy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Deepgram
          </a>
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://elevenlabs.io/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            ElevenLabs
          </a>
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://groq.com/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Groq
          </a>
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://www.assemblyai.com/legal/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            AssemblyAI
          </a>
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://mistral.ai/terms/#privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Mistral
          </a>
        </li>
      </ul>
      <p>
        <strong>Third-party post-processing providers</strong> (bring your own
        API key) — these services receive your transcribed text for correction
        and formatting:
      </p>
      <ul>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://openai.com/policies/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            OpenAI
          </a>
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://www.anthropic.com/privacy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Anthropic (Claude)
          </a>
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://policies.google.com/privacy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Google (Gemini)
          </a>
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://groq.com/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Groq
          </a>
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://cerebras.ai/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Cerebras
          </a>
        </li>
      </ul>
      <h3>HyperWhisper Cloud</h3>
      <p>
        <strong>HyperWhisper Cloud</strong> is our built-in cloud transcription
        service. It routes your audio through our edge servers to one of the
        providers listed below. We do not store your audio or transcripts — we
        only track credit usage.
      </p>
      <p>
        <strong>Transcription (speech-to-text) providers:</strong>
      </p>
      <ul>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://deepgram.com/privacy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Deepgram
          </a>{" "}
          (Nova-3) — default provider. Also used as a fallback when Groq is
          unavailable in certain regions.
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://groq.com/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Groq
          </a>{" "}
          (Whisper large-v3) — fastest option. Falls back to Deepgram if
          blocked in your region.
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://elevenlabs.io/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            ElevenLabs
          </a>{" "}
          (Scribe v2) — highest accuracy option.
        </li>
      </ul>
      <p>
        <strong>Post-processing (text correction) providers:</strong>
      </p>
      <ul>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://www.cerebras.ai/policies"
            target="_blank"
            rel="noopener noreferrer"
          >
            Cerebras
          </a>{" "}
          (GPT-OSS-120B) — default provider. Falls back to Groq on failure.
        </li>
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://groq.com/privacy-policy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Groq
          </a>{" "}
          (GPT-OSS-120B) — fallback provider. Falls back to Cerebras on
          failure.
        </li>
      </ul>
      <p>
        Where available, we have configured zero data retention on our
        provider accounts — for example, Deepgram&apos;s data retention is set
        to zero so that audio is deleted immediately after transcription.
        Tracking, voice data storage, and any optional data-sharing settings
        have been disabled on all provider accounts that offer those controls.
        However, we are not on enterprise plans with these providers, so their
        standard data processing terms still apply. Please review each
        provider&apos;s privacy policy linked above for full details on how they
        handle data.
      </p>
      <p>
        HyperWhisper is fully open source under Apache-2.0 — including the Cloud
        backend. You can read the full source code for the{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://github.com/ray-amjad/hyperwhisper-fly"
          target="_blank"
          rel="noopener noreferrer"
        >
          Cloud backend
        </a>{" "}
        and the{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://github.com/ray-amjad/hyperwhisper-app"
          target="_blank"
          rel="noopener noreferrer"
        >
          apps
        </a>{" "}
        on GitHub.
      </p>

      <h2>Payments and Licensing</h2>
      <ul>
        <li>
          <strong>Payments</strong>: All payments are handled by{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://stripe.com"
            target="_blank"
            rel="noopener noreferrer"
          >
            Stripe
          </a>
          . We receive the minimum order metadata required to fulfill your
          purchase and provide support. Please review the{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://stripe.com/privacy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Stripe Privacy Policy
          </a>{" "}
          for details on how they handle payment data.
        </li>
        <li>
          <strong>Past payments (before December 2025)</strong>: Payments made
          before December 2025 were processed by{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://polar.sh"
            target="_blank"
            rel="noopener noreferrer"
          >
            Polar
          </a>{" "}
          and their processors (e.g., Stripe). If you purchased through Polar,
          your order data may still be retained by them. See the{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://polar.sh/legal/privacy"
            target="_blank"
            rel="noopener noreferrer"
          >
            Polar Privacy Policy
          </a>{" "}
          for details.
        </li>
        <li>
          <strong>Licensing</strong>: License activation/validation may send
          your Account Key and minimal device information to our licensing
          service to prevent abuse. No audio or transcripts are transmitted.
        </li>
      </ul>

      <h2>Cookies and Analytics</h2>
      <p>
        The app does not set tracking cookies. Our website uses strictly
        necessary cookies for authentication (when you log in to your account to
        manage your subscription or add credits), checkout, and license
        management. We do not use advertising cookies.
      </p>
      <p>
        We use{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://posthog.com/privacy"
          target="_blank"
          rel="noopener noreferrer"
        >
          PostHog
        </a>{" "}
        for analytics to understand how our website is used and to improve the
        experience. PostHog may set its own cookies. We do not use this data for
        advertising purposes.
      </p>

      <h2>Children&apos;s Privacy</h2>
      <p>
        HyperWhisper is not directed to children under 16. We do not knowingly
        collect personal information from children. If you believe a child has
        provided us information, contact us to delete it.
      </p>

      <h2>Your Rights</h2>
      <p>
        Because we do not store your audio or transcripts, requests to access or
        delete that data should be carried out on your device by you. For email,
        order, or license information we maintain, you may request access or
        deletion by contacting support. Some information must be retained for
        legal/accounting purposes.
      </p>

      <h2>International Transfers</h2>
      <p>
        Our payment and licensing providers may process limited personal
        information in multiple countries, including the United Kingdom, the
        European Economic Area, and the United States. Where required,
        appropriate safeguards are used by those providers (for example,
        adequacy decisions, Standard Contractual Clauses, or equivalent
        mechanisms).
      </p>

      <h2>Changes to This Policy</h2>
      <p>
        We may update this policy. If we make material changes, we will update
        the date above and, where appropriate, notify recent purchasers.
      </p>

      <h2>Contact</h2>
      <p>
        Data Controller: Ray Amjad LTD (
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://find-and-update.company-information.service.gov.uk/company/14506459"
          target="_blank"
          rel="noopener noreferrer"
        >
          Company Number 14506459
        </a>
        , United Kingdom). Questions or requests:{" "}
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
