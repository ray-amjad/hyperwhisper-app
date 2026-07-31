import { Metadata } from "next";

const GITHUB_URL = "https://github.com/ray-amjad/hyperwhisper-app";
const LICENSE_URL =
  "https://github.com/ray-amjad/hyperwhisper-app/blob/main/LICENSE";
const SUPPORT_EMAIL = "support@hyperwhisper.com";
const REFUND_MAILTO = `mailto:${SUPPORT_EMAIL}?subject=${encodeURIComponent(
  "HyperWhisper — refund request"
)}`;
const CREDITS_MAILTO = `mailto:${SUPPORT_EMAIL}?subject=${encodeURIComponent(
  "HyperWhisper — convert my license to Cloud credits"
)}`;

export const metadata: Metadata = {
  title: "Open Source | HyperWhisper",
  description:
    "HyperWhisper is fully open source under Apache-2.0 — desktop apps and Cloud backend alike. Read the code, audit where your audio goes, fork it, and self-host forever.",
};

export default function OpenSourcePage() {
  return (
    <article className="mx-auto w-full max-w-4xl pt-16 pb-24 md:pt-24">
      {/* Header */}
      <div className="space-y-5 text-center">
        <p className="text-sm uppercase tracking-[0.3em] text-purple-300">
          Open Source
        </p>
        <h1 className="mx-auto max-w-3xl text-4xl font-semibold leading-tight text-white md:text-5xl">
          Open source. Nothing to hide.
        </h1>
        <p className="mx-auto max-w-2xl text-lg leading-8 text-gray-300">
          HyperWhisper is now fully open source under Apache-2.0 — the desktop
          apps and the Cloud backend alike.
        </p>
      </div>

      {/* Body */}
      <div className="prose prose-invert mx-auto mt-12 max-w-3xl prose-headings:text-white prose-a:text-purple-300 prose-a:no-underline hover:prose-a:text-purple-200 prose-strong:text-white">
        <p>
          An app that listens to your microphone should be something you can
          verify, not just trust. So we opened everything up. You can read every
          line of HyperWhisper — the macOS and Windows apps, and the Cloud
          transcription backend — see exactly where your audio goes, and know
          you&apos;ll never be locked in. The whole project lives on{" "}
          <a href={GITHUB_URL} target="_blank" rel="noopener noreferrer">
            GitHub
          </a>{" "}
          under the{" "}
          <a href={LICENSE_URL} target="_blank" rel="noopener noreferrer">
            Apache-2.0 license
          </a>
          .
        </p>

        <h2>Why it matters</h2>
        <p>
          <strong>Verify, don&apos;t trust.</strong> Read every line and audit
          exactly where your audio goes. An app with microphone access should be
          something you can inspect, not just take on faith.
        </p>
        <p>
          <strong>No lock-in.</strong> The code is Apache-2.0 — yours to keep,
          adapt, and run on your own terms. Your workflow is never held hostage.
        </p>
        <p>
          <strong>Fork and self-host.</strong> Clone the repo, build it
          yourself, or run your own Cloud backend. Everything you need is
          public.
        </p>
        <p>
          <strong>Built in the open.</strong> HyperWhisper is developed publicly
          by indie maker Ray Amjad. Issues, discussions, and releases all happen
          out in the open.
        </p>

        <h2>Already bought a license?</h2>
        <p>
          HyperWhisper is now open source, and paid transcription runs on
          HyperWhisper Cloud credits. If you bought a license before the switch,
          here&apos;s how we&apos;re taking care of you — just email{" "}
          <a href={`mailto:${SUPPORT_EMAIL}`}>{SUPPORT_EMAIL}</a> and we&apos;ll
          sort it out by hand.
        </p>
        <ul>
          <li>
            <strong>Bought in the last 30 days:</strong> you&apos;re inside the
            refund window. We&apos;ll refund your purchase in full — no questions
            asked. Just{" "}
            <a href={REFUND_MAILTO}>send us a note</a>.
          </li>
          <li>
            <strong>Bought more than 30 days ago:</strong> we&apos;ll convert
            what you paid into HyperWhisper Cloud credits — the full purchase
            price, minus the $5 in credits already added to your account.{" "}
            <a href={CREDITS_MAILTO}>Email us</a> and we&apos;ll apply the
            balance.
          </li>
        </ul>

        <h2>Want to contribute?</h2>
        <p>
          Bug reports, feature ideas, and pull requests are all welcome. Open an
          issue or start a discussion{" "}
          <a href={GITHUB_URL} target="_blank" rel="noopener noreferrer">
            on GitHub
          </a>
          .
        </p>
      </div>
    </article>
  );
}
