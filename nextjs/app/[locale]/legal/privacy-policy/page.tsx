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
        Last Updated: September 25, 2026
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
          troubleshooting. We do not store the message body. When you request
          the download link from our website, we record the email address you
          submit so we can send it. We use the IP address of that request only
          to rate limit the form (10 requests per IP address per hour) and we
          do not store the IP address, browser user agent, or country with the
          record.
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
          <strong>Crash and performance diagnostics</strong>: The HyperWhisper
          desktop apps for macOS, Windows, and Linux send diagnostics to{" "}
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://sentry.io/privacy/"
            target="_blank"
            rel="noopener noreferrer"
          >
            Sentry
          </a>
          , our error-monitoring provider. This is on by default. You can turn
          it off in Settings → General with the switch labelled &quot;Error
          logging&quot; on macOS and &quot;Send error reports&quot; on Windows
          and Linux. When you turn it off, the app stops collecting new
          diagnostics. On Windows and Linux, reports that are already waiting on
          your device can still be sent at that moment, and on macOS one final
          session record can be sent. The diagnostics include:
          <ul>
            <li>
              Crash and error reports with technical details about the error,
              and other diagnostic events and records about how the app is
              running. On macOS, this includes routine diagnostic log records
              that are sent even when nothing has gone wrong.
            </li>
            <li>
              Release-health session data: records of app sessions and whether
              they ended in a crash.
            </li>
            <li>
              Performance traces (timings of operations in the app) on macOS and
              Windows, and CPU profiles (samples of which parts of the
              app&apos;s code were running) on macOS. The Linux app does not
              currently record performance traces.
            </li>
            <li>
              Identifiers: your IP address, the app version, your operating
              system version, your CPU architecture, and an identifier for your
              installation of the app that stays the same from one launch to the
              next and is linked to your sessions. On Windows and Linux, this
              also includes your computer&apos;s name, and on Linux the name of
              your user account on it.
            </li>
            <li>
              Context about what the app was doing. For example, this can
              include the names of your audio input devices and the name of the
              app you were dictating into with the number of characters pasted
              (macOS and Windows), and file paths on your computer. On macOS, a
              file path can contain the name of your user account.
            </li>
          </ul>
          No audio is attached to a report, so a report cannot give us your
          recordings. Report fields named for transcripts, text, or prompts are
          replaced with &quot;[redacted]&quot; before the report is sent. On
          macOS, an error report can also include recent lines from the
          app&apos;s own log. Those lines are filtered to remove some personal
          details, such as home-folder paths, email addresses, IPv4 addresses,
          and some text that looks like transcript content.
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
        <li>
          <a
            className="text-blue-600 dark:text-blue-400 hover:underline"
            href="https://dev.meta.ai/docs/speech-to-text/"
            target="_blank"
            rel="noopener noreferrer"
          >
            Meta Muse Voice Transcribe
          </a>
          . A direct Meta request goes from your device to api.meta.ai and does
          not pass through HyperWhisper servers.
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
          href="https://github.com/ray-amjad/hyperwhisper-app/tree/main/hyperwhisper-cloud"
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

      <h3>Medical Mode</h3>
      <p>
        With HyperWhisper Cloud, a mode that uses AssemblyAI can be set to the{" "}
        <strong>medical</strong> Transcription Domain. Our edge servers then
        send the audio to AssemblyAI with its Medical Mode add-on turned on.
        AssemblyAI processes it on HyperWhisper Cloud&apos;s own AssemblyAI
        account, under{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://www.assemblyai.com/legal/privacy-policy"
          target="_blank"
          rel="noopener noreferrer"
        >
          AssemblyAI&apos;s privacy policy
        </a>
        , and we do not promise zero data retention there. A HyperWhisper
        Cloud mode can also use Deepgram&apos;s Nova-3 Medical or Nova-2
        Medical model. These options tune transcription for clinical
        vocabulary. For these medical requests, our edge servers do not write
        the audio or the transcript to our own storage. If Deepgram cannot
        take a request,
        HyperWhisper Cloud can send it to Groq or ElevenLabs as described
        above, and they do not use a medical model. Live streaming through
        HyperWhisper Cloud does not use a medical model. If you choose Nova 3
        Medical for streaming with your own Deepgram API key, your audio goes
        from your device directly to Deepgram. If post-processing is on, the
        transcript also goes to the post-processing provider you use. Each
        provider processes your data under its own terms, and you must review
        them before you dictate about a patient.
      </p>
      <p>
        Medical Mode is a vocabulary and formatting feature only. We are not a
        healthcare provider, and we do not sign a Business Associate Agreement
        (BAA). HyperWhisper, including HyperWhisper Cloud and Medical Mode, is
        not offered as HIPAA-compliant or as fit for regulated clinical record
        keeping.
      </p>

      <h3>Screen Text and app context</h3>
      <p>
        The post-processing request sent to the provider you choose
        (HyperWhisper Cloud, a bring-your-own-key provider, or a custom
        endpoint you add) carries more than your transcribed text. It includes your
        computer&apos;s name, local time, time zone and locale. It can also
        include the name of the app in front, the window or browser tab title,
        the website host, the type and label of the focused field, and up to
        100 characters of the text in that field or of the text you have
        selected. A mode can also turn on <strong>Screen Text</strong>,
        which is off by default in every mode. When a recording starts, the app
        reads the text on the display that shows your active window (on macOS,
        the main display when it cannot find that window; on Linux, the area
        you select in the screenshot prompt) and adds up to 2,000
        characters of it to the request. Text recognition runs on your device:
        the screenshot itself is never sent, only the recognized text. On macOS
        and Windows, no screenshot is taken when HyperWhisper itself is the app
        in front. When HyperWhisper identifies the app in front as sensitive,
        such as a password manager, the screen text and the field or selected text
        are left out, but the app name, the window or tab title, the website host
        and the field type and label are still sent. When post-processing is
        off, none of this is sent to a post-processing provider.
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
      <p>
        The desktop apps use{" "}
        <a
          className="text-blue-600 dark:text-blue-400 hover:underline"
          href="https://sentry.io/privacy/"
          target="_blank"
          rel="noopener noreferrer"
        >
          Sentry
        </a>
        , our error-monitoring provider, for crash and performance diagnostics,
        as described under What We Collect. You can turn it off in Settings →
        General.
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
      <p>
        If you dictated patient information with the Medical Mode options
        described above, we hold neither the audio nor the transcript, so
        delete them on your device as described above. Any copy a provider keeps is governed by
        that provider&apos;s terms.
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
          href="mailto:hi@support.hyperwhisper.com"
        >
          hi@support.hyperwhisper.com
        </a>
      </p>
    </div>
  );
}
