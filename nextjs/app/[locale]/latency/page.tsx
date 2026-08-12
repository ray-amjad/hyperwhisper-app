import { notFound } from "next/navigation";

import LatencyMatrix from "@/components/latency/LatencyMatrix";
import { getAllLatencyMatrices } from "@/src/content/latency";
import { DEFAULT_BUCKET, WINDOW_DAYS } from "@/lib/latency/types";

export const dynamic = "force-static";
export const revalidate = 3600;

const TITLE = "Speech-to-text latency, measured";
const DESCRIPTION =
  "How fast each speech-to-text provider actually answers HyperWhisper Cloud, by region, over the last 30 days.";

export async function generateMetadata() {
  return {
    title: TITLE,
    description: DESCRIPTION,
    alternates: {
      canonical: "https://hyperwhisper.com/en/latency",
    },
  };
}

type Props = {
  params: Promise<{ locale: string }>;
};

/**
 * Public latency page. English only, like the blog — the numbers are the point
 * and translating 40 copies of them buys nothing.
 *
 * Static with an hourly revalidate: the aggregate is a 30-day window, so a page
 * up to an hour old tells the same story as a fresh one, and every visitor gets
 * a cached response.
 */
export default async function LatencyPage({ params }: Props) {
  const { locale } = await params;
  if (locale !== "en") {
    notFound();
  }

  const matrices = await getAllLatencyMatrices();
  const totalSamples = Object.values(matrices).reduce(
    (sum, matrix) => sum + matrix.totalSamples,
    0,
  );

  return (
    <div className="w-full py-16 md:py-24">
      <header className="mx-auto max-w-3xl text-center">
        <p className="mx-auto text-lg text-gray-400">
          Every transcription we run times the provider that answered it. These
          are those timings — {totalSamples.toLocaleString()} provider attempts
          over the last {WINDOW_DAYS} days, grouped by the region that made the
          call.
        </p>
      </header>

      <LatencyMatrix defaultBucket={DEFAULT_BUCKET} matrices={matrices} />

      <section className="mx-auto mt-16 max-w-3xl border-t border-gray-800 pt-10">
        <h2 className="text-xl font-semibold text-white">How this is measured</h2>
        <dl className="mt-6 space-y-6 text-sm leading-6 text-gray-400">
          <div>
            <dt className="font-medium text-gray-200">What the number is</dt>
            <dd className="mt-1">
              The time one provider took to answer one call, measured at the edge
              machine that made it — everything that attempt spent, including
              handing the audio to the provider and waiting on a long-running
              job. Your upload to us, authentication, and our own credit checks
              are excluded. So is the network between you and us — this is the
              provider&apos;s time, not your round trip.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">Failed attempts count</dt>
            <dd className="mt-1">
              A provider that times out still spent your time, so its attempt is
              in the latency numbers. When one provider fails we fall back to
              another, and both attempts are recorded separately. The error-rate
              metric shows how often that happens. What is never counted is a
              request we turn away ourselves before calling anyone — audio past
              a provider&apos;s size cap, or in a format it does not accept:
              that provider never received the call, so it is not charged for it
              here.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">
              A row is a provider, not a model
            </dt>
            <dd className="mt-1">
              Most of these providers offer several models, and HyperWhisper
              lets you pin the one you want. A row covers whichever model of
              that provider actually ran — its default for most people, plus
              whatever anyone else picked — so read a row as &ldquo;how fast
              this provider answers us&rdquo;, not as a benchmark of one named
              model against another.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">Why clip length matters</dt>
            <dd className="mt-1">
              Longer audio takes longer to transcribe, so comparing a provider
              handed 5-second clips against one handed 5-minute files would say
              nothing. Every cell compares clips of similar length, grouped by
              an estimate taken from the audio&apos;s size and format — the same
              estimate for every provider, so no cell is flattered by a provider
              that measures its own audio differently.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">What it covers</dt>
            <dd className="mt-1">
              The batch transcription endpoint. Live streaming transcription is
              not measured here.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">What we store</dt>
            <dd className="mt-1">
              A timing row carries the provider, the model, the region, which
              clip-length group the audio fell in, which attempt in the chain it
              was, and whether the call worked. It carries no account, no key,
              no request id, and no audio or text. The clip&apos;s own length is
              not kept, and the timestamp is stored only to the hour, so nothing
              ties a row to a person or reassembles one transcription.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">How to opt out</dt>
            <dd className="mt-1">
              In the HyperWhisper app, turn off{" "}
              <span className="text-gray-200">Share anonymous speed data</span>{" "}
              under Settings → General. Your transcriptions work exactly the
              same — the same providers, the same speed, the same result — we
              just stop keeping the timing. Calling the API yourself? Send{" "}
              <code className="rounded bg-gray-900 px-1.5 py-0.5 text-xs text-gray-300">
                X-Latency-Opt-Out: 1
              </code>
              . Local models never send a timing at all.
            </dd>
          </div>
        </dl>
      </section>
    </div>
  );
}